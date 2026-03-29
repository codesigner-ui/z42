namespace Z42.Compiler.Lexer;

/// Single registration point for token metadata.
/// When adding a new keyword or type keyword, only this file and TokenKind.cs need to change.
/// Lexer, TypeParser, and Combinators all read from here.
internal static class TokenDefs
{
    // ── Keywords ──────────────────────────────────────────────────────────────
    // keyword text → TokenKind (used by Lexer)

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
}
