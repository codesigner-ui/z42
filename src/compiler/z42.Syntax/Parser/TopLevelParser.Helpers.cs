using Z42.Core.Text;
using Z42.Core.Diagnostics;
using System.Text;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser.Core;

namespace Z42.Syntax.Parser;

/// Shared helpers for top-level parsing — part of TopLevelParser.
internal static partial class TopLevelParser
{
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
            throw new ParseException("cannot combine access modifiers", cursor.Current.Span,
                DiagnosticCodes.InvalidModifier);
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

    /// Parse `<T>` or `<K, V>` type parameter list. Returns null if no `<` found.
    private static List<string>? ParseTypeParams(ref TokenCursor cursor)
    {
        if (cursor.Current.Kind != TokenKind.Lt) return null;
        cursor = cursor.Advance(); // skip <
        var typeParams = new List<string>();
        while (cursor.Current.Kind != TokenKind.Gt && !cursor.IsEnd)
        {
            typeParams.Add(ExpectKind(ref cursor, TokenKind.Identifier).Text);
            if (cursor.Current.Kind != TokenKind.Comma) break;
            cursor = cursor.Advance(); // skip ,
        }
        ExpectKind(ref cursor, TokenKind.Gt);
        return typeParams.Count > 0 ? typeParams : null;
    }

    /// Skip generic parameters without collecting them (e.g. in base class / interface lists).
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
        int depth2 = 1;
        while (!cursor.IsEnd && depth2 > 0)
        {
            if (cursor.Current.Kind == TokenKind.LBracket) depth2++;
            if (cursor.Current.Kind == TokenKind.RBracket) depth2--;
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
                cursor.Current.Span,
                DiagnosticCodes.ExpectedToken);
        var tok = cursor.Current;
        cursor  = cursor.Advance();
        return tok;
    }

    /// Returns true if the token kind is a Phase 2 reserved keyword (fn, let, mut, etc.).
    private static bool IsPhase2ReservedKeyword(TokenKind kind) => kind is
        TokenKind.Fn or TokenKind.Let or TokenKind.Mut or TokenKind.Trait or
        TokenKind.Impl or TokenKind.Use or TokenKind.Module or
        TokenKind.Spawn or TokenKind.None;
}
