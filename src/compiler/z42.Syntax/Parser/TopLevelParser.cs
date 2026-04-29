using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser.Core;

namespace Z42.Syntax.Parser;

// ── Grammar (top-level declarations — z42 Phase 1, C#-compatible subset) ─────
//
// compilation_unit  := namespace_decl? using_decl* top_item*
// top_item          := class_decl | func_decl | enum_decl | interface_decl
//
// class_decl     := visibility? modifier* 'record'? ('class'|'struct') IDENT
//                   ('<' type_params '>')?  (':' base_list)?
//                   '{' (field_decl | func_decl)* '}'
//                 | visibility? modifier* 'record' IDENT '(' param_list ')' ';'?
// field_decl     := visibility? 'static'? type IDENT ('=' expr)? ';'
//                 | visibility? 'static'? type IDENT '{' auto_prop_body '}'
// func_decl      := visibility? modifier* type IDENT '(' param_list ')' block
//                 | visibility? modifier* IDENT      '(' param_list ')' block  // ctor
//                 | visibility? 'abstract' type IDENT '(' param_list ')' ';'
// param_list     := (type IDENT (',' type IDENT)*)?
// enum_decl      := visibility? 'enum' IDENT '{' enum_member (',' enum_member)* ','? '}'
// interface_decl := visibility? modifier* 'interface' IDENT (':' name_list)?
//                   '{' method_sig* '}'
// ─────────────────────────────────────────────────────────────────────────────

/// Top-level parser: compilation unit, class/struct/record, function, enum, interface.
/// Lookahead helpers use TokenCursor.SkipWhile() instead of manual _pos+i arithmetic.
///
/// 拆分为多个 partial 文件：
/// • TopLevelParser.cs          — CompilationUnit 入口 + 函数声明
/// • TopLevelParser.Types.cs    — enum / interface / class / struct / record / impl 声明
/// • TopLevelParser.Helpers.cs  — 通用 helper（visibility / modifier / generic / attribute / lookahead / expect）
/// • TopLevelParser.Members.cs  — 类型成员特殊 desugar（indexer / auto-property）
internal static partial class TopLevelParser
{
    // ── Compilation unit ──────────────────────────────────────────────────────

    internal static CompilationUnit ParseCompilationUnit(
        TokenCursor cursor, LanguageFeatures feat, DiagnosticBag? diags = null)
    {
        var start = cursor.Current.Span;
        string? ns = null;

        if (cursor.Current.Kind == TokenKind.Namespace)
        {
            cursor = cursor.Advance();
            ns     = ParseQualifiedName(ref cursor);
            if (cursor.Current.Kind == TokenKind.Semicolon) cursor = cursor.Advance();
        }

        var usings = new List<string>();
        while (cursor.Current.Kind == TokenKind.Using)
        {
            cursor = cursor.Advance();
            usings.Add(ParseQualifiedName(ref cursor));
            if (cursor.Current.Kind == TokenKind.Semicolon) cursor = cursor.Advance();
        }

        var classes       = new List<ClassDecl>();
        var functions     = new List<FunctionDecl>();
        var enums         = new List<EnumDecl>();
        var interfaces    = new List<InterfaceDecl>();
        var impls         = new List<ImplDecl>();
        var nativeImports = new List<NativeTypeImport>();

        NativeAttribute?      pendingNative    = null;
        List<TestAttribute>?  pendingTestAttrs = null;  // R1: accumulated z42.test.* attrs
        while (!cursor.IsEnd)
        {
            try
            {
                if (IsPhase2ReservedKeyword(cursor.Current.Kind))
                    throw new ParseException(
                        $"`{cursor.Current.Text}` is a keyword reserved for Phase 2 and cannot be used yet",
                        cursor.Current.Span,
                        DiagnosticCodes.FeatureDisabled);
                if (cursor.Current.Kind == TokenKind.LBracket)
                {
                    var (parsedNative, parsedTest) = TryParseAttribute(ref cursor);
                    if (parsedNative != null) pendingNative = parsedNative;
                    if (parsedTest   != null) (pendingTestAttrs ??= new()).Add(parsedTest);
                    continue;
                }
                if (cursor.Current.Kind == TokenKind.Namespace)
                {
                    cursor = cursor.Advance();
                    ParseQualifiedName(ref cursor);
                    if (cursor.Current.Kind == TokenKind.Semicolon) cursor = cursor.Advance();
                    continue;
                }
                if (cursor.Current.Kind == TokenKind.Using)
                {
                    cursor = cursor.Advance();
                    ParseQualifiedName(ref cursor);
                    if (cursor.Current.Kind == TokenKind.Semicolon) cursor = cursor.Advance();
                    continue;
                }
                if (cursor.Current.Kind == TokenKind.Import)
                {
                    nativeImports.Add(ParseNativeTypeImport(ref cursor));
                    continue;
                }
                if (IsEnumDecl(cursor))          { pendingNative = null; pendingTestAttrs = null; enums.Add(ParseEnumDecl(ref cursor, diags)); continue; }
                if (IsInterfaceDecl(cursor))      { pendingNative = null; pendingTestAttrs = null; interfaces.Add(ParseInterfaceDecl(ref cursor, feat)); continue; }
                if (IsClassOrStructDecl(cursor))  {
                    // Spec C9: thread the class-level [Native(lib=, type=)]
                    // attribute (if present) into ClassDecl so its methods
                    // can stitch defaults at codegen time.
                    var classNative = pendingNative;
                    pendingNative    = null;
                    pendingTestAttrs = null;  // class-level test attrs not supported (R4 will diagnose)
                    classes.Add(ParseClassDecl(ref cursor, feat, diags, classNative));
                    continue;
                }
                if (cursor.Current.Kind == TokenKind.Impl)
                                                  { pendingNative = null; pendingTestAttrs = null; impls.Add(ParseImplDecl(ref cursor, feat, diags)); continue; }
                functions.Add(ParseFunctionDecl(ref cursor, feat, Visibility.Internal, pendingNative, diags, pendingTestAttrs));
                pendingNative    = null;
                pendingTestAttrs = null;
            }
            catch (ParseException ex) when (diags != null)
            {
                diags.Error(ex.Code ?? DiagnosticCodes.UnexpectedToken, ex.Message, ex.Span);
                pendingNative    = null;
                pendingTestAttrs = null;
                cursor = SkipToNextDeclaration(cursor);
            }
        }

        return new CompilationUnit(
            ns, usings, classes, functions, enums, interfaces, impls, start,
            NativeImports: nativeImports.Count == 0 ? null : nativeImports);
    }

