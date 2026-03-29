using System.Globalization;
using System.Text;
using Z42.Compiler.Features;
using Z42.Compiler.Lexer;
using Z42.Compiler.Parser.Core;

namespace Z42.Compiler.Parser;

/// Pratt expression parser.
/// All functions are pure: they take a TokenCursor and return ParseResult&lt;Expr&gt;.
/// NudFn / LedFn receive the cursor explicitly (no mutable ParserContext).
internal static class ExprParser
{
    // ── Delegate types ────────────────────────────────────────────────────────

    /// Prefix / atom handler: triggering token already consumed; `cursor` is after it.
    private delegate ParseResult<Expr> NudFn(
        TokenCursor cursor, Token tok, LanguageFeatures feat);

    /// Infix / postfix handler: operator token already consumed; `cursor` is after it.
    private delegate ParseResult<Expr> LedFn(
        TokenCursor cursor, Expr left, Token tok, LanguageFeatures feat);

    private sealed record NudEntry(NudFn Fn, string? Feature = null);
    private sealed record LedEntry(int Bp, LedFn Fn, string? Feature = null);

    // ── Entry point ───────────────────────────────────────────────────────────

    /// Parse an expression with the given minimum binding power (0 = full expression).
    internal static ParseResult<Expr> Parse(
        TokenCursor cursor, LanguageFeatures feat, int minBp = 0)
    {
        var span = cursor.Current.Span;

        if (!s_nudTable.TryGetValue(cursor.Current.Kind, out var nudEntry))
            return ParseResult<Expr>.Fail(cursor,
                $"unexpected token `{cursor.Current.Text}` in expression");

        if (nudEntry.Feature is { } nf && !feat.IsEnabled(nf))
            throw new ParseException($"feature `{nf}` is disabled", span);

        var nudTok = cursor.Current;
        cursor = cursor.Advance();
        var r = nudEntry.Fn(cursor, nudTok, feat);
        if (!r.IsOk) return r;
        cursor = r.Remainder;
        var left = r.Value;

        while (s_ledTable.TryGetValue(cursor.Current.Kind, out var ledEntry)
               && ledEntry.Bp > minBp)
        {
            if (ledEntry.Feature is { } lf && !feat.IsEnabled(lf))
                break;

            var ledTok = cursor.Current;
            cursor = cursor.Advance();
            var lr = ledEntry.Fn(cursor, left, ledTok, feat);
            if (!lr.IsOk) return lr;
            cursor = lr.Remainder;
            left   = lr.Value;
        }

        return ParseResult<Expr>.Ok(left, cursor);
    }

    // ── Led implementations (must be declared before s_ledTable) ─────────────

    private static LedFn BinaryLeft(string op, int bp) => (cursor, left, tok, feat) =>
        Parse(cursor, feat, bp).Map(right => (Expr)new BinaryExpr(op, left, right, left.Span));

    private static readonly LedFn Assign = (cursor, left, tok, feat) =>
        Parse(cursor, feat, 9).Map(right => (Expr)new AssignExpr(left, right, left.Span));

    private static LedFn CompoundAssign(string binOp) => (cursor, left, tok, feat) =>
        Parse(cursor, feat, 9).Map(right =>
            (Expr)new AssignExpr(left, new BinaryExpr(binOp, left, right, left.Span), left.Span));

    private static LedFn Postfix(string op) => (cursor, left, tok, feat) =>
        Ok(new PostfixExpr(op, left, left.Span), cursor);

    private static readonly LedFn CallLed = (cursor, left, tok, feat) =>
    {
        var args = ParseArgList(ref cursor, TokenKind.RParen, feat);
        Expect(ref cursor, TokenKind.RParen);
        return Ok(new CallExpr(left, args, left.Span), cursor);
    };

    private static readonly LedFn MemberAccessLed = (cursor, left, tok, feat) =>
    {
        if (cursor.Current.Kind != TokenKind.Identifier)
            throw new ParseException(
                $"expected member name, got `{cursor.Current.Text}`",
                cursor.Current.Span);
        var member = cursor.Current.Text;
        cursor = cursor.Advance();
        return Ok(new MemberExpr(left, member, left.Span), cursor);
    };

