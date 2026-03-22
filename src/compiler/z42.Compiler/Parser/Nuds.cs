using Z42.Compiler.Lexer;

namespace Z42.Compiler.Parser;

/// Prefix / atom Nud handlers. Each returns an Expr; the triggering token
/// has already been consumed and is provided as <paramref name="tok"/>.
internal static class Nuds
{
    // ── Prefix unary factory ─────────────────────────────────────────────────

    /// Returns a NudFn that wraps the next expression in a UnaryExpr.
    /// Uses bp=85 so that postfix operators (bp=90) bind tighter, but binary
    /// operators (max bp=80) do not steal the operand.
    public static NudFn PrefixUnary(string op) => (ctx, tok) =>
        new UnaryExpr(op, ctx.ParseExpr(85), tok.Span);

    public static readonly NudFn Await_ = (ctx, tok) =>
        new UnaryExpr("await", ctx.ParseExpr(85), tok.Span);

    // ── Literals ─────────────────────────────────────────────────────────────

    public static readonly NudFn IntLit = (ctx, tok) =>
    {
        var text = tok.Text.Replace("_", "").TrimEnd('L', 'l', 'u', 'U');
        long value;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = Convert.ToInt64(text[2..], 16);
        else if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            value = Convert.ToInt64(text[2..], 2);
        else
            value = long.Parse(text);
        return new LitIntExpr(value, tok.Span);
    };

    public static readonly NudFn FloatLit = (ctx, tok) =>
    {
        var text = tok.Text.Replace("_", "").TrimEnd('f', 'F', 'd', 'D', 'm', 'M');
        return new LitFloatExpr(
            double.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            tok.Span);
    };

    public static readonly NudFn StringLit = (ctx, tok) =>
        new LitStrExpr(tok.Text[1..^1], tok.Span);   // strip outer quotes

    public static readonly NudFn InterpolatedStr_ = (ctx, tok) =>
        ctx.ParseInterpolatedString(tok);

    public static readonly NudFn BoolLit = (ctx, tok) =>
        new LitBoolExpr(tok.Kind == TokenKind.True, tok.Span);

    public static readonly NudFn NullLit = (ctx, tok) =>
        new LitNullExpr(tok.Span);

    public static readonly NudFn CharLit = (ctx, tok) =>
    {
        char c = tok.Text.Length >= 3 ? tok.Text[1] : '\0';
        return new LitCharExpr(c, tok.Span);
    };

    public static readonly NudFn Ident_ = (ctx, tok) =>
        new IdentExpr(tok.Text, tok.Span);

    // ── new expression ───────────────────────────────────────────────────────
    //   new T(args)          — object constructor
    //   new T[] { e, ... }   — array literal   (ParseTypeExpr consumed "[]")
    //   new T[n]             — array with size  (ParseTypeExpr stopped at "[n]")

    public static readonly NudFn New_ = (ctx, tok) =>
    {
        var ty = ctx.ParseTypeExpr();

        // new T[] { e0, e1, ... }  (ty is already ArrayType because ParseTypeExpr saw "[]")
        if (ty is ArrayType arrTy && ctx.Check(TokenKind.LBrace))
        {
            ctx.Advance(); // consume {
            var elems = new List<Expr>();
            while (!ctx.Check(TokenKind.RBrace) && !ctx.Check(TokenKind.Eof))
            {
                elems.Add(ctx.ParseExpr());
                if (!ctx.Match(TokenKind.Comma)) break;
            }
            ctx.Expect(TokenKind.RBrace);
            return new ArrayLitExpr(arrTy.Element, elems, tok.Span);
        }

        // new T[n]  (ParseTypeExpr returned NamedType because it saw "[n]" not "[]")
        if (ctx.Check(TokenKind.LBracket))
        {
            ctx.Advance(); // consume [
            var size = ctx.ParseExpr();
            ctx.Expect(TokenKind.RBracket);
            return new ArrayCreateExpr(ty, size, tok.Span);
        }

        // new T(args)  — regular object constructor
        ctx.Expect(TokenKind.LParen);
        var args = new List<Expr>();
        while (!ctx.Check(TokenKind.RParen) && !ctx.Check(TokenKind.Eof))
        {
            args.Add(ctx.ParseExpr());
            if (!ctx.Match(TokenKind.Comma)) break;
        }
        ctx.Expect(TokenKind.RParen);
        return new NewExpr(ty, args, tok.Span);
    };

    // ── typeof ───────────────────────────────────────────────────────────────

    /// `typeof(T)` — desugars at parse time to a string literal with the type name.
    public static readonly NudFn Typeof_ = (ctx, tok) =>
    {
        ctx.Expect(TokenKind.LParen);
        var ty = ctx.ParseTypeExpr();
        ctx.Expect(TokenKind.RParen);
        var typeName = ty switch
        {
            NamedType  nt => nt.Name,
            ArrayType  at => at.Element is NamedType ne ? $"{ne.Name}[]" : "array",
            VoidType      => "void",
            _             => ty.ToString() ?? "unknown",
        };
        return new LitStrExpr(typeName, tok.Span);
    };

    // ── Grouped / cast ───────────────────────────────────────────────────────

    /// `(expr)` grouping OR `(TypeName)expr` cast.
    /// Decision: if the token directly inside `(` is a type keyword and the
    /// token after it is `)`, treat as cast (same heuristic as the old parser).
    public static readonly NudFn LParenNud = (ctx, tok) =>
    {
        // At this point `(` has been consumed; ctx.Current is the first token inside.
        if (ctx.IsTypeToken(ctx.Current.Kind) && ctx.Peek().Kind == TokenKind.RParen)
        {
            var ty = ctx.ParseTypeExpr();
            ctx.Expect(TokenKind.RParen);
            return new CastExpr(ty, ctx.ParseExpr(85), tok.Span);
        }
        var inner = ctx.ParseExpr();
        ctx.Expect(TokenKind.RParen);
        return inner;
    };
}
