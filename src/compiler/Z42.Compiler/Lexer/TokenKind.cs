namespace Z42.Compiler.Lexer;

public enum TokenKind
{
    // Literals
    IntLiteral, FloatLiteral, StringLiteral, CharLiteral, True, False,

    // Identifiers & keywords
    Identifier,
    Fn, Let, Mut, Return, If, Else, While, For, In,
    Match, Struct, Enum, Trait, Impl, Use, Module,
    Async, Await, Spawn,
    None, Error,

    // Types
    I8, I16, I32, I64, U8, U16, U32, U64, F32, F64,
    Bool, Char, Str, Void,

    // Symbols
    LParen, RParen, LBrace, RBrace, LBracket, RBracket,
    Comma, Dot, DotDot, Colon, ColonColon, Semicolon,
    Arrow,          // ->
    FatArrow,       // =>
    Question,       // ?
    Bang,           // !
    Ampersand, Pipe, Caret, Tilde,
    Plus, Minus, Star, Slash, Percent,
    Eq, EqEq, BangEq, Lt, LtEq, Gt, GtEq,
    LtLt, GtGt,     // << >>
    AmpAmp, PipePipe,
    PlusEq, MinusEq, StarEq, SlashEq, PercentEq,

    // Attributes
    Hash, LBracketHash, // #[

    // Special
    Underscore,
    Newline,
    Eof,
    Error_Unknown,
}
