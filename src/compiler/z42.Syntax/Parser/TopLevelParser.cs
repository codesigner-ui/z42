using Z42.Core.Text;
using System.Text;
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
internal static class TopLevelParser
{
    // ── Compilation unit ──────────────────────────────────────────────────────

    internal static CompilationUnit ParseCompilationUnit(
        TokenCursor cursor, LanguageFeatures feat)
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
            if (IsEnumDecl(cursor))          { pendingNative = null; enums.Add(ParseEnumDecl(ref cursor)); continue; }
            if (IsInterfaceDecl(cursor))      { pendingNative = null; interfaces.Add(ParseInterfaceDecl(ref cursor, feat)); continue; }
            if (IsClassOrStructDecl(cursor))  { pendingNative = null; classes.Add(ParseClassDecl(ref cursor, feat)); continue; }
            functions.Add(ParseFunctionDecl(ref cursor, feat, Visibility.Internal, pendingNative));
            pendingNative = null;
        }

        return new CompilationUnit(ns, usings, classes, functions, enums, interfaces, start);
    }

    // ── Enum ──────────────────────────────────────────────────────────────────

    private static EnumDecl ParseEnumDecl(ref TokenCursor cursor)
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
                throw new ParseException("enum members cannot have access modifiers",
                    cursor.Current.Span);

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

        // Skip generic params: interface IFoo<T>
        SkipGenericParams(ref cursor);

        // Skip base interfaces: interface IFoo : IBar, IBaz
        if (cursor.Current.Kind == TokenKind.Colon)
        {
            cursor = cursor.Advance();
            ParseQualifiedName(ref cursor);
            while (cursor.Current.Kind == TokenKind.Comma)
            {
                cursor = cursor.Advance();
                ParseQualifiedName(ref cursor);
            }
        }

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
        return new InterfaceDecl(name, vis, methods, start);
    }

    // ── Class / struct / record ───────────────────────────────────────────────

    private static ClassDecl ParseClassDecl(ref TokenCursor cursor, LanguageFeatures feat)
    {
        var start = cursor.Current.Span;
        var vis   = ParseVisibility(ref cursor, Visibility.Internal);
        var (_, _, _, isAbstract, isSealed, _) = ParseNonVisibilityModifiers(ref cursor);

        bool isRecord = false;
        if (cursor.Current.Kind == TokenKind.Record) { isRecord = true; cursor = cursor.Advance(); }

        bool isStruct = cursor.Current.Kind == TokenKind.Struct;
        if (cursor.Current.Kind is TokenKind.Class or TokenKind.Struct) cursor = cursor.Advance();

        var name = ExpectKind(ref cursor, TokenKind.Identifier).Text;

        // Skip generic params
        SkipGenericParams(ref cursor);

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
                ctorBody, Visibility.Public, false, false, false, false, false, null, start));

            if (cursor.Current.Kind == TokenKind.Semicolon)
            {
                cursor = cursor.Advance();
                return new ClassDecl(name, isStruct, isRecord, isAbstract, isSealed, vis,
                    null, [], fields, methods, start);
            }
        }

        // Base class / interfaces: class Foo : Base, IFace
        string? baseClass   = null;
        var     ifaces      = new List<string>();
        if (cursor.Current.Kind == TokenKind.Colon)
        {
            cursor = cursor.Advance();
            var firstName = ParseQualifiedName(ref cursor);
            if (firstName.Length > 1 && firstName[0] == 'I' && char.IsUpper(firstName[1]))
                ifaces.Add(firstName);
            else
                baseClass = firstName;

            while (cursor.Current.Kind == TokenKind.Comma)
            {
                cursor = cursor.Advance();
                var extra = ParseQualifiedName(ref cursor);
                SkipGenericParams(ref cursor);
                ifaces.Add(extra);
            }
        }

        ExpectKind(ref cursor, TokenKind.LBrace);
        string? pendingNative = null;
        while (cursor.Current.Kind != TokenKind.RBrace && !cursor.IsEnd)
        {
            if (cursor.Current.Kind == TokenKind.LBracket) { pendingNative = TryParseNativeAttribute(ref cursor); continue; }
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
                methods.Add(ParseFunctionDecl(ref cursor, feat, Visibility.Internal, pendingNative));
                pendingNative = null;
            }
        }
        ExpectKind(ref cursor, TokenKind.RBrace);
        return new ClassDecl(name, isStruct, isRecord, isAbstract, isSealed, vis,
            baseClass, ifaces, fields, methods, start);
    }

    // ── Function declaration ──────────────────────────────────────────────────

    internal static FunctionDecl ParseFunctionDecl(
        ref TokenCursor cursor, LanguageFeatures feat, Visibility defaultVis,
        string? nativeIntrinsic = null)
    {
        var start = cursor.Current.Span;
        var vis   = ParseVisibility(ref cursor, defaultVis);
        var (isStatic, isVirtual, isOverride, isAbstract, _, isExtern) =
            ParseNonVisibilityModifiers(ref cursor);

        // Constructor pattern: Ident( — no explicit return type
        TypeExpr returnType;
        string   name;
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

        // Abstract / extern methods: no body, just a semicolon
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
            body = StmtParser.ParseBlock(cursor, feat).Unwrap(ref cursor);
        }

        return new FunctionDecl(name, parms, returnType, body, vis,
            isStatic, isVirtual, isOverride, isAbstract, isExtern, nativeIntrinsic, start,
            BaseCtorArgs: baseCtorArgs);
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static List<Param> ParseParamList(ref TokenCursor cursor, LanguageFeatures feat)
    {
        ExpectKind(ref cursor, TokenKind.LParen);
        var parms = new List<Param>();
        while (cursor.Current.Kind != TokenKind.RParen && !cursor.IsEnd)
        {
            var pSpan    = cursor.Current.Span;
            var pType    = TypeParser.Parse(cursor).Unwrap(ref cursor);
            var pName    = ExpectKind(ref cursor, TokenKind.Identifier).Text;
            Expr? pDefault = null;
            if (cursor.Current.Kind == TokenKind.Eq)
            {
                cursor   = cursor.Advance();
                pDefault = ExprParser.Parse(cursor, feat).Unwrap(ref cursor);
            }
            parms.Add(new Param(pName, pType, pDefault, pSpan));
            if (cursor.Current.Kind != TokenKind.Comma) break;
            cursor = cursor.Advance();
        }
        ExpectKind(ref cursor, TokenKind.RParen);
        return parms;
    }

    private static Visibility ParseVisibility(
        ref TokenCursor cursor, Visibility defaultVis)
    {
        var vis = cursor.Current.Kind switch
        {
            TokenKind.Public    => Visibility.Public,
            TokenKind.Private   => Visibility.Private,
            TokenKind.Protected => Visibility.Protected,
            TokenKind.Internal  => Visibility.Internal,
            _                   => (Visibility?)null,
        };
        if (vis is null) return defaultVis;
        cursor = cursor.Advance();
        // Detect illegal combined visibility
        if (cursor.Current.Kind is TokenKind.Public or TokenKind.Private
                                 or TokenKind.Protected or TokenKind.Internal)
            throw new ParseException("cannot combine access modifiers", cursor.Current.Span);
        return vis.Value;
    }

    private static (bool IsStatic, bool IsVirtual, bool IsOverride,
                    bool IsAbstract, bool IsSealed, bool IsExtern)
        ParseNonVisibilityModifiers(ref TokenCursor cursor)
    {
        bool isStatic = false, isVirtual = false, isOverride = false,
             isAbstract = false, isSealed = false, isExtern = false;
        while (cursor.Current.Kind is TokenKind.Static or TokenKind.Abstract
                                   or TokenKind.Sealed  or TokenKind.Virtual
                                   or TokenKind.Override or TokenKind.Async
                                   or TokenKind.New     or TokenKind.Extern)
        {
            if (cursor.Current.Kind == TokenKind.Static)   isStatic   = true;
            if (cursor.Current.Kind == TokenKind.Virtual)  isVirtual  = true;
            if (cursor.Current.Kind == TokenKind.Override) isOverride = true;
            if (cursor.Current.Kind == TokenKind.Abstract) isAbstract = true;
            if (cursor.Current.Kind == TokenKind.Sealed)   isSealed   = true;
            if (cursor.Current.Kind == TokenKind.Extern)   isExtern   = true;
            cursor = cursor.Advance();
        }
        return (isStatic, isVirtual, isOverride, isAbstract, isSealed, isExtern);
    }

    private static void SkipNonVisibilityModifiers(ref TokenCursor cursor) =>
        ParseNonVisibilityModifiers(ref cursor);

    private static string ParseQualifiedName(ref TokenCursor cursor)
    {
        var sb = new StringBuilder(ExpectKind(ref cursor, TokenKind.Identifier).Text);
        while (cursor.Current.Kind == TokenKind.Dot)
        {
            cursor = cursor.Advance();
            sb.Append('.').Append(ExpectKind(ref cursor, TokenKind.Identifier).Text);
        }
        return sb.ToString();
    }

    private static void SkipGenericParams(ref TokenCursor cursor)
    {
        if (cursor.Current.Kind != TokenKind.Lt) return;
        cursor = cursor.Advance();
        int depth = 1;
        while (!cursor.IsEnd && depth > 0)
        {
            if (cursor.Current.Kind == TokenKind.Lt) depth++;
            if (cursor.Current.Kind == TokenKind.Gt) depth--;
            cursor = cursor.Advance();
        }
    }

    /// Parses an attribute `[...]`.
    /// Returns the intrinsic name string if it is `[Native("__name")]`; null otherwise.
    /// Always advances the cursor past the closing `]`.
    private static string? TryParseNativeAttribute(ref TokenCursor cursor)
    {
        ExpectKind(ref cursor, TokenKind.LBracket);

        // Try to match: Native ( "<string>" )
        string? intrinsic = null;
        if (cursor.Current.Kind == TokenKind.Identifier
            && cursor.Current.Text == "Native"
            && cursor.Peek(1).Kind == TokenKind.LParen
            && cursor.Peek(2).Kind == TokenKind.StringLiteral
            && cursor.Peek(3).Kind == TokenKind.RParen
            && cursor.Peek(4).Kind == TokenKind.RBracket)
        {
            cursor    = cursor.Advance(); // Native
            cursor    = cursor.Advance(); // (
            var lit   = cursor.Current.Text;
            intrinsic = lit.Length >= 2 ? lit[1..^1] : lit; // strip surrounding quotes
            cursor    = cursor.Advance(); // "<string>"
            cursor    = cursor.Advance(); // )
            cursor    = cursor.Advance(); // ]
            return intrinsic;
        }

        // Not a Native attribute — skip balanced brackets.
        int depth = 1;
        while (!cursor.IsEnd && depth > 0)
        {
            if (cursor.Current.Kind == TokenKind.LBracket) depth++;
            if (cursor.Current.Kind == TokenKind.RBracket) depth--;
            cursor = cursor.Advance();
        }
        return null;
    }

    private static void SkipAttribute(ref TokenCursor cursor)
    {
        ExpectKind(ref cursor, TokenKind.LBracket);
        int depth = 1;
        while (!cursor.IsEnd && depth > 0)
        {
            if (cursor.Current.Kind == TokenKind.LBracket) depth++;
            if (cursor.Current.Kind == TokenKind.RBracket) depth--;
            cursor = cursor.Advance();
        }
    }

    private static void SkipAutoPropBody(ref TokenCursor cursor)
    {
        ExpectKind(ref cursor, TokenKind.LBrace);
        int depth = 1;
        while (!cursor.IsEnd && depth > 0)
        {
            if (cursor.Current.Kind == TokenKind.LBrace) depth++;
            else if (cursor.Current.Kind == TokenKind.RBrace) { depth--; if (depth == 0) break; }
            cursor = cursor.Advance();
        }
        ExpectKind(ref cursor, TokenKind.RBrace);
    }

    // ── Lookahead helpers (clean cursor-based, no _pos+i arithmetic) ──────────

    private static bool IsEnumDecl(TokenCursor cursor)
    {
        cursor = cursor.SkipWhile(t => t.Kind is
            TokenKind.Public or TokenKind.Private
            or TokenKind.Protected or TokenKind.Internal);
        return cursor.Current.Kind == TokenKind.Enum;
    }

    private static bool IsInterfaceDecl(TokenCursor cursor)
    {
        cursor = cursor.SkipWhile(t => t.Kind is
            TokenKind.Public or TokenKind.Private
            or TokenKind.Protected or TokenKind.Internal);
        return cursor.Current.Kind == TokenKind.Interface;
    }

    private static bool IsClassOrStructDecl(TokenCursor cursor)
    {
        cursor = cursor.SkipWhile(t => t.Kind is
            TokenKind.Public    or TokenKind.Private or TokenKind.Protected
            or TokenKind.Internal or TokenKind.Static or TokenKind.Abstract
            or TokenKind.Sealed);
        return cursor.Current.Kind is
            TokenKind.Class or TokenKind.Struct or TokenKind.Record;
    }

    private static bool IsFieldDecl(TokenCursor cursor)
    {
        // Skip visibility + static/sealed modifiers
        cursor = cursor.SkipWhile(t => t.Kind is
            TokenKind.Public    or TokenKind.Private or TokenKind.Protected
            or TokenKind.Internal or TokenKind.Static or TokenKind.Sealed);
        // Must start with a type token
        if (!TypeParser.IsTypeToken(cursor.Current.Kind)) return false;
        cursor = cursor.Advance();
        // Optional array suffix []
        if (cursor.Current.Kind == TokenKind.LBracket
            && cursor.Peek(1).Kind == TokenKind.RBracket)
            cursor = cursor.Advance(2);
        // Must be followed by identifier
        if (cursor.Current.Kind != TokenKind.Identifier) return false;
        cursor = cursor.Advance();
        // Followed by ;  =  or {  (auto-property)
        return cursor.Current.Kind is
            TokenKind.Semicolon or TokenKind.Eq or TokenKind.LBrace;
    }

    // ── Expect helper ─────────────────────────────────────────────────────────

    private static Token ExpectKind(ref TokenCursor cursor, TokenKind kind)
    {
        if (cursor.Current.Kind != kind)
            throw new ParseException(
                $"expected `{Combinators.KindDisplay(kind)}`, got `{cursor.Current.Text}`",
                cursor.Current.Span);
        var tok = cursor.Current;
        cursor  = cursor.Advance();
        return tok;
    }
}