    private static readonly LedFn IndexAccessLed = (cursor, left, tok, feat) =>
    {
        var idxR = Parse(cursor, feat);
        if (!idxR.IsOk) return idxR;
        cursor = idxR.Remainder;
        Expect(ref cursor, TokenKind.RBracket);
        return Ok(new IndexExpr(left, idxR.Value, left.Span), cursor);
    };

    private static readonly LedFn QuestionLed = (cursor, left, tok, feat) =>
    {
        // null-conditional: obj?.member
        if (cursor.Current.Kind == TokenKind.Dot)
        {
            cursor = cursor.Advance();
            if (cursor.Current.Kind != TokenKind.Identifier)
                throw new ParseException("expected member name after `?.`", cursor.Current.Span);
            var member = cursor.Current.Text;
            cursor = cursor.Advance();
            return Ok(new NullConditionalExpr(left, member, left.Span), cursor);
        }
        // ternary: cond ? then : else
        if (!feat.IsEnabled("ternary"))
            throw new ParseException("feature `ternary` is disabled", tok.Span);
        var thenR = Parse(cursor, feat);
        if (!thenR.IsOk) return thenR;
        cursor = thenR.Remainder;
        Expect(ref cursor, TokenKind.Colon);
        var elseR = Parse(cursor, feat);
        return elseR.Map(e => (Expr)new ConditionalExpr(left, thenR.Value, e, left.Span));
    };

    private static readonly LedFn NullCoalesce = (cursor, left, tok, feat) =>
        Parse(cursor, feat, 19).Map(right => (Expr)new NullCoalesceExpr(left, right, tok.Span));

    private static readonly LedFn IsLed = (cursor, left, tok, feat) =>
    {
        if (cursor.Current.Kind != TokenKind.Identifier)
            throw new ParseException(
                $"expected type name after `is`, got `{cursor.Current.Text}`",
                cursor.Current.Span);
        var typeTok = cursor.Current;
        cursor = cursor.Advance();
        // Optional variable binding: `a is Dog d`
        if (cursor.Current.Kind == TokenKind.Identifier)
        {
            var bindTok = cursor.Current;
            cursor = cursor.Advance();
            return Ok(new IsPatternExpr(left, typeTok.Text, bindTok.Text, left.Span), cursor);
        }
        return Ok(
            new BinaryExpr("is", left, new IdentExpr(typeTok.Text, typeTok.Span), left.Span),
            cursor);
    };

    private static readonly LedFn SwitchExprLed = (cursor, left, tok, feat) =>
    {
        Expect(ref cursor, TokenKind.LBrace);
        var arms = new List<SwitchArm>();
        while (cursor.Current.Kind != TokenKind.RBrace && !cursor.IsEnd)
        {
            var aSpan = cursor.Current.Span;
            Expr? pattern;
            if (cursor.Current.Kind == TokenKind.Underscore)
            {
                cursor = cursor.Advance();
                pattern = null;
            }
            else
            {
                pattern = Parse(cursor, feat, 11).Unwrap(ref cursor);
            }
            Expect(ref cursor, TokenKind.FatArrow);
            var body = Parse(cursor, feat, 11).Unwrap(ref cursor);
            arms.Add(new SwitchArm(pattern, body, aSpan));
            if (cursor.Current.Kind != TokenKind.Comma) break;
            cursor = cursor.Advance();
        }
        Expect(ref cursor, TokenKind.RBrace);
        return Ok(new SwitchExpr(left, arms, tok.Span), cursor);
    };

    // ── Nud table ─────────────────────────────────────────────────────────────

