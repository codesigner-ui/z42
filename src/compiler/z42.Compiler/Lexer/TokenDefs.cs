namespace Z42.Compiler.Lexer;

/// Static token metadata — no combinator dependency.
/// Contains everything that is purely declarative: keyword maps, symbol tables,
/// type-keyword sets, display names, and numeric suffix characters.
///
/// To add a new keyword : add one entry to Keywords (+ TokenKind.cs).
/// To add a new symbol  : add one entry to SymbolRules (longer variants first).
/// For combinator-based rules (numbers, strings) see LexRules.cs.
internal static class TokenDefs
{
    // ── Keywords ──────────────────────────────────────────────────────────────
    // keyword text → TokenKind (used by Lexer.LexIdentOrKeyword)

    internal static readonly IReadOnlyDictionary<string, TokenKind> Keywords =
        new Dictionary<string, TokenKind>
    {
        // Control flow
        ["return"]   = TokenKind.Return,
        ["if"]       = TokenKind.If,
        ["else"]     = TokenKind.Else,
        ["while"]    = TokenKind.While,
        ["for"]      = TokenKind.For,
        ["foreach"]  = TokenKind.Foreach,
        ["in"]       = TokenKind.In,
        ["do"]       = TokenKind.Do,
        ["break"]    = TokenKind.Break,
        ["continue"] = TokenKind.Continue,
        ["switch"]   = TokenKind.Switch,
        ["case"]     = TokenKind.Case,
        ["default"]  = TokenKind.Default,

        // Exceptions
        ["try"]     = TokenKind.Try,
        ["catch"]   = TokenKind.Catch,
        ["finally"] = TokenKind.Finally,
        ["throw"]   = TokenKind.Throw,

        // Declarations
        ["namespace"] = TokenKind.Namespace,
        ["using"]     = TokenKind.Using,
        ["var"]       = TokenKind.Var,
        ["static"]    = TokenKind.Static,
        ["public"]    = TokenKind.Public,
        ["private"]   = TokenKind.Private,
        ["protected"] = TokenKind.Protected,
        ["internal"]  = TokenKind.Internal,
        ["class"]     = TokenKind.Class,
        ["struct"]    = TokenKind.Struct,
        ["enum"]      = TokenKind.Enum,
        ["interface"] = TokenKind.Interface,
        ["record"]    = TokenKind.Record,
        ["abstract"]  = TokenKind.Abstract,
        ["sealed"]    = TokenKind.Sealed,
        ["override"]  = TokenKind.Override,
        ["virtual"]   = TokenKind.Virtual,
        ["new"]       = TokenKind.New,
        ["async"]     = TokenKind.Async,
        ["await"]     = TokenKind.Await,

        // Type tests / conversions
        ["typeof"] = TokenKind.Typeof,
        ["is"]     = TokenKind.Is,
        ["as"]     = TokenKind.As,

        // Literals
        ["null"]  = TokenKind.Null,
        ["true"]  = TokenKind.True,
        ["false"] = TokenKind.False,

        // C# type names
        ["string"] = TokenKind.String,
        ["int"]    = TokenKind.Int,
        ["long"]   = TokenKind.Long,
        ["short"]  = TokenKind.Short,
        ["double"] = TokenKind.Double,
        ["float"]  = TokenKind.Float,
        ["byte"]   = TokenKind.Byte,
        ["uint"]   = TokenKind.Uint,
        ["ulong"]  = TokenKind.Ulong,
        ["ushort"] = TokenKind.Ushort,
        ["sbyte"]  = TokenKind.Sbyte,
        ["object"] = TokenKind.Object,
        ["bool"]   = TokenKind.Bool,
        ["char"]   = TokenKind.Char,
        ["void"]   = TokenKind.Void,

        // Explicit-size aliases
        ["i8"]  = TokenKind.I8,
        ["i16"] = TokenKind.I16,
        ["i32"] = TokenKind.I32,
        ["i64"] = TokenKind.I64,
        ["u8"]  = TokenKind.U8,
        ["u16"] = TokenKind.U16,
        ["u32"] = TokenKind.U32,
        ["u64"] = TokenKind.U64,
        ["f32"] = TokenKind.F32,
        ["f64"] = TokenKind.F64,

        // Legacy z42 keywords (reserved for Phase 2)
        ["fn"]     = TokenKind.Fn,
        ["let"]    = TokenKind.Let,
        ["mut"]    = TokenKind.Mut,
        ["trait"]  = TokenKind.Trait,
        ["impl"]   = TokenKind.Impl,
        ["use"]    = TokenKind.Use,
        ["module"] = TokenKind.Module,
        ["spawn"]  = TokenKind.Spawn,
        ["none"]   = TokenKind.None,
    };

    // ── Type keywords ─────────────────────────────────────────────────────────
    // Which TokenKinds are valid as the start of a type expression (used by TypeParser)

