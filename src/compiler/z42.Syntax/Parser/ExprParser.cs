using Z42.Core.Text;
using System.Globalization;
using System.Text;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser.Core;

namespace Z42.Syntax.Parser;

/// Pratt expression parser.
/// All functions are pure: they take a TokenCursor and return ParseResult&lt;Expr&gt;.
/// NudFn / LedFn receive the cursor explicitly (no mutable ParserContext).
internal static class ExprParser
{
    // ── Binding-power constants ──────────────────────────────────────────────
    // Single source of truth for operator precedence.
    // Values are spaced to leave room for future operators between levels.

    private const int BpAssign      = 10;  // = += -= *= /= %= &= |= ^= (right-assoc)
    private const int BpTernary     = 20;  // ? : / ?? / ?.
    private const int BpLogicalOr   = 30;  // ||
    private const int BpLogicalAnd  = 40;  // &&
    private const int BpBitwiseOr   = 44;  // |
    private const int BpBitwiseXor  = 46;  // ^
    private const int BpBitwiseAnd  = 48;  // &
    private const int BpEquality    = 50;  // == !=
    private const int BpRelational  = 60;  // < <= > >= is as
    private const int BpShift       = 65;  // << >>
    private const int BpAdditive    = 70;  // + -
    private const int BpMultiply    = 80;  // * / %
    private const int BpSwitch      = 85;  // switch expr (postfix)
    private const int BpUnary       = 85;  // prefix + - ! ~ await ++ --
    private const int BpPostfix     = 90;  // postfix ++ -- call . []

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
        Parse(cursor, feat, BpAssign - 1).Map(right => (Expr)new AssignExpr(left, right, left.Span));

    private static LedFn CompoundAssign(string binOp) => (cursor, left, tok, feat) =>
        Parse(cursor, feat, BpAssign - 1).Map(right =>
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
        Parse(cursor, feat, BpTernary - 1).Map(right => (Expr)new NullCoalesceExpr(left, right, tok.Span));

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
                pattern = Parse(cursor, feat, BpAssign + 1).Unwrap(ref cursor);
            }
            Expect(ref cursor, TokenKind.FatArrow);
            var body = Parse(cursor, feat, BpAssign + 1).Unwrap(ref cursor);
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

        // Prefix unary (BpUnary so postfix at BpPostfix binds tighter)
        [TokenKind.Plus]       = new(PrefixUnary("+",      BpUnary)),
        [TokenKind.Minus]      = new(PrefixUnary("-",      BpUnary)),
        [TokenKind.Bang]       = new(PrefixUnary("!",      BpUnary)),
        [TokenKind.Tilde]      = new(PrefixUnary("~",      BpUnary), "bitwise"),
        [TokenKind.Await]      = new(PrefixUnary("await",  BpUnary)),
        [TokenKind.PlusPlus]   = new(PrefixUnary("++",     BpUnary)),
        [TokenKind.MinusMinus] = new(PrefixUnary("--",     BpUnary)),

