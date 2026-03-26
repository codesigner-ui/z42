using Z42.Compiler.Lexer;

namespace Z42.Compiler.Parser;

// ── Delegate types ────────────────────────────────────────────────────────────

/// Prefix / atom handler: the triggering token has already been consumed.
internal delegate Expr NudFn(ParserContext ctx, Token tok);

/// Infix / postfix handler: the triggering token has already been consumed.
internal delegate Expr LedFn(ParserContext ctx, Expr left, Token tok);

/// Statement handler: the triggering keyword has already been consumed.
internal delegate Stmt StmtFn(ParserContext ctx, Token keyword);

// ── Rule records ──────────────────────────────────────────────────────────────

/// Expression parse rule for one token kind.
internal sealed record ParseRule(
    int     LeftBp,           // binding power when appearing as Led (0 = prefix-only)
    NudFn?  Nud,              // prefix / atom handler (null = not valid as prefix)
    LedFn?  Led,              // infix / postfix handler (null = not valid as infix)
    string? Feature = null);  // snake_case feature name; null = always enabled

/// Statement parse rule for one keyword token.
internal sealed record StmtRule(
    StmtFn  Fn,
    string? Feature = null);

// ── Parse table (single source of truth for operator precedence & feature gates) ─

/// Binding power levels (spaced to allow future insertions):
///   10  assignment  (right-assoc)
///   20  ternary / null-coalesce
///   30  logical-or  (||)
///   40  logical-and (&&)
///   44  bitwise-or  (|)       [feat:bitwise]
///   46  bitwise-xor (^)       [feat:bitwise]
///   48  bitwise-and (&)       [feat:bitwise]
///   50  equality    (== !=)
///   60  relational / is / as
///   65  bit-shifts  (<< >>)   [feat:bitwise]
///   70  additive
///   80  multiplicative
///   85  (conceptual unary right-operand floor — not a Led level)
///   90  postfix / call / member / index
internal static class ParseTable
{
    internal static readonly IReadOnlyDictionary<TokenKind, ParseRule> ExprRules =
        new Dictionary<TokenKind, ParseRule>
        {
            // ── Assignment (right-assoc, bp=10 → right side uses minBp=9) ────────
            [TokenKind.Eq]        = new(10, null, Leds.Assign),
            [TokenKind.PlusEq]    = new(10, null, Leds.CompoundAssign("+")),
            [TokenKind.MinusEq]   = new(10, null, Leds.CompoundAssign("-")),
            [TokenKind.StarEq]    = new(10, null, Leds.CompoundAssign("*")),
            [TokenKind.SlashEq]   = new(10, null, Leds.CompoundAssign("/")),
            [TokenKind.PercentEq] = new(10, null, Leds.CompoundAssign("%")),
            [TokenKind.AmpEq]     = new(10, null, Leds.CompoundAssign("&"),  "bitwise"),
            [TokenKind.PipeEq]    = new(10, null, Leds.CompoundAssign("|"),  "bitwise"),
            [TokenKind.CaretEq]   = new(10, null, Leds.CompoundAssign("^"),  "bitwise"),

            // ── Ternary / null-conditional (bp=20; feature handled inside Led) ──
            [TokenKind.Question]         = new(20, null, Leds.QuestionLed),
            // ── Null-coalescing (bp=20, right-assoc → right uses minBp=19) ──────
            [TokenKind.QuestionQuestion] = new(20, null, Leds.NullCoalesce, "null_coalesce"),

            // ── Logical (bp=30/40) ────────────────────────────────────────────
            [TokenKind.PipePipe] = new(30, null, Leds.BinaryLeft("||", 30)),
            [TokenKind.AmpAmp]   = new(40, null, Leds.BinaryLeft("&&", 40)),

            // ── Bitwise OR/XOR/AND (bp=44/46/48) ─────────────────────────────
            [TokenKind.Pipe]      = new(44, null, Leds.BinaryLeft("|",  44), "bitwise"),
            [TokenKind.Caret]     = new(46, null, Leds.BinaryLeft("^",  46), "bitwise"),
            [TokenKind.Ampersand] = new(48, null, Leds.BinaryLeft("&",  48), "bitwise"),

            // ── Equality (bp=50) ──────────────────────────────────────────────
            [TokenKind.EqEq]   = new(50, null, Leds.BinaryLeft("==", 50)),
            [TokenKind.BangEq] = new(50, null, Leds.BinaryLeft("!=", 50)),

            // ── Relational / type tests (bp=60) ──────────────────────────────
            [TokenKind.Lt]   = new(60, null, Leds.BinaryLeft("<",  60)),
            [TokenKind.LtEq] = new(60, null, Leds.BinaryLeft("<=", 60)),
            [TokenKind.Gt]   = new(60, null, Leds.BinaryLeft(">",  60)),
            [TokenKind.GtEq] = new(60, null, Leds.BinaryLeft(">=", 60)),
            [TokenKind.Is]   = new(60, null, Leds.Is_),
            [TokenKind.As]   = new(60, null, Leds.BinaryLeft("as", 60)),

            // ── Bit-shifts (bp=65) — between relational (60) and additive (70) ─
            [TokenKind.LtLt] = new(65, null, Leds.BinaryLeft("<<", 65), "bitwise"),
            [TokenKind.GtGt] = new(65, null, Leds.BinaryLeft(">>", 65), "bitwise"),

            // ── Additive (bp=70) — Plus/Minus also have prefix Nud ───────────
            [TokenKind.Plus]  = new(70, Nuds.PrefixUnary("+"), Leds.BinaryLeft("+", 70)),
            [TokenKind.Minus] = new(70, Nuds.PrefixUnary("-"), Leds.BinaryLeft("-", 70)),

            // ── Multiplicative (bp=80) ────────────────────────────────────────
            [TokenKind.Star]    = new(80, null, Leds.BinaryLeft("*", 80)),
            [TokenKind.Slash]   = new(80, null, Leds.BinaryLeft("/", 80)),
            [TokenKind.Percent] = new(80, null, Leds.BinaryLeft("%", 80)),

            // ── Prefix-only (LeftBp=0 → never triggers as Led) ───────────────
            [TokenKind.Bang]  = new(0, Nuds.PrefixUnary("!"), null),
            [TokenKind.Tilde] = new(0, Nuds.PrefixUnary("~"), null),
            [TokenKind.Await] = new(0, Nuds.Await_, null),

            // ── Prefix AND postfix (PlusPlus/MinusMinus share same kind) ─────
            // Nud = prefix, Led = postfix (bp=90 > unary=85, so postfix binds tighter)
            [TokenKind.PlusPlus]   = new(90, Nuds.PrefixUnary("++"), Leds.Postfix_("++") ),
            [TokenKind.MinusMinus] = new(90, Nuds.PrefixUnary("--"), Leds.Postfix_("--") ),

            // ── Postfix / call / member (bp=90) ──────────────────────────────
            // LParen: Nud = grouped/cast, Led = function call
            [TokenKind.LParen]   = new(90, Nuds.LParenNud, Leds.Call_),
            [TokenKind.Dot]      = new(90, null, Leds.MemberAccess_),
            [TokenKind.LBracket] = new(90, null, Leds.IndexAccess_),

            // ── Switch expression (postfix `expr switch { ... }`, bp=85) ──────
            [TokenKind.Switch] = new(85, null, Leds.SwitchExpr_, "pattern_match"),

            // ── Atoms (Nud-only, LeftBp=0) ────────────────────────────────────
            [TokenKind.IntLiteral]               = new(0, Nuds.IntLit,    null),
            [TokenKind.FloatLiteral]             = new(0, Nuds.FloatLit,  null),
            [TokenKind.StringLiteral]            = new(0, Nuds.StringLit, null),
            [TokenKind.CharLiteral]              = new(0, Nuds.CharLit,   null),
            [TokenKind.InterpolatedStringLiteral]= new(0, Nuds.InterpolatedStr_, null),
            [TokenKind.True]       = new(0, Nuds.BoolLit,  null),
            [TokenKind.False]      = new(0, Nuds.BoolLit,  null),
            [TokenKind.Null]       = new(0, Nuds.NullLit,  null),
            [TokenKind.Identifier] = new(0, Nuds.Ident_,   null),
            [TokenKind.New]        = new(0, Nuds.New_,      null),
            [TokenKind.Typeof]     = new(0, Nuds.Typeof_,   null),

            // ── Type keywords usable as static-call targets: string.Join, int.Parse, … ─
            [TokenKind.String] = new(0, Nuds.TypeKeyword("string"), null),
            [TokenKind.Int]    = new(0, Nuds.TypeKeyword("int"),    null),
            [TokenKind.Long]   = new(0, Nuds.TypeKeyword("long"),   null),
            [TokenKind.Double] = new(0, Nuds.TypeKeyword("double"), null),
            [TokenKind.Float]  = new(0, Nuds.TypeKeyword("float"),  null),
            [TokenKind.Bool]   = new(0, Nuds.TypeKeyword("bool"),   null),
            [TokenKind.Char]   = new(0, Nuds.TypeKeyword("char"),   null),
        };

    internal static readonly IReadOnlyDictionary<TokenKind, StmtRule> StmtRules =
        new Dictionary<TokenKind, StmtRule>
        {
            [TokenKind.Var]     = new(Stmts.VarDecl_),
            [TokenKind.Return]  = new(Stmts.Return_),
            [TokenKind.If]      = new(Stmts.If_,      "control_flow"),
            [TokenKind.While]   = new(Stmts.While_,   "control_flow"),
            [TokenKind.Do]      = new(Stmts.DoWhile_,  "control_flow"),
            [TokenKind.For]     = new(Stmts.For_,      "control_flow"),
            [TokenKind.Foreach]  = new(Stmts.Foreach_,  "control_flow"),
            [TokenKind.Break]    = new(Stmts.Break_,    "control_flow"),
            [TokenKind.Continue] = new(Stmts.Continue_, "control_flow"),
            [TokenKind.Switch]   = new(Stmts.Switch_,   "pattern_match"),
            [TokenKind.Try]      = new(Stmts.TryCatch_, "exceptions"),
            [TokenKind.Throw]    = new(Stmts.Throw_,    "exceptions"),
        };
}