    internal static readonly IReadOnlySet<TokenKind> TypeKeywords =
        new HashSet<TokenKind>
    {
        TokenKind.Void,   TokenKind.String, TokenKind.Int,    TokenKind.Long,
        TokenKind.Short,  TokenKind.Double, TokenKind.Float,  TokenKind.Byte,
        TokenKind.Uint,   TokenKind.Ulong,  TokenKind.Ushort, TokenKind.Sbyte,
        TokenKind.Object, TokenKind.Bool,   TokenKind.Char,
        TokenKind.I8,  TokenKind.I16, TokenKind.I32, TokenKind.I64,
        TokenKind.U8,  TokenKind.U16, TokenKind.U32, TokenKind.U64,
        TokenKind.F32, TokenKind.F64,
        TokenKind.Identifier,
    };

    // ── Display names ─────────────────────────────────────────────────────────
    // Human-readable token names for error messages (used by Combinators.KindDisplay)

    private static readonly IReadOnlyDictionary<TokenKind, string> s_display =
        new Dictionary<TokenKind, string>
    {
        [TokenKind.LParen]    = "(",
        [TokenKind.RParen]    = ")",
        [TokenKind.LBrace]    = "{",
        [TokenKind.RBrace]    = "}",
        [TokenKind.LBracket]  = "[",
        [TokenKind.RBracket]  = "]",
        [TokenKind.Semicolon] = ";",
        [TokenKind.Comma]     = ",",
        [TokenKind.Colon]     = ":",
        [TokenKind.Eq]        = "=",
        [TokenKind.Dot]       = ".",
        [TokenKind.Arrow]     = "->",
        [TokenKind.FatArrow]  = "=>",
        [TokenKind.Question]  = "?",
    };

    internal static string Display(TokenKind k) =>
        s_display.TryGetValue(k, out var d) ? d : k.ToString().ToLower();

    // ── Symbol rules ──────────────────────────────────────────────────────────
    // Longest-match: multi-char entries must appear before their single-char prefixes.
    // Used by Lexer.TryLexSymbol — no manual Peek() chains needed.

    internal sealed record SymbolRule(string Text, TokenKind Kind);

    internal static readonly IReadOnlyList<SymbolRule> SymbolRules =
    [
        // 3-char (none currently, placeholder for future)

        // 2-char
        new("++",  TokenKind.PlusPlus),
        new("+=",  TokenKind.PlusEq),
        new("--",  TokenKind.MinusMinus),
        new("-=",  TokenKind.MinusEq),
        new("->",  TokenKind.Arrow),
        new("*=",  TokenKind.StarEq),
        new("/=",  TokenKind.SlashEq),
        new("%=",  TokenKind.PercentEq),
        new("==",  TokenKind.EqEq),
        new("=>",  TokenKind.FatArrow),
        new("!=",  TokenKind.BangEq),
        new("<=",  TokenKind.LtEq),
        new("<<",  TokenKind.LtLt),
        new(">=",  TokenKind.GtEq),
        new(">>",  TokenKind.GtGt),
        new("&&",  TokenKind.AmpAmp),
        new("&=",  TokenKind.AmpEq),
        new("||",  TokenKind.PipePipe),
        new("|=",  TokenKind.PipeEq),
        new("^=",  TokenKind.CaretEq),
        new("??",  TokenKind.QuestionQuestion),
        new("..",  TokenKind.DotDot),
        new("::",  TokenKind.ColonColon),

        // 1-char
        new("+",   TokenKind.Plus),
        new("-",   TokenKind.Minus),
        new("*",   TokenKind.Star),
        new("/",   TokenKind.Slash),
        new("%",   TokenKind.Percent),
        new("=",   TokenKind.Eq),
        new("!",   TokenKind.Bang),
        new("<",   TokenKind.Lt),
        new(">",   TokenKind.Gt),
        new("&",   TokenKind.Ampersand),
        new("|",   TokenKind.Pipe),
        new("^",   TokenKind.Caret),
        new("~",   TokenKind.Tilde),
        new("(",   TokenKind.LParen),
        new(")",   TokenKind.RParen),
        new("{",   TokenKind.LBrace),
        new("}",   TokenKind.RBrace),
        new("[",   TokenKind.LBracket),
        new("]",   TokenKind.RBracket),
        new(",",   TokenKind.Comma),
        new(".",   TokenKind.Dot),
        new(":",   TokenKind.Colon),
        new(";",   TokenKind.Semicolon),
        new("?",   TokenKind.Question),
        new("#",   TokenKind.Hash),
    ];

    // Pre-indexed by first character for O(1) dispatch
    internal static readonly IReadOnlyDictionary<char, IReadOnlyList<SymbolRule>> SymbolIndex =
        SymbolRules
            .GroupBy(r => r.Text[0])
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SymbolRule>)g.ToList());

    // ── Numeric suffixes ──────────────────────────────────────────────────────
    // Characters that may follow a numeric literal as a type suffix (L, u, f, d, …)

    internal static readonly IReadOnlySet<char> NumericSuffixes =
        new HashSet<char> { 'L', 'l', 'u', 'U', 'f', 'F', 'd', 'D', 'm', 'M' };
}
