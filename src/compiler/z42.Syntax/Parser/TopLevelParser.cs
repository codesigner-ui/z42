using Z42.Core.Text;
using System.Text;
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

        var classes    = new List<ClassDecl>();
        var functions  = new List<FunctionDecl>();
        var enums      = new List<EnumDecl>();
        var interfaces = new List<InterfaceDecl>();

        string? pendingNative = null;
        while (!cursor.IsEnd)
        {
            try
            {
                if (IsPhase2ReservedKeyword(cursor.Current.Kind))
                    throw new ParseException(
                        $"`{cursor.Current.Text}` is a keyword reserved for Phase 2 and cannot be used yet",
                        cursor.Current.Span,
                        DiagnosticCodes.FeatureDisabled);
                if (cursor.Current.Kind == TokenKind.LBracket) { pendingNative = TryParseNativeAttribute(ref cursor); continue; }
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
                if (IsEnumDecl(cursor))          { pendingNative = null; enums.Add(ParseEnumDecl(ref cursor, diags)); continue; }
                if (IsInterfaceDecl(cursor))      { pendingNative = null; interfaces.Add(ParseInterfaceDecl(ref cursor, feat)); continue; }
                if (IsClassOrStructDecl(cursor))  { pendingNative = null; classes.Add(ParseClassDecl(ref cursor, feat, diags)); continue; }
                functions.Add(ParseFunctionDecl(ref cursor, feat, Visibility.Internal, pendingNative, diags));
                pendingNative = null;
            }
            catch (ParseException ex) when (diags != null)
            {
                diags.Error(ex.Code ?? DiagnosticCodes.UnexpectedToken, ex.Message, ex.Span);
                pendingNative = null;
                cursor = SkipToNextDeclaration(cursor);
            }
        }

        return new CompilationUnit(ns, usings, classes, functions, enums, interfaces, start);
    }

    /// Skip tokens until we reach a plausible start of a new top-level declaration.
    private static TokenCursor SkipToNextDeclaration(TokenCursor cursor)
    {
        while (!cursor.IsEnd)
        {
            var kind = cursor.Current.Kind;
            // Stop at tokens that start a new top-level declaration
            if (kind is TokenKind.Class or TokenKind.Struct or TokenKind.Enum
                or TokenKind.Interface or TokenKind.Namespace or TokenKind.Using
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

    // ── Enum ──────────────────────────────────────────────────────────────────

    private static EnumDecl ParseEnumDecl(ref TokenCursor cursor, DiagnosticBag? diags = null)
    {
        var start = cursor.Current.Span;
        var vis   = ParseVisibility(ref cursor, Visibility.Internal);
        SkipNonVisibilityModifiers(ref cursor);
        ExpectKind(ref cursor, TokenKind.Enum);
        var name = ExpectKind(ref cursor, TokenKind.Identifier).Text;

        if (cursor.Current.Kind == TokenKind.Colon)
        {
            cursor = cursor.Advance();  // skip base type token
            cursor = cursor.Advance();
        }
        ExpectKind(ref cursor, TokenKind.LBrace);

        var members = new List<EnumMember>();
        long next   = 0;
        while (cursor.Current.Kind != TokenKind.RBrace && !cursor.IsEnd)
        {
            var mSpan = cursor.Current.Span;
            if (cursor.Current.Kind is TokenKind.Public or TokenKind.Private
                                    or TokenKind.Protected or TokenKind.Internal)
            {
                if (diags != null)
                {
                    diags.Error(DiagnosticCodes.InvalidModifier,
                        "enum members cannot have access modifiers", cursor.Current.Span);
                    while (!cursor.IsEnd && cursor.Current.Kind != TokenKind.Comma
                                        && cursor.Current.Kind != TokenKind.RBrace)
                        cursor = cursor.Advance();
                    if (cursor.Current.Kind == TokenKind.Comma) cursor = cursor.Advance();
                    continue;
                }
                throw new ParseException("enum members cannot have access modifiers",
                    cursor.Current.Span,
                    DiagnosticCodes.InvalidModifier);
            }

            var mName = ExpectKind(ref cursor, TokenKind.Identifier).Text;
            long? val;
            if (cursor.Current.Kind == TokenKind.Eq)
            {
                cursor = cursor.Advance();
                bool neg = cursor.Current.Kind == TokenKind.Minus;
                if (neg) cursor = cursor.Advance();
                long raw = long.Parse(ExpectKind(ref cursor, TokenKind.IntLiteral).Text);
                val  = neg ? -raw : raw;
                next = val.Value + 1;
            }
            else
            {
                val = next++;
            }
            members.Add(new EnumMember(mName, val, mSpan));
            if (cursor.Current.Kind != TokenKind.Comma) break;
            cursor = cursor.Advance();
        }
        ExpectKind(ref cursor, TokenKind.RBrace);
        return new EnumDecl(name, vis, members, start);
    }

    // ── Interface ─────────────────────────────────────────────────────────────

    private static InterfaceDecl ParseInterfaceDecl(
        ref TokenCursor cursor, LanguageFeatures feat)
    {
        var start = cursor.Current.Span;
        var vis   = ParseVisibility(ref cursor, Visibility.Internal);
        SkipNonVisibilityModifiers(ref cursor);
        ExpectKind(ref cursor, TokenKind.Interface);
        var name = ExpectKind(ref cursor, TokenKind.Identifier).Text;

        // Generic params: interface IFoo<T>
        var typeParams = ParseTypeParams(ref cursor);

        // Skip base interfaces: interface IFoo : IBar, IBaz
        if (cursor.Current.Kind == TokenKind.Colon)
        {
            cursor = cursor.Advance();
            ParseQualifiedName(ref cursor);
            SkipGenericParams(ref cursor);
            while (cursor.Current.Kind == TokenKind.Comma)
            {
                cursor = cursor.Advance();
                ParseQualifiedName(ref cursor);
                SkipGenericParams(ref cursor);
            }
        }

        var whereClause = ParseWhereClause(ref cursor);

        ExpectKind(ref cursor, TokenKind.LBrace);
        var methods = new List<MethodSignature>();
        while (cursor.Current.Kind != TokenKind.RBrace && !cursor.IsEnd)
        {
            if (cursor.Current.Kind == TokenKind.LBracket) { SkipAttribute(ref cursor); continue; }
            var mSpan = cursor.Current.Span;
            ParseVisibility(ref cursor, Visibility.Public);
            SkipNonVisibilityModifiers(ref cursor);
            var mType = TypeParser.Parse(cursor).Unwrap(ref cursor);
            var mName = ExpectKind(ref cursor, TokenKind.Identifier).Text;

            if (cursor.Current.Kind == TokenKind.LBrace) { SkipAutoPropBody(ref cursor); continue; }

            var parms = ParseParamList(ref cursor, feat);
            ExpectKind(ref cursor, TokenKind.Semicolon);
            methods.Add(new MethodSignature(mName, parms, mType, mSpan));
        }
        ExpectKind(ref cursor, TokenKind.RBrace);
        return new InterfaceDecl(name, vis, methods, start, typeParams, whereClause);
    }

    // ── Class / struct / record ───────────────────────────────────────────────

    private static ClassDecl ParseClassDecl(ref TokenCursor cursor, LanguageFeatures feat,
        DiagnosticBag? diags = null)
    {
        var start = cursor.Current.Span;
        var vis   = ParseVisibility(ref cursor, Visibility.Internal);
        var (_, _, _, isAbstract, isSealed, _) = ParseNonVisibilityModifiers(ref cursor);

        bool isRecord = false;
        if (cursor.Current.Kind == TokenKind.Record) { isRecord = true; cursor = cursor.Advance(); }

        bool isStruct = cursor.Current.Kind == TokenKind.Struct;
        if (cursor.Current.Kind is TokenKind.Class or TokenKind.Struct) cursor = cursor.Advance();

        var name = ExpectKind(ref cursor, TokenKind.Identifier).Text;

        // Generic params: class Foo<T>, struct Pair<K,V>
        var typeParams = ParseTypeParams(ref cursor);

        var fields  = new List<FieldDecl>();
        var methods = new List<FunctionDecl>();

        // Positional record: record Person(string Name, int Age)
        if (isRecord && cursor.Current.Kind == TokenKind.LParen)
        {
            cursor = cursor.Advance();
            var ctorParams = new List<Param>();
            var ctorStmts  = new List<Stmt>();
            while (cursor.Current.Kind != TokenKind.RParen && !cursor.IsEnd)
            {
                var pSpan = cursor.Current.Span;
                ParseVisibility(ref cursor, Visibility.Public);
                var pType = TypeParser.Parse(cursor).Unwrap(ref cursor);
                var pName = ExpectKind(ref cursor, TokenKind.Identifier).Text;
                ctorParams.Add(new Param(pName, pType, null, pSpan));
                fields.Add(new FieldDecl(pName, pType, Visibility.Public, false, null, pSpan));
                ctorStmts.Add(new ExprStmt(
                    new AssignExpr(
                        new MemberExpr(new IdentExpr("this", pSpan), pName, pSpan),
                        new IdentExpr(pName, pSpan), pSpan),
                    pSpan));
                if (cursor.Current.Kind != TokenKind.Comma) break;
                cursor = cursor.Advance();
            }
            ExpectKind(ref cursor, TokenKind.RParen);
            var ctorBody = new BlockStmt(ctorStmts, start);
            methods.Add(new FunctionDecl(name, ctorParams, new VoidType(start),
                ctorBody, Visibility.Public, FunctionModifiers.None, null, start));

            if (cursor.Current.Kind == TokenKind.Semicolon)
            {
                cursor = cursor.Advance();
                return new ClassDecl(name, isStruct, isRecord, isAbstract, isSealed, vis,
                    null, [], fields, methods, start, typeParams, Where: null);
            }
        }

        // Base class / interfaces: class Foo : Base, IFace<T>, IBar
        // First entry may be a base class (non-`I`-prefix) or an interface;
        // subsequent entries are all interfaces. L3-G2.5 chain: interface
        // type args are preserved in the AST (parsed as full TypeExpr).
        string? baseClass   = null;
        var     ifaces      = new List<TypeExpr>();
        if (cursor.Current.Kind == TokenKind.Colon)
        {
            cursor = cursor.Advance();
            var firstSpan = cursor.Current.Span;
            var firstTy   = TypeParser.Parse(cursor).Unwrap(ref cursor);
            var firstName = ExtractTypeName(firstTy);
            if (firstName.Length > 1 && firstName[0] == 'I' && char.IsUpper(firstName[1]))
                ifaces.Add(firstTy);
            else
                baseClass = firstName;

            while (cursor.Current.Kind == TokenKind.Comma)
            {
                cursor = cursor.Advance();
                ifaces.Add(TypeParser.Parse(cursor).Unwrap(ref cursor));
            }
        }

        var classWhereClause = ParseWhereClause(ref cursor);

        ExpectKind(ref cursor, TokenKind.LBrace);
        string? pendingNative = null;
        while (cursor.Current.Kind != TokenKind.RBrace && !cursor.IsEnd)
        {
            try
            {
                if (cursor.Current.Kind == TokenKind.LBracket) { pendingNative = TryParseNativeAttribute(ref cursor); continue; }
                // L3-G4e: indexer `<vis>? <type> this [params] { get {..} set {..} }`
                // desugars to `get_Item(params) → type` + `set_Item(params, type value) → void`
                if (IsIndexerDecl(cursor))
                {
                    pendingNative = null;
                    foreach (var m in ParseIndexerDecl(ref cursor, feat, diags))
                        methods.Add(m);
                    continue;
                }
                if (IsFieldDecl(cursor))
                {
                    pendingNative = null;
                    var fSpan = cursor.Current.Span;
                    var fVis  = ParseVisibility(ref cursor, Visibility.Internal);
                    var (fStatic, _, _, _, _, _) = ParseNonVisibilityModifiers(ref cursor);
                    var fType = TypeParser.Parse(cursor).Unwrap(ref cursor);
                    var fName = ExpectKind(ref cursor, TokenKind.Identifier).Text;
                    Expr? fInit = null;
                    if (cursor.Current.Kind == TokenKind.LBrace)
                        SkipAutoPropBody(ref cursor);
                    else
                    {
                        if (cursor.Current.Kind == TokenKind.Eq)
                        {
                            cursor = cursor.Advance();
                            fInit = ExprParser.Parse(cursor, LanguageFeatures.Phase1).Unwrap(ref cursor);
                        }
                        ExpectKind(ref cursor, TokenKind.Semicolon);
                    }
                    fields.Add(new FieldDecl(fName, fType, fVis, fStatic, fInit, fSpan));
                }
                else
                {
                    methods.Add(ParseFunctionDecl(ref cursor, feat, Visibility.Internal, pendingNative, diags));
                    pendingNative = null;
                }
            }
            catch (ParseException ex) when (diags != null)
            {
                diags.Error(ex.Code ?? DiagnosticCodes.UnexpectedToken, ex.Message, ex.Span);
                pendingNative = null;
                if (!cursor.IsEnd) cursor = cursor.Advance();
                while (!cursor.IsEnd && cursor.Current.Kind != TokenKind.RBrace
                                     && cursor.Current.Kind != TokenKind.Semicolon)
                    cursor = cursor.Advance();
                if (cursor.Current.Kind == TokenKind.Semicolon) cursor = cursor.Advance();
            }
        }
        ExpectKind(ref cursor, TokenKind.RBrace);
        return new ClassDecl(name, isStruct, isRecord, isAbstract, isSealed, vis,
            baseClass, ifaces, fields, methods, start, typeParams, classWhereClause);
    }

    // ── Function declaration ──────────────────────────────────────────────────

    internal static FunctionDecl ParseFunctionDecl(
        ref TokenCursor cursor, LanguageFeatures feat, Visibility defaultVis,
        string? nativeIntrinsic = null, DiagnosticBag? diags = null)
    {
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
            name       = ExpectKind(ref cursor, TokenKind.Identifier).Text;
            // Generic function: T Max<T>(T a, T b)
            funcTypeParams = ParseTypeParams(ref cursor);
        }

        // Extern property: `extern T Name { get; }` — no parameter list
        if (isExtern && cursor.Current.Kind == TokenKind.LBrace)
        {
            SkipAutoPropBody(ref cursor);
            return new FunctionDecl(name, [], returnType,
                new BlockStmt([], start), vis, BuildModifiers(isStatic, isVirtual, isOverride, isAbstract, isExtern),
                nativeIntrinsic, start);
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
            BaseCtorArgs: baseCtorArgs, TypeParams: funcTypeParams, Where: whereClause);
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