    private static readonly Dictionary<TokenKind, NudEntry> s_nudTable = new()
    {
        // Literals
        [TokenKind.IntLiteral]                = new(ParseIntLit),
        [TokenKind.FloatLiteral]              = new(ParseFloatLit),
        [TokenKind.StringLiteral]             = new((c, t, _) => Ok(new LitStrExpr(t.Text[1..^1], t.Span), c)),
        [TokenKind.CharLiteral]               = new((c, t, _) => Ok(new LitCharExpr(t.Text.Length >= 3 ? t.Text[1] : '\0', t.Span), c)),
        [TokenKind.InterpolatedStringLiteral] = new(ParseInterpolatedNud),
        [TokenKind.True]                      = new((c, t, _) => Ok(new LitBoolExpr(true,  t.Span), c)),
        [TokenKind.False]                     = new((c, t, _) => Ok(new LitBoolExpr(false, t.Span), c)),
        [TokenKind.Null]                      = new((c, t, _) => Ok(new LitNullExpr(t.Span), c)),

        // Identifier and type-keywords as static-call targets (string.Join, int.Parse, …)
        [TokenKind.Identifier] = new((c, t, _) => Ok(new IdentExpr(t.Text,     t.Span), c)),
        [TokenKind.String]     = new((c, t, _) => Ok(new IdentExpr("string",   t.Span), c)),
        [TokenKind.Int]        = new((c, t, _) => Ok(new IdentExpr("int",      t.Span), c)),
        [TokenKind.Long]       = new((c, t, _) => Ok(new IdentExpr("long",     t.Span), c)),
        [TokenKind.Double]     = new((c, t, _) => Ok(new IdentExpr("double",   t.Span), c)),
        [TokenKind.Float]      = new((c, t, _) => Ok(new IdentExpr("float",    t.Span), c)),
        [TokenKind.Bool]       = new((c, t, _) => Ok(new IdentExpr("bool",     t.Span), c)),
        [TokenKind.Char]       = new((c, t, _) => Ok(new IdentExpr("char",     t.Span), c)),

        // Prefix unary (bp=85 so postfix at 90 binds tighter)
        [TokenKind.Plus]       = new(PrefixUnary("+",      85)),
        [TokenKind.Minus]      = new(PrefixUnary("-",      85)),
        [TokenKind.Bang]       = new(PrefixUnary("!",      85)),
        [TokenKind.Tilde]      = new(PrefixUnary("~",      85), "bitwise"),
        [TokenKind.Await]      = new(PrefixUnary("await",  85)),
        [TokenKind.PlusPlus]   = new(PrefixUnary("++",     85)),
        [TokenKind.MinusMinus] = new(PrefixUnary("--",     85)),

        // Complex atoms
        [TokenKind.New]    = new(ParseNew),
        [TokenKind.Typeof] = new(ParseTypeof),
        [TokenKind.LParen] = new(ParseLParen),
    };

    // ── Led table ─────────────────────────────────────────────────────────────

    private static readonly Dictionary<TokenKind, LedEntry> s_ledTable = new()
    {
        // Assignment (right-assoc: right parsed at bp=9)
        [TokenKind.Eq]        = new(10, Assign),
        [TokenKind.PlusEq]    = new(10, CompoundAssign("+")),
        [TokenKind.MinusEq]   = new(10, CompoundAssign("-")),
        [TokenKind.StarEq]    = new(10, CompoundAssign("*")),
        [TokenKind.SlashEq]   = new(10, CompoundAssign("/")),
        [TokenKind.PercentEq] = new(10, CompoundAssign("%")),
        [TokenKind.AmpEq]     = new(10, CompoundAssign("&"), "bitwise"),
        [TokenKind.PipeEq]    = new(10, CompoundAssign("|"), "bitwise"),
        [TokenKind.CaretEq]   = new(10, CompoundAssign("^"), "bitwise"),

        // Ternary / null-conditional (bp=20)
        [TokenKind.Question]         = new(20, QuestionLed),
        [TokenKind.QuestionQuestion] = new(20, NullCoalesce, "null_coalesce"),

        // Logical (bp=30/40)
        [TokenKind.PipePipe] = new(30, BinaryLeft("||", 30)),
        [TokenKind.AmpAmp]   = new(40, BinaryLeft("&&", 40)),

        // Bitwise OR/XOR/AND (bp=44/46/48)
        [TokenKind.Pipe]      = new(44, BinaryLeft("|",  44), "bitwise"),
        [TokenKind.Caret]     = new(46, BinaryLeft("^",  46), "bitwise"),
        [TokenKind.Ampersand] = new(48, BinaryLeft("&",  48), "bitwise"),

        // Equality (bp=50)
        [TokenKind.EqEq]   = new(50, BinaryLeft("==", 50)),
        [TokenKind.BangEq] = new(50, BinaryLeft("!=", 50)),

        // Relational / type tests (bp=60)
        [TokenKind.Lt]   = new(60, BinaryLeft("<",  60)),
        [TokenKind.LtEq] = new(60, BinaryLeft("<=", 60)),
        [TokenKind.Gt]   = new(60, BinaryLeft(">",  60)),
        [TokenKind.GtEq] = new(60, BinaryLeft(">=", 60)),
        [TokenKind.Is]   = new(60, IsLed),
        [TokenKind.As]   = new(60, BinaryLeft("as", 60)),

        // Bit-shifts (bp=65, between relational and additive)
        [TokenKind.LtLt] = new(65, BinaryLeft("<<", 65), "bitwise"),
        [TokenKind.GtGt] = new(65, BinaryLeft(">>", 65), "bitwise"),

        // Additive (bp=70) — Plus/Minus also have a Nud entry
        [TokenKind.Plus]  = new(70, BinaryLeft("+", 70)),
        [TokenKind.Minus] = new(70, BinaryLeft("-", 70)),

        // Multiplicative (bp=80)
        [TokenKind.Star]    = new(80, BinaryLeft("*", 80)),
        [TokenKind.Slash]   = new(80, BinaryLeft("/", 80)),
        [TokenKind.Percent] = new(80, BinaryLeft("%", 80)),

        // Postfix ++ / -- (bp=90, tighter than unary at 85)
        [TokenKind.PlusPlus]   = new(90, Postfix("++")),
        [TokenKind.MinusMinus] = new(90, Postfix("--")),

        // Call / member / index (bp=90)
        [TokenKind.LParen]   = new(90, CallLed),
        [TokenKind.Dot]      = new(90, MemberAccessLed),
        [TokenKind.LBracket] = new(90, IndexAccessLed),

        // Switch expression (bp=85, feature-gated)
        [TokenKind.Switch] = new(85, SwitchExprLed, "pattern_match"),
    };

