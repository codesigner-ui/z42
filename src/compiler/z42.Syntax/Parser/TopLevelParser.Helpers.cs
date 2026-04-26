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

    /// L3-G2.5 chain: extract the short name from a parsed `TypeExpr`
    /// (for base class / interface list entries). `NamedType` → .Name;
    /// `GenericType` → .Name (args carried separately on the TypeExpr).
    internal static string ExtractTypeName(TypeExpr t) => t switch
    {
        NamedType nt   => nt.Name,
        GenericType gt => gt.Name,
        _              => "<unknown>",
    };

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

    // ── L3-G4e: indexer ───────────────────────────────────────────────────────

    /// Detects `<vis>? <type> this [` in a class body — the indexer signature.
    /// Lookahead only; does not consume.
    private static bool IsIndexerDecl(TokenCursor cursor)
    {
        cursor = cursor.SkipWhile(t => t.Kind is
            TokenKind.Public    or TokenKind.Private or TokenKind.Protected
            or TokenKind.Internal or TokenKind.Static or TokenKind.Sealed);
        if (!TypeParser.IsTypeToken(cursor.Current.Kind)) return false;
        cursor = cursor.Advance();
        // Optional [] for array element type
        if (cursor.Current.Kind == TokenKind.LBracket
            && cursor.Peek(1).Kind == TokenKind.RBracket)
            cursor = cursor.Advance(2);
        // Optional <TypeArgs> (for generic return type like `T`)
        if (cursor.Current.Kind == TokenKind.Lt)
        {
            int depth = 1; cursor = cursor.Advance();
            while (!cursor.IsEnd && depth > 0)
            {
                if (cursor.Current.Kind == TokenKind.Lt) depth++;
                else if (cursor.Current.Kind == TokenKind.Gt) depth--;
                cursor = cursor.Advance();
            }
        }
        // Optional ? for nullable
        if (cursor.Current.Kind == TokenKind.Question) cursor = cursor.Advance();
        // Must be `this [`
        return cursor.Current is { Kind: TokenKind.Identifier, Text: "this" }
            && cursor.Peek(1).Kind == TokenKind.LBracket;
    }

    /// Parse an indexer declaration and desugar into two synthetic FunctionDecls:
    /// one for `get` (named `get_Item`) and one for `set` (named `set_Item`, with
    /// implicit `value` parameter of the indexer's element type).
    /// Either accessor may be absent; at least one must be present.
    private static IEnumerable<FunctionDecl> ParseIndexerDecl(
        ref TokenCursor cursor, LanguageFeatures feat, DiagnosticBag? diags)
    {
        var start   = cursor.Current.Span;
        var vis     = ParseVisibility(ref cursor, Visibility.Internal);
        ParseNonVisibilityModifiers(ref cursor); // no static-indexer in L3-G4e; ignored
        var elemType = TypeParser.Parse(cursor).Unwrap(ref cursor);
        // Consume `this`
        var thisTok = ExpectKind(ref cursor, TokenKind.Identifier);
        if (thisTok.Text != "this")
            throw new ParseException(
                $"expected `this` in indexer declaration, got `{thisTok.Text}`",
                thisTok.Span, DiagnosticCodes.UnexpectedToken);
        // Params: [int i] or [int row, int col]
        ExpectKind(ref cursor, TokenKind.LBracket);
        var indexParams = new List<Param>();
        while (cursor.Current.Kind != TokenKind.RBracket && !cursor.IsEnd)
        {
            var pSpan = cursor.Current.Span;
            var pType = TypeParser.Parse(cursor).Unwrap(ref cursor);
            var pName = ExpectKind(ref cursor, TokenKind.Identifier).Text;
            indexParams.Add(new Param(pName, pType, null, pSpan));
            if (cursor.Current.Kind != TokenKind.Comma) break;
            cursor = cursor.Advance();
        }
        ExpectKind(ref cursor, TokenKind.RBracket);
        // Body: { get { ... } [set { ... }] } — order-insensitive, at least one required.
        ExpectKind(ref cursor, TokenKind.LBrace);
        BlockStmt? getBody = null;
        BlockStmt? setBody = null;
        Span       getSpan = start, setSpan = start;
        while (cursor.Current.Kind != TokenKind.RBrace && !cursor.IsEnd)
        {
            ParseVisibility(ref cursor, vis); // accessor visibility ignored in L3-G4e
            var kw = cursor.Current;
            if (kw.Kind != TokenKind.Identifier || (kw.Text != "get" && kw.Text != "set"))
                throw new ParseException(
                    $"expected `get` or `set` in indexer body, got `{kw.Text}`",
                    kw.Span, DiagnosticCodes.UnexpectedToken);
            cursor = cursor.Advance();
            var body = StmtParser.ParseBlock(cursor, feat, diags).Unwrap(ref cursor);
            if (kw.Text == "get") { getBody = body; getSpan = kw.Span; }
            else                  { setBody = body; setSpan = kw.Span; }
        }
        ExpectKind(ref cursor, TokenKind.RBrace);
        if (getBody is null && setBody is null)
            throw new ParseException(
                "indexer must declare at least one of `get` or `set`",
                start, DiagnosticCodes.UnexpectedToken);
        var result = new List<FunctionDecl>();
        if (getBody is not null)
            result.Add(new FunctionDecl("get_Item", indexParams, elemType, getBody,
                vis, FunctionModifiers.None, null, getSpan));
        if (setBody is not null)
        {
            var setParams = new List<Param>(indexParams) {
                new Param("value", elemType, null, setSpan)
            };
            result.Add(new FunctionDecl("set_Item", setParams, new VoidType(setSpan), setBody,
                vis, FunctionModifiers.None, null, setSpan));
        }
        return result;
    }

    /// Parse auto-property accessor block `{ get; [set;] }` (order-insensitive).
    /// Returns (hasGet, hasSet). At least one accessor must be present.
    /// Accessor visibility (`public get; private set;`) parsed but ignored
    /// — uniform with indexer (L3-G4e).
    private static (bool hasGet, bool hasSet) ParseAutoPropAccessors(
        ref TokenCursor cursor, Visibility memberVis, Span propSpan)
    {
        ExpectKind(ref cursor, TokenKind.LBrace);
        bool hasGet = false, hasSet = false;
        while (cursor.Current.Kind != TokenKind.RBrace && !cursor.IsEnd)
        {
            ParseVisibility(ref cursor, memberVis); // accessor vis ignored
            var kw = cursor.Current;
            if (kw.Kind != TokenKind.Identifier || (kw.Text != "get" && kw.Text != "set"))
                throw new ParseException(
                    $"expected `get` or `set` in auto-property body, got `{kw.Text}`",
                    kw.Span, DiagnosticCodes.UnexpectedToken);
            cursor = cursor.Advance();
            ExpectKind(ref cursor, TokenKind.Semicolon);
            if (kw.Text == "get") hasGet = true; else hasSet = true;
        }
        ExpectKind(ref cursor, TokenKind.RBrace);
        if (!hasGet)
            throw new ParseException(
                "auto-property must declare `get` (set-only properties are not supported)",
                propSpan, DiagnosticCodes.UnexpectedToken);
        return (hasGet, hasSet);
    }

    /// Synthesise (FieldDecl, getter, [setter]) for a class auto-property
    /// `<vis>? <type> <name> { get; [set;] }`. Returns:
    ///   - backing FieldDecl: `private <type> __prop_<name>;`
    ///   - getter: `<vis> <type> get_<name>() { return this.__prop_<name>; }`
    ///   - setter (if hasSet): `<vis> void set_<name>(<type> value) { this.__prop_<name> = value; }`
    internal static (FieldDecl backing, List<FunctionDecl> accessors)
    SynthesizeClassAutoProp(
        ref TokenCursor cursor, Visibility memberVis, bool isStatic,
        TypeExpr propType, string propName, Span propSpan)
    {
        var (_, hasSet) = ParseAutoPropAccessors(ref cursor, memberVis, propSpan);

        var bfName  = $"__prop_{propName}";
        var backing = new FieldDecl(bfName, propType, Visibility.Private, isStatic, null, propSpan);

        var mods = isStatic ? FunctionModifiers.Static : FunctionModifiers.None;

        // getter body: return this.__prop_<name>;
        var thisExpr = new IdentExpr("this", propSpan);
        var bfRead   = new MemberExpr(thisExpr, bfName, propSpan);
        var getBody  = new BlockStmt(new List<Stmt> { new ReturnStmt(bfRead, propSpan) }, propSpan);
        var getter   = new FunctionDecl($"get_{propName}",
            new List<Param>(), propType, getBody, memberVis, mods, null, propSpan);

        var accessors = new List<FunctionDecl> { getter };

        if (hasSet)
        {
            // setter body: this.__prop_<name> = value;
            var valExpr  = new IdentExpr("value", propSpan);
            var bfWrite  = new MemberExpr(thisExpr, bfName, propSpan);
            var assign   = new AssignExpr(bfWrite, valExpr, propSpan);
            var setBody  = new BlockStmt(new List<Stmt> { new ExprStmt(assign, propSpan) }, propSpan);
            var setter   = new FunctionDecl($"set_{propName}",
                new List<Param> { new Param("value", propType, null, propSpan) },
                new VoidType(propSpan), setBody, memberVis, mods, null, propSpan);
            accessors.Add(setter);
        }
        return (backing, accessors);
    }

    /// Parse auto-property in an interface body — produces method signatures only
    /// (no backing field, no body).
    /// Returns (getter [+ setter] MethodSignatures).
    internal static List<MethodSignature> SynthesizeInterfaceAutoProp(
        ref TokenCursor cursor, Visibility memberVis, bool isStatic,
        TypeExpr propType, string propName, Span propSpan)
    {
        var (_, hasSet) = ParseAutoPropAccessors(ref cursor, memberVis, propSpan);
        var result = new List<MethodSignature>
        {
            new MethodSignature($"get_{propName}", new List<Param>(), propType, propSpan, isStatic, false, null),
        };
        if (hasSet)
        {
            var setParams = new List<Param> { new Param("value", propType, null, propSpan) };
            result.Add(new MethodSignature($"set_{propName}", setParams, new VoidType(propSpan),
                propSpan, isStatic, false, null));
        }
        return result;
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