        // Complex atoms
        [TokenKind.New]    = new(ParseNew),
        [TokenKind.Typeof] = new(ParseTypeof),
        [TokenKind.LParen] = new(ParseLParen),
    };

    // ── Led table ─────────────────────────────────────────────────────────────

    private static readonly Dictionary<TokenKind, LedEntry> s_ledTable = new()
    {
        // Assignment (right-assoc: right parsed at BpAssign-1)
        [TokenKind.Eq]        = new(BpAssign, Assign),
        [TokenKind.PlusEq]    = new(BpAssign, CompoundAssign("+")),
        [TokenKind.MinusEq]   = new(BpAssign, CompoundAssign("-")),
        [TokenKind.StarEq]    = new(BpAssign, CompoundAssign("*")),
        [TokenKind.SlashEq]   = new(BpAssign, CompoundAssign("/")),
        [TokenKind.PercentEq] = new(BpAssign, CompoundAssign("%")),
        [TokenKind.AmpEq]     = new(BpAssign, CompoundAssign("&"), "bitwise"),
        [TokenKind.PipeEq]    = new(BpAssign, CompoundAssign("|"), "bitwise"),
        [TokenKind.CaretEq]   = new(BpAssign, CompoundAssign("^"), "bitwise"),

        // Ternary / null-conditional
        [TokenKind.Question]         = new(BpTernary, QuestionLed),
        [TokenKind.QuestionQuestion] = new(BpTernary, NullCoalesce, "null_coalesce"),

        // Logical
        [TokenKind.PipePipe] = new(BpLogicalOr,  BinaryLeft("||", BpLogicalOr)),
        [TokenKind.AmpAmp]   = new(BpLogicalAnd, BinaryLeft("&&", BpLogicalAnd)),

        // Bitwise OR / XOR / AND
        [TokenKind.Pipe]      = new(BpBitwiseOr,  BinaryLeft("|",  BpBitwiseOr),  "bitwise"),
        [TokenKind.Caret]     = new(BpBitwiseXor, BinaryLeft("^",  BpBitwiseXor), "bitwise"),
        [TokenKind.Ampersand] = new(BpBitwiseAnd, BinaryLeft("&",  BpBitwiseAnd), "bitwise"),

        // Equality
        [TokenKind.EqEq]   = new(BpEquality, BinaryLeft("==", BpEquality)),
        [TokenKind.BangEq] = new(BpEquality, BinaryLeft("!=", BpEquality)),

        // Relational / type tests
        [TokenKind.Lt]   = new(BpRelational, BinaryLeft("<",  BpRelational)),
        [TokenKind.LtEq] = new(BpRelational, BinaryLeft("<=", BpRelational)),
        [TokenKind.Gt]   = new(BpRelational, BinaryLeft(">",  BpRelational)),
        [TokenKind.GtEq] = new(BpRelational, BinaryLeft(">=", BpRelational)),
        [TokenKind.Is]   = new(BpRelational, IsLed),
        [TokenKind.As]   = new(BpRelational, BinaryLeft("as", BpRelational)),

        // Bit-shifts (between relational and additive)
        [TokenKind.LtLt] = new(BpShift, BinaryLeft("<<", BpShift), "bitwise"),
        [TokenKind.GtGt] = new(BpShift, BinaryLeft(">>", BpShift), "bitwise"),

        // Additive — Plus/Minus also have a Nud entry
        [TokenKind.Plus]  = new(BpAdditive, BinaryLeft("+", BpAdditive)),
        [TokenKind.Minus] = new(BpAdditive, BinaryLeft("-", BpAdditive)),

        // Multiplicative
        [TokenKind.Star]    = new(BpMultiply, BinaryLeft("*", BpMultiply)),
        [TokenKind.Slash]   = new(BpMultiply, BinaryLeft("/", BpMultiply)),
        [TokenKind.Percent] = new(BpMultiply, BinaryLeft("%", BpMultiply)),

        // Postfix ++ / -- (tighter than unary)
        [TokenKind.PlusPlus]   = new(BpPostfix, Postfix("++")),
        [TokenKind.MinusMinus] = new(BpPostfix, Postfix("--")),

        // Call / member / index
        [TokenKind.LParen]   = new(BpPostfix, CallLed),
        [TokenKind.Dot]      = new(BpPostfix, MemberAccessLed),
        [TokenKind.LBracket] = new(BpPostfix, IndexAccessLed),

        // Switch expression (feature-gated)
        [TokenKind.Switch] = new(BpSwitch, SwitchExprLed, "pattern_match"),
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
        var raw     = tok.Text.Replace("_", "");
        bool isFloat = raw.EndsWith('f') || raw.EndsWith('F');
        var  text    = raw.TrimEnd('f', 'F', 'd', 'D', 'm', 'M');
        var  val     = double.Parse(text, CultureInfo.InvariantCulture);
        return Ok(new LitFloatExpr(val, isFloat, tok.Span), cursor);
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
                var operandR   = Parse(afterClose, feat, BpUnary);
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
                $"expected `{Combinators.KindDisplay(kind)}`, got `{cursor.Current.Text}`",
                cursor.Current.Span);
        cursor = cursor.Advance();
    }
}