    // ── Nud implementations ───────────────────────────────────────────────────

    private static NudFn PrefixUnary(string op, int bp) => (cursor, tok, feat) =>
        Parse(cursor, feat, bp).Map(operand => (Expr)new UnaryExpr(op, operand, tok.Span));

    private static ParseResult<Expr> ParseIntLit(
        TokenCursor cursor, Token tok, LanguageFeatures _)
    {
        var text = tok.Text.Replace("_", "").TrimEnd('L', 'l', 'u', 'U');
        long value;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = Convert.ToInt64(text[2..], 16);
        else if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            value = Convert.ToInt64(text[2..], 2);
        else
            value = long.Parse(text);
        return Ok(new LitIntExpr(value, tok.Span), cursor);
    }

    private static ParseResult<Expr> ParseFloatLit(
        TokenCursor cursor, Token tok, LanguageFeatures _)
    {
        var text = tok.Text.Replace("_", "").TrimEnd('f', 'F', 'd', 'D', 'm', 'M');
        var val  = double.Parse(text, CultureInfo.InvariantCulture);
        return Ok(new LitFloatExpr(val, tok.Span), cursor);
    }

    private static ParseResult<Expr> ParseInterpolatedNud(
        TokenCursor cursor, Token tok, LanguageFeatures feat) =>
        Ok(ParseInterpolatedString(tok, feat), cursor);

    private static ParseResult<Expr> ParseNew(
        TokenCursor cursor, Token tok, LanguageFeatures feat)
    {
        var ty = TypeParser.Parse(cursor).Unwrap(ref cursor);

        // new T[] { e0, e1, … }
        if (ty is ArrayType arrTy && cursor.Current.Kind == TokenKind.LBrace)
        {
            cursor = cursor.Advance();
            var elems = ParseArgList(ref cursor, TokenKind.RBrace, feat);
            Expect(ref cursor, TokenKind.RBrace);
            return Ok(new ArrayLitExpr(arrTy.Element, elems, tok.Span), cursor);
        }

        // new T[n]
        if (cursor.Current.Kind == TokenKind.LBracket)
        {
            cursor = cursor.Advance();
            var size = Parse(cursor, feat).Unwrap(ref cursor);
            Expect(ref cursor, TokenKind.RBracket);
            return Ok(new ArrayCreateExpr(ty, size, tok.Span), cursor);
        }

        // new T(args)
        Expect(ref cursor, TokenKind.LParen);
        var args = ParseArgList(ref cursor, TokenKind.RParen, feat);
        Expect(ref cursor, TokenKind.RParen);
        return Ok(new NewExpr(ty, args, tok.Span), cursor);
    }

