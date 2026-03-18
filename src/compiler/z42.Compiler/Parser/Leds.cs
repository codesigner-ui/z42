using Z42.Compiler.Lexer;

namespace Z42.Compiler.Parser;

/// Infix / postfix Led handlers. Each receives the already-consumed operator
/// token and the left-hand expression; returns the combined Expr.
internal static class Leds
{
    // ── Binary left-associative factory ─────────────────────────────────────

    /// Left-associative binary operator. `bp` = this operator's left-binding-power;
    /// the right operand is parsed with `ParseExpr(bp)` so same-precedence
    /// operators on the right are NOT absorbed (left-to-right fold).
    public static LedFn BinaryLeft(string op, int bp) => (ctx, left, tok) =>
        new BinaryExpr(op, left, ctx.ParseExpr(bp), left.Span);

    // ── Assignment ───────────────────────────────────────────────────────────

    /// Plain assignment `=` (right-associative: minBp = leftBp - 1 = 9).
    public static readonly LedFn Assign = (ctx, left, tok) =>
        new AssignExpr(left, ctx.ParseExpr(9), left.Span);

    /// Compound assignment `op=` — desugars to `left = left op right`.
    public static LedFn CompoundAssign(string binOp) => (ctx, left, tok) =>
    {
        var right    = ctx.ParseExpr(9);
        var combined = new BinaryExpr(binOp, left, right, left.Span);
        return new AssignExpr(left, combined, left.Span);
    };

    // ── Postfix ──────────────────────────────────────────────────────────────

    /// Postfix `++` / `--`; does not consume any further tokens.
    public static LedFn Postfix_(string op) => (ctx, left, tok) =>
        new PostfixExpr(op, left, left.Span);

    // ── Call / member / index (bp=90) ────────────────────────────────────────

    /// Function / method call: `f(arg, ...)` — `(` already consumed.
    public static readonly LedFn Call_ = (ctx, left, tok) =>
    {
        var args = new List<Expr>();
        while (!ctx.Check(TokenKind.RParen) && !ctx.Check(TokenKind.Eof))
        {
            args.Add(ctx.ParseExpr());
            if (!ctx.Match(TokenKind.Comma)) break;
        }
        ctx.Expect(TokenKind.RParen);
        return new CallExpr(left, args, left.Span);
    };

    /// Member access `a.member` — `.` already consumed.
    public static readonly LedFn MemberAccess_ = (ctx, left, tok) =>
    {
        var member = ctx.Expect(TokenKind.Identifier).Text;
        return new MemberExpr(left, member, left.Span);
    };

    /// Index access `a[i]` — `[` already consumed.
    public static readonly LedFn IndexAccess_ = (ctx, left, tok) =>
    {
        var index = ctx.ParseExpr();
        ctx.Expect(TokenKind.RBracket);
        return new IndexExpr(left, index, left.Span);
    };

    // ── Switch expression (postfix `switch { arm, ... }`, bp=85) ────────────

    public static readonly LedFn SwitchExpr_ = (ctx, left, tok) =>
    {
        ctx.Expect(TokenKind.LBrace);
        var arms = new List<SwitchArm>();
        while (!ctx.Check(TokenKind.RBrace) && !ctx.Check(TokenKind.Eof))
        {
            var aSpan = ctx.Current.Span;
            Expr? pattern;
            if (ctx.Current.Kind == TokenKind.Underscore)
            {
                ctx.Advance();
                pattern = null;
            }
            else
            {
                pattern = ctx.ParseExpr(11);
            }
            ctx.Expect(TokenKind.FatArrow);
            var body = ctx.ParseExpr(11);
            arms.Add(new SwitchArm(pattern, body, aSpan));
            if (!ctx.Match(TokenKind.Comma)) break;
        }
        ctx.Expect(TokenKind.RBrace);
        return new SwitchExpr(left, arms, tok.Span);
    };

    // ── Ternary / null-conditional (both triggered by `?`) ───────────────────

    /// Handles `cond ? then : else` (ternary) and `obj?.member` (null-conditional).
    /// Feature checking is done inside the handler so that `?.` remains usable
    /// even when the "ternary" feature is disabled.
    public static readonly LedFn QuestionLed = (ctx, left, tok) =>
    {
        if (ctx.Current.Kind == TokenKind.Dot)
        {
            // null-conditional: obj?.member
            ctx.Advance();
            var member = ctx.Expect(TokenKind.Identifier).Text;
            return new MemberExpr(left, "?." + member, left.Span);
        }
        // ternary
        ctx.RequireFeature("ternary", tok.Span);
        var then  = ctx.ParseExpr();
        ctx.Expect(TokenKind.Colon);
        var else_ = ctx.ParseExpr();
        return new ConditionalExpr(left, then, else_, left.Span);
    };
}
