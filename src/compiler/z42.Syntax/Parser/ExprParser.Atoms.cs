using Z42.Core.Diagnostics;
using System.Globalization;
using System.Text;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser.Core;

namespace Z42.Syntax.Parser;

/// Pratt expression parser — Nud（前缀 / atom）实现 + 字符串插值 + 通用 helper /
/// 转义解码。与 ExprParser.cs（核心 Pratt loop + Led 实现 + 优先级表）配套。
internal static partial class ExprParser
{
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
                if (sb.Length > 0) { parts.Add(new TextPart(UnescapeString(sb.ToString()), tok.Span)); sb.Clear(); }
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
        if (sb.Length > 0) parts.Add(new TextPart(UnescapeString(sb.ToString()), tok.Span));
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
                cursor.Current.Span,
                DiagnosticCodes.ExpectedToken);
        cursor = cursor.Advance();
    }

    /// Decode standard escape sequences in a string literal body
    /// (lexer captures them as raw 2-char `\n`, `\t` etc.; parser converts here).
    /// Recognised: `\n` `\t` `\r` `\0` `\\` `\"` `\'`. Unknown escape kept literal.
    /// 2026-04-26 fix-string-literal-escape：将 lexer 跳过但未解码的转义序列还原。
    internal static string UnescapeString(string raw)
    {
        if (raw.IndexOf('\\') < 0) return raw;
        var sb = new System.Text.StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\\' && i + 1 < raw.Length)
            {
                char next = raw[++i];
                sb.Append(next switch
                {
                    'n'  => '\n',
                    't'  => '\t',
                    'r'  => '\r',
                    '0'  => '\0',
                    '\\' => '\\',
                    '"'  => '"',
                    '\'' => '\'',
                    _    => next, // unknown escape: keep next char literal (drop the `\`)
                });
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// Decode a char literal token text `'X'` or `'\X'` to a single char.
    private static char UnescapeChar(string tokenText)
    {
        // Strip surrounding quotes: `'\n'` → `\n`, `'a'` → `a`.
        if (tokenText.Length < 3) return '\0';
        var inner = tokenText[1..^1];
        var unescaped = UnescapeString(inner);
        return unescaped.Length > 0 ? unescaped[0] : '\0';
    }
}