    /// `import IDENT from "<lib>";` — spec C11a manifest-driven native import.
    /// `from` is contextual (matched as Identifier with text == "from") so it
    /// does not pollute the keyword namespace.
    private static NativeTypeImport ParseNativeTypeImport(ref TokenCursor cursor)
    {
        var start = cursor.Current.Span;            // `import` token span
        cursor = cursor.Advance();                  // consume `import`

        var nameTok = ExpectKind(ref cursor, TokenKind.Identifier);
        var name    = nameTok.Text;

        if (cursor.Current.Kind != TokenKind.Identifier || cursor.Current.Text != "from")
            throw new ParseException(
                $"expected `from` after import name, got `{cursor.Current.Text}`",
                cursor.Current.Span,
                DiagnosticCodes.ExpectedToken);
        cursor = cursor.Advance();                  // consume `from`

        var libTok = ExpectKind(ref cursor, TokenKind.StringLiteral);
        var libRaw = libTok.Text;
        var libName = libRaw.Length >= 2 ? libRaw[1..^1] : libRaw;

        ExpectKind(ref cursor, TokenKind.Semicolon);
        return new NativeTypeImport(name, libName, start);
    }

    /// Skip tokens until we reach a plausible start of a new top-level declaration.
    private static TokenCursor SkipToNextDeclaration(TokenCursor cursor)
    {
        while (!cursor.IsEnd)
        {
            var kind = cursor.Current.Kind;
            // Stop at tokens that start a new top-level declaration
            if (kind is TokenKind.Class or TokenKind.Struct or TokenKind.Enum
                or TokenKind.Interface or TokenKind.Impl or TokenKind.Namespace or TokenKind.Using
                or TokenKind.Import
                or TokenKind.Public or TokenKind.Private or TokenKind.Internal
                or TokenKind.Protected or TokenKind.Abstract or TokenKind.Sealed
                or TokenKind.Static or TokenKind.Virtual or TokenKind.Override
                or TokenKind.LBracket)
                break;
            // Stop at type keywords that could start a function: `void Main()`, `int Foo()`
            if (TokenDefs.TypeKeywords.Contains(kind))
                break;
            // Stop after `}` (end of previous declaration)
            if (kind == TokenKind.RBrace) { cursor = cursor.Advance(); break; }
            cursor = cursor.Advance();
        }
        return cursor;
    }

    // ── Function declaration ──────────────────────────────────────────────────