    private static ParseResult<Expr> ParseTypeof(
        TokenCursor cursor, Token tok, LanguageFeatures _)
    {
        Expect(ref cursor, TokenKind.LParen);
        var ty = TypeParser.Parse(cursor).Unwrap(ref cursor);
        Expect(ref cursor, TokenKind.RParen);
        var typeName = ty switch
        {
            NamedType nt => nt.Name,
            ArrayType at => at.Element is NamedType ne ? $"{ne.Name}[]" : "array",
            VoidType     => "void",
            _            => ty.ToString() ?? "unknown",
        };
        return Ok(new LitStrExpr(typeName, tok.Span), cursor);
    }

    private static ParseResult<Expr> ParseLParen(
        TokenCursor cursor, Token tok, LanguageFeatures feat)
    {
        // Try cast: `(TypeName)expr` — type token followed immediately by `)`
        if (TypeParser.IsTypeToken(cursor.Current.Kind)
            && cursor.Peek(1).Kind == TokenKind.RParen)
        {
            var tyR = TypeParser.Parse(cursor);
            if (tyR.IsOk && tyR.Remainder.Current.Kind == TokenKind.RParen)
            {
                var afterClose = tyR.Remainder.Advance();
                var operandR   = Parse(afterClose, feat, 85);
                if (operandR.IsOk)
                    return Ok(new CastExpr(tyR.Value, operandR.Value, tok.Span),
                              operandR.Remainder);
            }
        }
        // Grouping: `(expr)`
        var innerR = Parse(cursor, feat);
        if (!innerR.IsOk) return innerR;
        cursor = innerR.Remainder;
        Expect(ref cursor, TokenKind.RParen);
        return Ok(innerR.Value, cursor);
    }

    // ── Interpolated string ───────────────────────────────────────────────────

    internal static InterpolatedStrExpr ParseInterpolatedString(
        Token tok, LanguageFeatures feat)
    {
        var raw  = tok.Text;
        var body = raw.StartsWith("$\"") ? raw[2..^1] : raw[1..^1];
        var parts = new List<InterpolationPart>();
        var sb    = new StringBuilder();
        int i     = 0;

        while (i < body.Length)
        {
            if (body[i] == '{')
            {
                if (sb.Length > 0) { parts.Add(new TextPart(sb.ToString(), tok.Span)); sb.Clear(); }
                i++;
                int depth   = 1;
                var exprSrc = new StringBuilder();
                while (i < body.Length && depth > 0)
                {
                    if (body[i] == '{') depth++;
                    else if (body[i] == '}') { depth--; if (depth == 0) { i++; break; } }
                    if (depth > 0) exprSrc.Append(body[i]);
                    i++;
                }
                var innerTokens = new Lexer.Lexer(exprSrc.ToString()).Tokenize();
                var innerCursor = TokenCursor.From(innerTokens);
                var innerExpr   = Parse(innerCursor, feat).OrThrow();
                parts.Add(new ExprPart(innerExpr, tok.Span));
            }
            else
            {
                sb.Append(body[i++]);
            }
        }
        if (sb.Length > 0) parts.Add(new TextPart(sb.ToString(), tok.Span));
        return new InterpolatedStrExpr(parts, tok.Span);
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    /// Shorthand: create a successful ParseResult<Expr>.
    private static ParseResult<Expr> Ok(Expr e, TokenCursor cursor) =>
        ParseResult<Expr>.Ok(e, cursor);

    /// Parse a comma-separated argument list, stopping at `stop` token.
    private static List<Expr> ParseArgList(
        ref TokenCursor cursor, TokenKind stop, LanguageFeatures feat)
    {
        var args = new List<Expr>();
        while (cursor.Current.Kind != stop && !cursor.IsEnd)
        {
            args.Add(Parse(cursor, feat).Unwrap(ref cursor));
            if (cursor.Current.Kind != TokenKind.Comma) break;
            cursor = cursor.Advance();
        }
        return args;
    }

    /// Consume an expected token or throw ParseException.
    private static void Expect(ref TokenCursor cursor, TokenKind kind)
    {
        if (cursor.Current.Kind != kind)
            throw new ParseException(
                $"expected `{P.KindDisplay(kind)}`, got `{cursor.Current.Text}`",
                cursor.Current.Span);
        cursor = cursor.Advance();
    }
}
