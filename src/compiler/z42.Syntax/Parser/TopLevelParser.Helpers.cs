using Z42.Core.Text;
using Z42.Core.Diagnostics;
using System.Text;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser.Core;

namespace Z42.Syntax.Parser;

/// 顶层 parser 通用 helper：visibility / 修饰符 / qualified name / generic
/// type params / where 子句 / attribute / lookahead / expect。复杂的类型成员
/// desugar（indexer / auto-property）见 TopLevelParser.Members.cs。
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

    /// L3-G2.5 chain: extract the short name from a parsed `TypeExpr`
    /// (for base class / interface list entries). `NamedType` → .Name;
    /// `GenericType` → .Name (args carried separately on the TypeExpr).
    internal static string ExtractTypeName(TypeExpr t) => t switch
    {
        NamedType nt   => nt.Name,
        GenericType gt => gt.Name,
        _              => "<unknown>",
    };

    // ── Generic type parameters & where clause ───────────────────────────────

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

    /// Parse `where T: I [+ J]* [, K: I2]*` clause if present. Supports:
    /// - Type constraints: interface / class (go into `Constraints: List<TypeExpr>`)
    /// - Flag keywords: `class` / `struct` (go into `Kinds: GenericConstraintKind`)
    /// Called immediately before the declaration body (`{` or `=>`).
    /// Returns null when there is no `where` keyword.
    private static WhereClause? ParseWhereClause(ref TokenCursor cursor)
    {
        if (cursor.Current.Kind != TokenKind.Where) return null;
        var startSpan = cursor.Current.Span;
        cursor = cursor.Advance(); // skip `where`
        var entries = new List<GenericConstraint>();
        while (true)
        {
            var entrySpan = cursor.Current.Span;
            var paramName = ExpectKind(ref cursor, TokenKind.Identifier).Text;
            ExpectKind(ref cursor, TokenKind.Colon);
            var types = new List<TypeExpr>();
            var kinds = GenericConstraintKind.None;
            ParseOneConstraint(ref cursor, types, ref kinds);
            while (cursor.Current.Kind == TokenKind.Plus)
            {
                cursor = cursor.Advance();
                ParseOneConstraint(ref cursor, types, ref kinds);
            }
            entries.Add(new GenericConstraint(paramName, types, entrySpan, kinds));
            if (cursor.Current.Kind != TokenKind.Comma) break;
            cursor = cursor.Advance(); // skip , — next `K: I` entry
        }
        return new WhereClause(entries, startSpan);
    }

    /// Parse one constraint entry: flag keyword (`class` / `struct` / `new()`)
    /// or a type expression.
    private static void ParseOneConstraint(
        ref TokenCursor cursor, List<TypeExpr> types, ref GenericConstraintKind kinds)
    {
        switch (cursor.Current.Kind)
        {
            case TokenKind.Class:
                kinds |= GenericConstraintKind.Class;
                cursor = cursor.Advance();
                break;
            case TokenKind.Struct:
                kinds |= GenericConstraintKind.Struct;
                cursor = cursor.Advance();
                break;
            // L3-G2.5 constructor: `where T: new()` — no type args permitted on `new` here.
            case TokenKind.New:
                cursor = cursor.Advance();
                ExpectKind(ref cursor, TokenKind.LParen);
                ExpectKind(ref cursor, TokenKind.RParen);
                kinds |= GenericConstraintKind.Constructor;
                break;
            // L3-G2.5 enum: `where T: enum` — T must be a user-defined enum type.
            case TokenKind.Enum:
                kinds |= GenericConstraintKind.Enum;
                cursor = cursor.Advance();
                break;
            default:
                types.Add(TypeParser.Parse(cursor).Unwrap(ref cursor));
                break;
        }
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

    // ── Attributes ────────────────────────────────────────────────────────────

    /// Spec C6 — parsed `[Native(...)]` attribute. Carries **exactly one** of:
    ///   * `Intrinsic`  for the legacy `[Native("__name")]` form (L1 stdlib dispatch)
    ///   * `Tier1`      for the new `[Native(lib=, type=, entry=)]` form (Tier 1)
    internal sealed record NativeAttribute(string? Intrinsic, Tier1NativeBinding? Tier1);

    /// Spec R1 — known `z42.test.*` attribute names. Parser recognises bare
    /// identifier matches (e.g. `[Test]`); semantic validation lives in R4.
    /// `ShouldThrow` added in R4.B (generic attribute syntax `[ShouldThrow&lt;E&gt;]`).
    private static readonly HashSet<string> TestAttributeNames = new(StringComparer.Ordinal)
    {
        "Test", "Benchmark", "Setup", "Teardown", "Ignore", "Skip", "ShouldThrow"
    };

    /// Parses any bracketed attribute `[...]` and dispatches by name. Returns
    /// a discriminated result (exactly one of Native / Test populated, or both
    /// null when the attribute is unrecognised and skipped). Always advances
    /// the cursor past the closing `]`.
    internal static (NativeAttribute? Native, TestAttribute? Test) TryParseAttribute(ref TokenCursor cursor)
    {
        ExpectKind(ref cursor, TokenKind.LBracket);
        var attrSpan = cursor.Current.Span;

        // Identifier required as first token after `[`. Otherwise skip.
        if (cursor.Current.Kind != TokenKind.Identifier)
        {
            SkipBalancedToRBracket(ref cursor);
            return (null, null);
        }

        string name = cursor.Current.Text;

        // Dispatch: known z42.test attributes vs Native vs unknown.
        if (TestAttributeNames.Contains(name))
        {
            var test = ParseTestAttributeBody(ref cursor, name, attrSpan);
            return (null, test);
        }
        if (name == "Native" && cursor.Peek(1).Kind == TokenKind.LParen)
        {
            var native = ParseNativeAttributeBody(ref cursor);
            return (native, null);
        }

        // Unknown attribute — skip silently (existing behavior, preserves
        // forward compatibility with future attributes).
        SkipBalancedToRBracket(ref cursor);
        return (null, null);
    }

    /// Back-compat shim: returns Native part only. Used by call sites that
    /// pre-date R1; new sites use TryParseAttribute directly.
    private static NativeAttribute? TryParseNativeAttribute(ref TokenCursor cursor)
        => TryParseAttribute(ref cursor).Native;

    /// Spec R1 — parses the body of a `[Test]` / `[Benchmark]` / `[Skip(...)]`
    /// / etc. attribute. Cursor enters at the attribute name identifier;
    /// exits past the closing `]`. Supports two forms:
    ///   * No-arg: `[Test]`, `[Benchmark]`, `[Setup]`, `[Teardown]`, `[Ignore]`
    ///   * Named-args: `[Skip(reason: "...", platform: "...", feature: "...")]`
    ///
    /// Named arg values must be string literals. Returns a TestAttribute AST
    /// node; semantic validation (e.g. requiring [Skip] reason) is in R4.
    private static TestAttribute ParseTestAttributeBody(
        ref TokenCursor cursor, string name, Span attrSpan)
    {
        cursor = cursor.Advance(); // <name>

        // R4.B — optional `<TypeArg>`. Only single non-nested identifier allowed.
        // Parser accepts the syntax for any attribute name; validator gates
        // which names actually permit a type arg.
        string? typeArg = null;
        if (cursor.Current.Kind == TokenKind.Lt)
        {
            cursor = cursor.Advance(); // <
            if (cursor.Current.Kind != TokenKind.Identifier)
            {
                throw new ParseException(
                    $"`[{name}<...>]` requires a type identifier",
                    cursor.Current.Span,
                    DiagnosticCodes.UnexpectedToken);
            }
            typeArg = cursor.Current.Text;
            cursor = cursor.Advance(); // <Type>
            if (cursor.Current.Kind == TokenKind.Comma)
            {
                throw new ParseException(
                    $"`[{name}<...>]` accepts a single type parameter; multi-arg generics not supported in attributes",
                    cursor.Current.Span,
                    DiagnosticCodes.UnexpectedToken);
            }
            if (cursor.Current.Kind == TokenKind.Lt)
            {
                throw new ParseException(
                    $"`[{name}<...>]` does not support nested generic type parameters",
                    cursor.Current.Span,
                    DiagnosticCodes.UnexpectedToken);
            }
            ExpectKind(ref cursor, TokenKind.Gt);
        }

        Dictionary<string, string>? namedArgs = null;
        if (cursor.Current.Kind == TokenKind.LParen)
        {
            cursor = cursor.Advance(); // (
            namedArgs = new Dictionary<string, string>(StringComparer.Ordinal);
            while (cursor.Current.Kind != TokenKind.RParen && !cursor.IsEnd)
            {
                if (cursor.Current.Kind != TokenKind.Identifier
                    || cursor.Peek(1).Kind != TokenKind.Colon)
                {
                    throw new ParseException(
                        $"`[{name}(...)]` requires named arguments of the form `key: \"value\"`",
                        cursor.Current.Span,
                        DiagnosticCodes.UnexpectedToken);
                }

                var keyTok = ExpectKind(ref cursor, TokenKind.Identifier);
                ExpectKind(ref cursor, TokenKind.Colon);
                if (cursor.Current.Kind != TokenKind.StringLiteral)
                {
                    throw new ParseException(
                        $"`[{name}(...)]`: value for `{keyTok.Text}` must be a string literal",
                        cursor.Current.Span,
                        DiagnosticCodes.UnexpectedToken);
                }
                var raw = cursor.Current.Text;
                var val = raw.Length >= 2 ? raw[1..^1] : raw;
                namedArgs[keyTok.Text] = val;
                cursor = cursor.Advance(); // string

                if (cursor.Current.Kind == TokenKind.Comma)
                    cursor = cursor.Advance();
            }
            ExpectKind(ref cursor, TokenKind.RParen);
        }
        ExpectKind(ref cursor, TokenKind.RBracket);

        return new TestAttribute(
            Name:      name,
            TypeArg:   typeArg,
            NamedArgs: namedArgs,
            Span:      attrSpan);
    }

    /// Body of `[Native(...)]`. Caller has already consumed `[` and is
    /// positioned at the `Native` identifier. Exits past the closing `]`.
    private static NativeAttribute? ParseNativeAttributeBody(ref TokenCursor cursor)
    {

        // Look at the first token inside the parens to decide form.
        if (cursor.Peek(2).Kind == TokenKind.StringLiteral)
        {
            // Legacy: `[Native("__name")]`
            cursor    = cursor.Advance(); // Native
            cursor    = cursor.Advance(); // (
            var lit   = cursor.Current.Text;
            var intrinsic = lit.Length >= 2 ? lit[1..^1] : lit; // strip "..."
            cursor    = cursor.Advance(); // "<string>"
            ExpectKind(ref cursor, TokenKind.RParen);
            ExpectKind(ref cursor, TokenKind.RBracket);
            return new NativeAttribute(intrinsic, null);
        }

        if (cursor.Peek(2).Kind == TokenKind.Identifier
            && cursor.Peek(3).Kind == TokenKind.Eq)
        {
            // New form: `[Native(lib="...", type="...", entry="...")]`
            cursor = cursor.Advance(); // Native
            cursor = cursor.Advance(); // (
            string? lib = null, typeName = null, entry = null;
            var attrSpan = cursor.Current.Span;
            while (cursor.Current.Kind != TokenKind.RParen && !cursor.IsEnd)
            {
                var keyTok = ExpectKind(ref cursor, TokenKind.Identifier);
                ExpectKind(ref cursor, TokenKind.Eq);
                if (cursor.Current.Kind != TokenKind.StringLiteral)
                    throw new ParseException(
                        $"`[Native(...)]`: value for `{keyTok.Text}` must be a string literal",
                        cursor.Current.Span,
                        DiagnosticCodes.NativeAttributeMalformed);
                var raw = cursor.Current.Text;
                var val = raw.Length >= 2 ? raw[1..^1] : raw;
                cursor = cursor.Advance(); // string

                switch (keyTok.Text)
                {
                    case "lib":   lib = val; break;
                    case "type":  typeName = val; break;
                    case "entry": entry = val; break;
                    default:
                        throw new ParseException(
                            $"`[Native(...)]`: unknown key `{keyTok.Text}` (allowed: lib, type, entry)",
                            keyTok.Span,
                            DiagnosticCodes.NativeAttributeMalformed);
                }

                if (cursor.Current.Kind == TokenKind.Comma)
                    cursor = cursor.Advance();
            }
            ExpectKind(ref cursor, TokenKind.RParen);
            ExpectKind(ref cursor, TokenKind.RBracket);
            // Spec C9 — accept any non-empty subset of {lib, type, entry}.
            // Strict completeness is validated later by TypeChecker after
            // class-level defaults are stitched in. An attribute that
            // specifies *no* keys at all is still malformed.
            if (lib is null && typeName is null && entry is null)
                throw new ParseException(
                    "`[Native(...)]` requires at least one of `lib`, `type`, `entry`",
                    attrSpan,
                    DiagnosticCodes.NativeAttributeMalformed);
            return new NativeAttribute(null, new Tier1NativeBinding(lib, typeName, entry));
        }

        // Recognised `[Native(` but neither shape — skip and report nothing.
        SkipBalancedToRBracket(ref cursor);
        return null;
    }

    private static void SkipBalancedToRBracket(ref TokenCursor cursor)
    {
        // Caller has already consumed the opening `[`; bracket depth starts at 1.
        int depth = 1;
        while (!cursor.IsEnd && depth > 0)
        {
            if (cursor.Current.Kind == TokenKind.LBracket) depth++;
            if (cursor.Current.Kind == TokenKind.RBracket) depth--;
            cursor = cursor.Advance();
        }
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

    // 2026-05-02 add-delegate-type
    private static bool IsDelegateDecl(TokenCursor cursor)
    {
        cursor = cursor.SkipWhile(t => t.Kind is
            TokenKind.Public or TokenKind.Private
            or TokenKind.Protected or TokenKind.Internal);
        return cursor.Current.Kind == TokenKind.Delegate;
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
        // Optional generic suffix <...> — depth-counted scan to handle multiple
        // type args (e.g. `Dictionary<int, string>`). Note: nested generics
        // `List<List<int>>` are blocked by TypeParser's `>>` (GtGt) handling
        // (separate bug); IsFieldDecl returns false for that and lets the
        // parser produce its usual diagnostic.
        if (cursor.Current.Kind == TokenKind.Lt)
        {
            int depth = 1;
            cursor = cursor.Advance();
            while (!cursor.IsEnd && depth > 0)
            {
                switch (cursor.Current.Kind)
                {
                    case TokenKind.Lt:    depth++; break;
                    case TokenKind.Gt:    depth--; break;
                    // Bail on tokens that can't appear inside a type-arg list —
                    // probably means we mis-identified `<` as generic.
                    case TokenKind.Semicolon:
                    case TokenKind.LBrace:
                    case TokenKind.RBrace:
                    case TokenKind.LParen:
                    case TokenKind.RParen:
                        return false;
                }
                cursor = cursor.Advance();
            }
            if (depth != 0) return false;
        }
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

    /// L3-G4b primitive-as-struct: allow primitive type keywords (`int`, `long`, `double`, ...)
    /// as declaration names so stdlib can write `struct int : IComparable<int> { ... }`.
    /// Returns the token (with `.Text` = source spelling e.g. "int").
    /// Accepts `TokenKind.Identifier` as well.
    private static Token ExpectTypeDeclName(ref TokenCursor cursor)
    {
        var kind = cursor.Current.Kind;
        bool ok = kind == TokenKind.Identifier || IsPrimitiveTypeKeyword(kind);
        if (!ok)
            throw new ParseException(
                $"expected type name, got `{cursor.Current.Text}`",
                cursor.Current.Span,
                DiagnosticCodes.ExpectedToken);
        var tok = cursor.Current;
        cursor  = cursor.Advance();
        return tok;
    }

    /// Primitive scalar type keyword that may appear as a struct/class declaration name.
    /// Excludes `void` / `object` (special) and generic-arg markers.
    private static bool IsPrimitiveTypeKeyword(TokenKind kind) => kind is
        TokenKind.Int    or TokenKind.Long   or TokenKind.Short  or TokenKind.Byte   or
        TokenKind.Sbyte  or TokenKind.Ushort or TokenKind.Uint   or TokenKind.Ulong  or
        TokenKind.Float  or TokenKind.Double or TokenKind.Bool   or TokenKind.Char   or
        TokenKind.String or
        TokenKind.I8 or TokenKind.I16 or TokenKind.I32 or TokenKind.I64 or
        TokenKind.U8 or TokenKind.U16 or TokenKind.U32 or TokenKind.U64 or
        TokenKind.F32 or TokenKind.F64;

    /// Returns true if the token kind is a Phase 2 reserved keyword (fn, let, mut, etc.).
    /// `impl` promoted to Phase 1 for L3 extern impl block.
    private static bool IsPhase2ReservedKeyword(TokenKind kind) => kind is
        TokenKind.Fn or TokenKind.Let or TokenKind.Mut or TokenKind.Trait or
        TokenKind.Use or TokenKind.Module or
        TokenKind.Spawn or TokenKind.None;

    /// L3 operator overload: parse the operator symbol following the `operator` keyword
    /// and return its mangled method name (`op_Add` / `op_Subtract` / ...).
    /// Supports 5 binary arithmetic operators. Names align with `INumber<T>` interface.
    private static string ParseOperatorSymbolAsMethodName(ref TokenCursor cursor)
    {
        var kind = cursor.Current.Kind;
        string name = kind switch
        {
            TokenKind.Plus    => "op_Add",
            TokenKind.Minus   => "op_Subtract",
            TokenKind.Star    => "op_Multiply",
            TokenKind.Slash   => "op_Divide",
            TokenKind.Percent => "op_Modulo",
            _ => throw new ParseException(
                $"expected binary arithmetic operator (+ - * / %) after `operator`, got `{cursor.Current.Text}`",
                cursor.Current.Span,
                DiagnosticCodes.ExpectedToken),
        };
        cursor = cursor.Advance();
        return name;
    }
}