    internal static FunctionDecl ParseFunctionDecl(
        ref TokenCursor cursor, LanguageFeatures feat, Visibility defaultVis,
        NativeAttribute? nativeAttr = null, DiagnosticBag? diags = null,
        List<TestAttribute>? testAttrs = null)
    {
        var nativeIntrinsic = nativeAttr?.Intrinsic;
        var tier1Binding    = nativeAttr?.Tier1;
        var start = cursor.Current.Span;
        var vis   = ParseVisibility(ref cursor, defaultVis);
        var (isStatic, isVirtual, isOverride, isAbstract, _, isExtern) =
            ParseNonVisibilityModifiers(ref cursor);

        // Constructor pattern: Ident( — no explicit return type
        TypeExpr returnType;
        string   name;
        List<string>? funcTypeParams = null;
        if (cursor.Current.Kind == TokenKind.Identifier
            && cursor.Peek(1).Kind == TokenKind.LParen)
        {
            returnType = new VoidType(cursor.Current.Span);
            name       = cursor.Current.Text;
            cursor     = cursor.Advance();
        }
        else
        {
            returnType = TypeParser.Parse(cursor).Unwrap(ref cursor);
            // L3 operator overload: `public static T operator +(T a, T b) { ... }`.
            // The `operator` keyword stands in for the method name; the following token
            // is the operator symbol, mangled into an `op_*` method name.
            if (cursor.Current.Kind == TokenKind.Operator)
            {
                cursor = cursor.Advance(); // consume 'operator'
                name   = ParseOperatorSymbolAsMethodName(ref cursor);
            }
            else
            {
                name = ExpectKind(ref cursor, TokenKind.Identifier).Text;
                // Generic function: T Max<T>(T a, T b)
                funcTypeParams = ParseTypeParams(ref cursor);
            }
        }

        // Extern property: `extern T Name { get; }` — desugars to `extern T get_Name()`.
        // Setter (`set;`) on extern property currently rejected (no stdlib use case;
        // VM intrinsics are uniformly read-only properties).
        if (isExtern && cursor.Current.Kind == TokenKind.LBrace)
        {
            var (_, hasSet) = ParseAutoPropAccessors(ref cursor, vis, start);
            if (hasSet)
                throw new ParseException(
                    "extern property setter is not supported (only `extern T Name { get; }`)",
                    start, DiagnosticCodes.UnexpectedToken);
            return new FunctionDecl($"get_{name}", [], returnType,
                new BlockStmt([], start), vis,
                BuildModifiers(isStatic, isVirtual, isOverride, isAbstract, isExtern),
                nativeIntrinsic, start,
                Tier1Binding: tier1Binding);
        }

        var parms = ParseParamList(ref cursor, feat);

        // Constructor initializer: ClassName(...) : base(args)
        List<Expr>? baseCtorArgs = null;
        bool isCtor = returnType is VoidType && cursor.Current.Kind == TokenKind.Colon;
        if (isCtor && cursor.Peek(1) is { Kind: TokenKind.Identifier, Text: "base" })
        {
            cursor = cursor.Advance(); // skip ':'
            cursor = cursor.Advance(); // skip 'base'
            ExpectKind(ref cursor, TokenKind.LParen);
            baseCtorArgs = [];
            while (cursor.Current.Kind != TokenKind.RParen && !cursor.IsEnd)
            {
                baseCtorArgs.Add(ExprParser.Parse(cursor, feat).Unwrap(ref cursor));
                if (cursor.Current.Kind != TokenKind.Comma) break;
                cursor = cursor.Advance();
            }
            ExpectKind(ref cursor, TokenKind.RParen);
        }

        // Where clause: T Max<T>(...) where T: I + J { ... }
        var whereClause = ParseWhereClause(ref cursor);

        // Abstract / extern methods: no body — semicolon
        BlockStmt body;
        if ((isAbstract || isExtern) && cursor.Current.Kind == TokenKind.Semicolon)
        {
            cursor = cursor.Advance();
            body   = new BlockStmt([], start);
        }
        else if (cursor.Current.Kind == TokenKind.FatArrow)
        {
            // Expression body: `=> expr;`
            cursor = cursor.Advance();  // skip =>
            var expr = ExprParser.Parse(cursor, feat).Unwrap(ref cursor);
            ExpectKind(ref cursor, TokenKind.Semicolon);
            Stmt stmt = returnType is VoidType
                ? new ExprStmt(expr, expr.Span)
                : new ReturnStmt(expr, expr.Span);
            body = new BlockStmt([stmt], start);
        }
        else
        {
            body = StmtParser.ParseBlock(cursor, feat, diags).Unwrap(ref cursor);
        }

        return new FunctionDecl(name, parms, returnType, body, vis,
            BuildModifiers(isStatic, isVirtual, isOverride, isAbstract, isExtern),
            nativeIntrinsic, start,
            BaseCtorArgs: baseCtorArgs, TypeParams: funcTypeParams, Where: whereClause,
            Tier1Binding: tier1Binding,
            TestAttributes: testAttrs);
    }

    /// Build FunctionModifiers flags from individual booleans parsed from source.
    private static FunctionModifiers BuildModifiers(
        bool isStatic, bool isVirtual, bool isOverride, bool isAbstract, bool isExtern)
    {
        var m = FunctionModifiers.None;
        if (isStatic)   m |= FunctionModifiers.Static;
        if (isVirtual)  m |= FunctionModifiers.Virtual;
        if (isOverride) m |= FunctionModifiers.Override;
        if (isAbstract) m |= FunctionModifiers.Abstract;
        if (isExtern)   m |= FunctionModifiers.Extern;
        return m;
    }
}
