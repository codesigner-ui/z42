using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser.Core;

namespace Z42.Syntax.Parser;

/// Pratt expression parser.
/// All functions are pure: they take a TokenCursor and return ParseResult&lt;Expr&gt;.
/// NudFn / LedFn receive the cursor explicitly (no mutable ParserContext).
///
/// 拆分为多个 partial 文件：
/// • ExprParser.cs       — Pratt entry + 优先级常量 + delegate 类型 + Led 实现 + Nud/Led 优先级表
/// • ExprParser.Atoms.cs — Nud（前缀 / atom）实现 + 字符串插值 + 通用 helper / 转义解码
internal static partial class ExprParser
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
    /// `diags` flows through recursive Parse calls so per-arg / per-subexpr recovery
    /// can localize errors (spec enhance-expr-recovery, 2026-05-08).
    private delegate ParseResult<Expr> NudFn(
        TokenCursor cursor, Token tok, LanguageFeatures feat, DiagnosticBag? diags);

    /// Infix / postfix handler: operator token already consumed; `cursor` is after it.
    private delegate ParseResult<Expr> LedFn(
        TokenCursor cursor, Expr left, Token tok, LanguageFeatures feat, DiagnosticBag? diags);

    private sealed record NudEntry(NudFn Fn, LanguageFeature? Feature = null);
    private sealed record LedEntry(int Bp, LedFn Fn, LanguageFeature? Feature = null);

    // ── Entry point ───────────────────────────────────────────────────────────

    /// Parse an expression with the given minimum binding power (0 = full expression).
    ///
    /// 错误恢复（spec enhance-expr-recovery, 2026-05-08）：当 caller 传入
    /// `DiagnosticBag` 时，遇到 ParseException 不再冒泡——改为把错误写进
    /// `diags` 并返回 `ParseResult.Ok(ErrorExpr, cursor_at_sync_point)`。
    /// caller 可以继续解析剩余 input。`diags == null` 时保持原 throw 行为
    /// （test 路径 fail-fast）。
    internal static ParseResult<Expr> Parse(
        TokenCursor cursor, LanguageFeatures feat, int minBp = 0,
        DiagnosticBag? diags = null)
    {
        if (diags == null)
            return ParseInternal(cursor, feat, minBp, diags: null);

        try
        {
            return ParseInternal(cursor, feat, minBp, diags);
        }
        catch (ParseException ex)
        {
            diags.Error(ex.Code ?? DiagnosticCodes.UnexpectedToken,
                        ex.Message, ex.Span);
            var skipped = SkipToExprBoundary(cursor);
            return ParseResult<Expr>.Ok(new ErrorExpr(ex.Message, ex.Span), skipped);
        }
    }

    /// Pratt 主体（行为不变；提取自原 Parse 以便上层 try/catch 包裹）。
    /// `diags` 透传给 Nud/Led 以支持子表达式独立恢复；`null` 时维持 throw 语义。
    private static ParseResult<Expr> ParseInternal(
        TokenCursor cursor, LanguageFeatures feat, int minBp, DiagnosticBag? diags)
    {
        var span = cursor.Current.Span;

        // Lambda detection — `x => ...` or `(...) => ...`. Lambda binds at the
        // lowest expression level; we preempt before normal Nud dispatch.
        // See docs/design/language/closure.md §3.1.
        if (IsLambdaStart(cursor))
        {
            if (!feat.IsEnabled(LanguageFeature.Lambda))
                throw new ParseException($"feature `lambda` is disabled", span,
                    DiagnosticCodes.FeatureDisabled);
            return ParseLambda(cursor, feat);
        }

        if (!s_nudTable.TryGetValue(cursor.Current.Kind, out var nudEntry))
            return ParseResult<Expr>.Fail(cursor,
                $"unexpected token `{cursor.Current.Text}` in expression");

        if (nudEntry.Feature is { } nf && !feat.IsEnabled(nf))
            throw new ParseException($"feature `{LanguageFeatures.Metadata[nf].Name}` is disabled", span,
                DiagnosticCodes.FeatureDisabled);

        var nudTok = cursor.Current;
        cursor = cursor.Advance();
        var r = nudEntry.Fn(cursor, nudTok, feat, diags);
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
            var lr = ledEntry.Fn(cursor, left, ledTok, feat, diags);
            if (!lr.IsOk) return lr;
            cursor = lr.Remainder;
            left   = lr.Value;
        }

        return ParseResult<Expr>.Ok(left, cursor);
    }

    /// Skip tokens to the next expression sync point (`,` `)` `]` `;` `}` or EOF).
    /// 平面 skip：不跟踪嵌套深度（嵌套由外层 ArgList / block 循环条件管控）。
    /// spec enhance-expr-recovery design Decision 3.
    private static TokenCursor SkipToExprBoundary(TokenCursor cursor)
    {
        while (!cursor.IsEnd)
        {
            var k = cursor.Current.Kind;
            if (k is TokenKind.Comma
                  or TokenKind.RParen
                  or TokenKind.RBracket
                  or TokenKind.RBrace
                  or TokenKind.Semicolon)
                break;
            cursor = cursor.Advance();
        }
        return cursor;
    }

    // ── Led implementations (must be declared before s_ledTable) ─────────────

    private static LedFn BinaryLeft(string op, int bp) => (cursor, left, tok, feat, diags) =>
        Parse(cursor, feat, bp, diags).Map(right => (Expr)new BinaryExpr(op, left, right, left.Span));

    private static readonly LedFn Assign = (cursor, left, tok, feat, diags) =>
        Parse(cursor, feat, BpAssign - 1, diags).Map(right => (Expr)new AssignExpr(left, right, left.Span));

    private static LedFn CompoundAssign(string binOp) => (cursor, left, tok, feat, diags) =>
        Parse(cursor, feat, BpAssign - 1, diags).Map(right =>
            (Expr)new AssignExpr(left, new BinaryExpr(binOp, left, right, left.Span), left.Span));

    private static LedFn Postfix(string op) => (cursor, left, tok, feat, diags) =>
        Ok(new PostfixExpr(op, left, left.Span), cursor);

    private static readonly LedFn CallLed = (cursor, left, tok, feat, diags) =>
    {
        // ParseCallArgumentList: spec define-ref-out-in-parameters + add-named-arguments
        //   — `f(ref x)` / `f(name: 1)` / `f(out var y)` all unified through
        //   `Argument(Name?, Value, Span)` shape.
        // diags: spec enhance-expr-recovery — single bad arg becomes ErrorExpr
        // and parsing of subsequent args continues.
        var args = ParseCallArgumentList(ref cursor, TokenKind.RParen, feat, diags: diags);
        Expect(ref cursor, TokenKind.RParen);
        return Ok(new CallExpr(left, args, left.Span), cursor);
    };

    private static readonly LedFn MemberAccessLed = (cursor, left, tok, feat, diags) =>
    {
        if (cursor.Current.Kind != TokenKind.Identifier)
            throw new ParseException(
                $"expected member name, got `{cursor.Current.Text}`",
                cursor.Current.Span,
                DiagnosticCodes.ExpectedToken);
        var member = cursor.Current.Text;
        cursor = cursor.Advance();
        return Ok(new MemberExpr(left, member, left.Span), cursor);
    };

    private static readonly LedFn IndexAccessLed = (cursor, left, tok, feat, diags) =>
    {
        var idxR = Parse(cursor, feat, diags: diags);
        if (!idxR.IsOk) return idxR;
        cursor = idxR.Remainder;
        Expect(ref cursor, TokenKind.RBracket);
        return Ok(new IndexExpr(left, idxR.Value, left.Span), cursor);
    };

    private static readonly LedFn QuestionLed = (cursor, left, tok, feat, diags) =>
    {
        // null-conditional: obj?.member
        if (cursor.Current.Kind == TokenKind.Dot)
        {
            cursor = cursor.Advance();
            if (cursor.Current.Kind != TokenKind.Identifier)
                throw new ParseException("expected member name after `?.`", cursor.Current.Span,
                    DiagnosticCodes.ExpectedToken);
            var member = cursor.Current.Text;
            cursor = cursor.Advance();
            return Ok(new NullConditionalExpr(left, member, left.Span), cursor);
        }
        // ternary: cond ? then : else
        if (!feat.IsEnabled(LanguageFeature.Ternary))
            throw new ParseException($"feature `{LanguageFeatures.Metadata[LanguageFeature.Ternary].Name}` is disabled", tok.Span,
                DiagnosticCodes.FeatureDisabled);
        var thenR = Parse(cursor, feat, diags: diags);
        if (!thenR.IsOk) return thenR;
        cursor = thenR.Remainder;
        Expect(ref cursor, TokenKind.Colon);
        var elseR = Parse(cursor, feat, diags: diags);
        return elseR.Map(e => (Expr)new ConditionalExpr(left, thenR.Value, e, left.Span));
    };

    private static readonly LedFn NullCoalesce = (cursor, left, tok, feat, diags) =>
        Parse(cursor, feat, BpTernary - 1, diags).Map(right => (Expr)new NullCoalesceExpr(left, right, tok.Span));

    private static readonly LedFn IsLed = (cursor, left, tok, feat, diags) =>
    {
        if (cursor.Current.Kind != TokenKind.Identifier)
            throw new ParseException(
                $"expected type name after `is`, got `{cursor.Current.Text}`",
                cursor.Current.Span,
                DiagnosticCodes.ExpectedToken);
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

    private static readonly LedFn SwitchExprLed = (cursor, left, tok, feat, diags) =>
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
                pattern = Parse(cursor, feat, BpAssign + 1, diags).Unwrap(ref cursor);
            }
            Expect(ref cursor, TokenKind.FatArrow);
            var body = Parse(cursor, feat, BpAssign + 1, diags).Unwrap(ref cursor);
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
        [TokenKind.StringLiteral]             = new((c, t, _, _) => Ok(new LitStrExpr(UnescapeString(t.Text[1..^1]), t.Span), c)),
        [TokenKind.CharLiteral]               = new((c, t, _, _) => Ok(new LitCharExpr(UnescapeChar(t.Text), t.Span), c)),
        [TokenKind.InterpolatedStringLiteral] = new(ParseInterpolatedNud),
        [TokenKind.True]                      = new((c, t, _, _) => Ok(new LitBoolExpr(true,  t.Span), c)),
        [TokenKind.False]                     = new((c, t, _, _) => Ok(new LitBoolExpr(false, t.Span), c)),
        [TokenKind.Null]                      = new((c, t, _, _) => Ok(new LitNullExpr(t.Span), c)),

        // Identifier and type-keywords as static-call targets (string.Join, int.Parse, …)
        [TokenKind.Identifier] = new((c, t, _, _) => Ok(new IdentExpr(t.Text,     t.Span), c)),
        [TokenKind.String]     = new((c, t, _, _) => Ok(new IdentExpr("string",   t.Span), c)),
        [TokenKind.Int]        = new((c, t, _, _) => Ok(new IdentExpr("int",      t.Span), c)),
        [TokenKind.Long]       = new((c, t, _, _) => Ok(new IdentExpr("long",     t.Span), c)),
        [TokenKind.Double]     = new((c, t, _, _) => Ok(new IdentExpr("double",   t.Span), c)),
        [TokenKind.Float]      = new((c, t, _, _) => Ok(new IdentExpr("float",    t.Span), c)),
        [TokenKind.Bool]       = new((c, t, _, _) => Ok(new IdentExpr("bool",     t.Span), c)),
        [TokenKind.Char]       = new((c, t, _, _) => Ok(new IdentExpr("char",     t.Span), c)),

        // Prefix unary (BpUnary so postfix at BpPostfix binds tighter)
        [TokenKind.Plus]       = new(PrefixUnary("+",      BpUnary)),
        [TokenKind.Minus]      = new(PrefixUnary("-",      BpUnary)),
        [TokenKind.Bang]       = new(PrefixUnary("!",      BpUnary)),
        [TokenKind.Tilde]      = new(PrefixUnary("~",      BpUnary), LanguageFeature.Bitwise),
        [TokenKind.Await]      = new(PrefixUnary("await",  BpUnary)),
        [TokenKind.PlusPlus]   = new(PrefixUnary("++",     BpUnary)),
        [TokenKind.MinusMinus] = new(PrefixUnary("--",     BpUnary)),

        // Complex atoms
        [TokenKind.New]     = new(ParseNew),
        [TokenKind.Typeof]  = new(ParseTypeof),
        [TokenKind.Default] = new(ParseDefault),
        [TokenKind.LParen]  = new(ParseLParen),
    };

    // ── Led table ─────────────────────────────────────────────────────────────

    private static readonly Dictionary<TokenKind, LedEntry> s_ledTable = BuildLedTable();

    private static Dictionary<TokenKind, LedEntry> BuildLedTable()
    {
        var table = new Dictionary<TokenKind, LedEntry>
        {
            // Assignment (right-assoc: right parsed at BpAssign-1)
            [TokenKind.Eq]        = new(BpAssign, Assign),
            [TokenKind.PlusEq]    = new(BpAssign, CompoundAssign("+")),
            [TokenKind.MinusEq]   = new(BpAssign, CompoundAssign("-")),
            [TokenKind.StarEq]    = new(BpAssign, CompoundAssign("*")),
            [TokenKind.SlashEq]   = new(BpAssign, CompoundAssign("/")),
            [TokenKind.PercentEq] = new(BpAssign, CompoundAssign("%")),
            [TokenKind.AmpEq]     = new(BpAssign, CompoundAssign("&"), LanguageFeature.Bitwise),
            [TokenKind.PipeEq]    = new(BpAssign, CompoundAssign("|"), LanguageFeature.Bitwise),
            [TokenKind.CaretEq]   = new(BpAssign, CompoundAssign("^"), LanguageFeature.Bitwise),

            // Ternary / null-conditional
            [TokenKind.Question]         = new(BpTernary, QuestionLed),
            [TokenKind.QuestionQuestion] = new(BpTernary, NullCoalesce, LanguageFeature.NullCoalesce),

            // is / as (relational but with special handlers)
            [TokenKind.Is] = new(BpRelational, IsLed),
            [TokenKind.As] = new(BpRelational, BinaryLeft("as", BpRelational)),

            // Postfix ++ / -- (tighter than unary)
            [TokenKind.PlusPlus]   = new(BpPostfix, Postfix("++")),
            [TokenKind.MinusMinus] = new(BpPostfix, Postfix("--")),

            // Call / member / index
            [TokenKind.LParen]   = new(BpPostfix, CallLed),
            [TokenKind.Dot]      = new(BpPostfix, MemberAccessLed),
            [TokenKind.LBracket] = new(BpPostfix, IndexAccessLed),

            // Switch expression (feature-gated)
            [TokenKind.Switch] = new(BpSwitch, SwitchExprLed, LanguageFeature.PatternMatch),
        };

        // Binary operators — generated from OperatorDefs single source of truth
        foreach (var def in OperatorDefs.BinaryOps)
            table[def.Token] = new LedEntry(def.Bp, BinaryLeft(def.OpStr, def.Bp), def.Feature);

        return table;
    }
}
