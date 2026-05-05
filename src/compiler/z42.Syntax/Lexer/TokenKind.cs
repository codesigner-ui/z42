namespace Z42.Syntax.Lexer;

public enum TokenKind
{
    // ── Literals ──────────────────────────────────────────────────────────────
    IntLiteral, FloatLiteral, StringLiteral, CharLiteral,
    InterpolatedStringLiteral,   // $"..."
    True, False,

    // ── Identifiers & keywords ─────────────────────────────────────────────
    Identifier,

    // C# keywords
    Namespace, Using, Var,
    Static, Public, Private, Protected, Internal,
    Ref, Out,        // parameter modifiers (spec: define-ref-out-in-parameters); In reuses foreach...in token below
    Class, Struct, Enum, Interface, Record, Delegate, Event,
    Abstract, Sealed, Override, Virtual, New, Extern,
    Async, Await,
    Return, If, Else, While, For, Foreach, In,
    Do, Break, Continue,
    Switch, Case, Default,
    Try, Catch, Finally, Throw,
    Pinned,         // `pinned p = s { ... }` — Tier 1 native interop borrow (spec C5)
    Import,         // `import T from "lib";` — manifest-driven native type import (spec C11a)
    Typeof, Is, As,
    Null,
    Where,          // L3-G2 generic constraints: `where T: I + J`
    Operator,       // L3 operator overloading: `public static T operator +(T a, T b)`

    // Legacy / z42-specific keywords (kept for future use)
    Fn, Let, Mut, Trait, Impl, Use, Module, Spawn, None, Error,

    // ── Type keywords ──────────────────────────────────────────────────────
    // C# type names
    String, Int, Long, Short, Double, Float, Byte,
    Uint, Ulong, Ushort, Sbyte, Object,
    Bool, Char, Void,
    // Explicit-size aliases
    I8, I16, I32, I64, U8, U16, U32, U64, F32, F64,

    // ── Symbols ───────────────────────────────────────────────────────────
    LParen, RParen, LBrace, RBrace, LBracket, RBracket,
    Comma, Dot, DotDot, Colon, ColonColon, Semicolon,
    Arrow,          // ->
    FatArrow,       // =>
    Question,       // ?
    QuestionQuestion, // ??
    Bang,           // !
    Ampersand, Pipe, Caret, Tilde,
    Plus, Minus, Star, Slash, Percent,
    Eq, EqEq, BangEq, Lt, LtEq, Gt, GtEq,
    LtLt, GtGt,
    AmpAmp, PipePipe,
    PlusEq, MinusEq, StarEq, SlashEq, PercentEq,
    PlusPlus, MinusMinus,   // ++ --
    AmpEq, PipeEq, CaretEq, // &= |= ^=

    // ── Attributes ────────────────────────────────────────────────────────
    Hash, LBracketHash,

    // ── Special ───────────────────────────────────────────────────────────
    Underscore,
    Newline,
    Eof,
    Error_Unknown,
}
