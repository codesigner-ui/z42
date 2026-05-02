using Z42.Core.Features;

namespace Z42.Syntax.Lexer;

/// Static token metadata — no combinator dependency.
/// Contains everything that is purely declarative: keyword maps, symbol tables,
/// type-keyword sets, display names, and numeric suffix characters.
///
/// To add a new keyword : add one entry to KeywordDefs (+ TokenKind.cs).
/// To add a new symbol  : add one entry to SymbolRules (longer variants first).
/// For combinator-based rules (numbers, strings) see LexRules.cs.
internal static class TokenDefs
{
    // ── Keyword definitions (single source of truth) ─────────────────────────
    // Each keyword is annotated with its language phase.
    // Phase1 = available in current compiler; Phase2 = reserved for future use.

    internal sealed record KeywordDef(string Text, TokenKind Kind, LanguagePhase Phase);

    internal static readonly IReadOnlyList<KeywordDef> KeywordDefs =
    [
        // Control flow (Phase 1)
        new("return",   TokenKind.Return,   LanguagePhase.Phase1),
        new("if",       TokenKind.If,       LanguagePhase.Phase1),
        new("else",     TokenKind.Else,     LanguagePhase.Phase1),
        new("while",    TokenKind.While,    LanguagePhase.Phase1),
        new("for",      TokenKind.For,      LanguagePhase.Phase1),
        new("foreach",  TokenKind.Foreach,  LanguagePhase.Phase1),
        new("in",       TokenKind.In,       LanguagePhase.Phase1),
        new("do",       TokenKind.Do,       LanguagePhase.Phase1),
        new("break",    TokenKind.Break,    LanguagePhase.Phase1),
        new("continue", TokenKind.Continue, LanguagePhase.Phase1),
        new("switch",   TokenKind.Switch,   LanguagePhase.Phase1),
        new("case",     TokenKind.Case,     LanguagePhase.Phase1),
        new("default",  TokenKind.Default,  LanguagePhase.Phase1),

        // Exceptions (Phase 1)
        new("try",     TokenKind.Try,     LanguagePhase.Phase1),
        new("catch",   TokenKind.Catch,   LanguagePhase.Phase1),
        new("finally", TokenKind.Finally, LanguagePhase.Phase1),
        new("throw",   TokenKind.Throw,   LanguagePhase.Phase1),

        // Native interop (Phase 1 — spec C5 `impl-pinned-syntax`)
        new("pinned",  TokenKind.Pinned,  LanguagePhase.Phase1),

        // Native interop (Phase 1 — spec C11a `manifest-reader-import`)
        new("import",  TokenKind.Import,  LanguagePhase.Phase1),

        // Declarations (Phase 1)
        new("namespace", TokenKind.Namespace, LanguagePhase.Phase1),
        new("using",     TokenKind.Using,     LanguagePhase.Phase1),
        new("var",       TokenKind.Var,       LanguagePhase.Phase1),
        new("static",    TokenKind.Static,    LanguagePhase.Phase1),
        new("public",    TokenKind.Public,    LanguagePhase.Phase1),
        new("private",   TokenKind.Private,   LanguagePhase.Phase1),
        new("protected", TokenKind.Protected, LanguagePhase.Phase1),
        new("internal",  TokenKind.Internal,  LanguagePhase.Phase1),
        new("class",     TokenKind.Class,     LanguagePhase.Phase1),
        new("struct",    TokenKind.Struct,    LanguagePhase.Phase1),
        new("enum",      TokenKind.Enum,      LanguagePhase.Phase1),
        new("interface", TokenKind.Interface, LanguagePhase.Phase1),
        new("record",    TokenKind.Record,    LanguagePhase.Phase1),
        new("delegate",  TokenKind.Delegate,  LanguagePhase.Phase1),
        new("abstract",  TokenKind.Abstract,  LanguagePhase.Phase1),
        new("extern",    TokenKind.Extern,    LanguagePhase.Phase1),
        new("sealed",    TokenKind.Sealed,    LanguagePhase.Phase1),
        new("override",  TokenKind.Override,  LanguagePhase.Phase1),
        new("virtual",   TokenKind.Virtual,   LanguagePhase.Phase1),
        new("new",       TokenKind.New,       LanguagePhase.Phase1),
        new("async",     TokenKind.Async,     LanguagePhase.Phase1),
        new("await",     TokenKind.Await,     LanguagePhase.Phase1),

        // Type tests / conversions (Phase 1)
        new("typeof", TokenKind.Typeof, LanguagePhase.Phase1),
        new("is",     TokenKind.Is,     LanguagePhase.Phase1),
        new("as",     TokenKind.As,     LanguagePhase.Phase1),

        // Generic constraints (Phase 1 — L3-G2)
        new("where",  TokenKind.Where,  LanguagePhase.Phase1),

        // Operator overloading (Phase 1 — L3)
        new("operator", TokenKind.Operator, LanguagePhase.Phase1),

        // Literals (Phase 1)
        new("null",  TokenKind.Null,  LanguagePhase.Phase1),
        new("true",  TokenKind.True,  LanguagePhase.Phase1),
        new("false", TokenKind.False, LanguagePhase.Phase1),

        // C# type names (Phase 1)
        new("string", TokenKind.String, LanguagePhase.Phase1),
        new("int",    TokenKind.Int,    LanguagePhase.Phase1),
        new("long",   TokenKind.Long,   LanguagePhase.Phase1),
        new("short",  TokenKind.Short,  LanguagePhase.Phase1),
        new("double", TokenKind.Double, LanguagePhase.Phase1),
        new("float",  TokenKind.Float,  LanguagePhase.Phase1),
        new("byte",   TokenKind.Byte,   LanguagePhase.Phase1),
        new("uint",   TokenKind.Uint,   LanguagePhase.Phase1),
        new("ulong",  TokenKind.Ulong,  LanguagePhase.Phase1),
        new("ushort", TokenKind.Ushort, LanguagePhase.Phase1),
        new("sbyte",  TokenKind.Sbyte,  LanguagePhase.Phase1),
        new("object", TokenKind.Object, LanguagePhase.Phase1),
        new("bool",   TokenKind.Bool,   LanguagePhase.Phase1),
        new("char",   TokenKind.Char,   LanguagePhase.Phase1),
        new("void",   TokenKind.Void,   LanguagePhase.Phase1),

        // Explicit-size aliases (Phase 1)
        new("i8",  TokenKind.I8,  LanguagePhase.Phase1),
        new("i16", TokenKind.I16, LanguagePhase.Phase1),
        new("i32", TokenKind.I32, LanguagePhase.Phase1),
        new("i64", TokenKind.I64, LanguagePhase.Phase1),
        new("u8",  TokenKind.U8,  LanguagePhase.Phase1),
        new("u16", TokenKind.U16, LanguagePhase.Phase1),
        new("u32", TokenKind.U32, LanguagePhase.Phase1),
        new("u64", TokenKind.U64, LanguagePhase.Phase1),
        new("f32", TokenKind.F32, LanguagePhase.Phase1),
        new("f64", TokenKind.F64, LanguagePhase.Phase1),

        // z42-native keywords (reserved — Phase 2)
        new("fn",     TokenKind.Fn,     LanguagePhase.Phase2),
        new("let",    TokenKind.Let,    LanguagePhase.Phase2),
        new("mut",    TokenKind.Mut,    LanguagePhase.Phase2),
        new("trait",  TokenKind.Trait,  LanguagePhase.Phase2),
        new("impl",   TokenKind.Impl,   LanguagePhase.Phase1),
        new("use",    TokenKind.Use,    LanguagePhase.Phase2),
        new("module", TokenKind.Module, LanguagePhase.Phase2),
        new("spawn",  TokenKind.Spawn,  LanguagePhase.Phase2),
        new("none",   TokenKind.None,   LanguagePhase.Phase2),
    ];

    // ── Keywords ──────────────────────────────────────────────────────────────
    // Derived lookup: keyword text → TokenKind (used by Lexer.LexIdentOrKeyword).
    // Includes ALL phases — reserved keywords are always lexed as keywords, never identifiers.

    internal static readonly IReadOnlyDictionary<string, TokenKind> Keywords =
        KeywordDefs.ToDictionary(d => d.Text, d => d.Kind);

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
