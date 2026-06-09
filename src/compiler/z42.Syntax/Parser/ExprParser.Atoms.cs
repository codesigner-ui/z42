using Z42.Core.Diagnostics;
using Z42.Core.Text;
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

    private static NudFn PrefixUnary(string op, int bp) => (cursor, tok, feat, diags) =>
        Parse(cursor, feat, bp, diags).Map(operand => (Expr)new UnaryExpr(op, operand, tok.Span));

    private static ParseResult<Expr> ParseIntLit(
        TokenCursor cursor, Token tok, LanguageFeatures _, DiagnosticBag? __)
    {
        var text = tok.Text.Replace("_", "").TrimEnd('L', 'l', 'u', 'U');
        long value;
        try
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = Convert.ToInt64(text[2..], 16);
            else if (text.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                value = Convert.ToInt64(text[2..], 2);
            else
                value = long.Parse(text);
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            // The lexer can hand us a lenient/malformed numeric token (e.g. "8F")
            // or one that overflows Int64 — surface a structured ParseException
            // rather than letting an unstructured exception crash the parser.
            throw new ParseException(
                $"invalid integer literal `{tok.Text}`", tok.Span, DiagnosticCodes.InvalidNumericLit);
        }
        return Ok(new LitIntExpr(value, tok.Span), cursor);
    }

    private static ParseResult<Expr> ParseFloatLit(
        TokenCursor cursor, Token tok, LanguageFeatures _, DiagnosticBag? __)
    {
        var raw     = tok.Text.Replace("_", "");
        bool isFloat = raw.EndsWith('f') || raw.EndsWith('F');
        var  text    = raw.TrimEnd('f', 'F', 'd', 'D', 'm', 'M');
        double val;
        try { val = double.Parse(text, CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            throw new ParseException(
                $"invalid numeric literal `{tok.Text}`", tok.Span, DiagnosticCodes.InvalidNumericLit);
        }
        return Ok(new LitFloatExpr(val, isFloat, tok.Span), cursor);
    }

    private static ParseResult<Expr> ParseInterpolatedNud(
        TokenCursor cursor, Token tok, LanguageFeatures feat, DiagnosticBag? _) =>
        Ok(ParseInterpolatedString(tok, feat), cursor);

    private static ParseResult<Expr> ParseNew(
        TokenCursor cursor, Token tok, LanguageFeatures feat, DiagnosticBag? diags)
    {
        var ty = TypeParser.Parse(cursor).Unwrap(ref cursor);

        // new T[] { e0, e1, … }
        if (ty is ArrayType arrTy && cursor.Current.Kind == TokenKind.LBrace)
        {
            cursor = cursor.Advance();
            var elems = ParseArgList(ref cursor, TokenKind.RBrace, feat, diags: diags);
            Expect(ref cursor, TokenKind.RBrace);
            return Ok(new ArrayLitExpr(arrTy.Element, elems, tok.Span), cursor);
        }

        // new T[n]
        if (cursor.Current.Kind == TokenKind.LBracket)
        {
            cursor = cursor.Advance();
            var size = Parse(cursor, feat, diags: diags).Unwrap(ref cursor);
            Expect(ref cursor, TokenKind.RBracket);
            return Ok(new ArrayCreateExpr(ty, size, tok.Span), cursor);
        }

        // new T(args)
        Expect(ref cursor, TokenKind.LParen);
        var args = ParseCallArgumentList(ref cursor, TokenKind.RParen, feat, diags: diags);
        Expect(ref cursor, TokenKind.RParen);
        return Ok(new NewExpr(ty, args, tok.Span), cursor);
    }

    private static ParseResult<Expr> ParseTypeof(
        TokenCursor cursor, Token tok, LanguageFeatures _, DiagnosticBag? __)
    {
        Expect(ref cursor, TokenKind.LParen);
        var ty = TypeParser.Parse(cursor).Unwrap(ref cursor);
        Expect(ref cursor, TokenKind.RParen);
        // make-typeof-return-type (C2, 2026-06-09): preserve T as a TypeExpr;
        // the TypeChecker resolves it to a fully-qualified type identity and
        // binds `BoundTypeof`. (Previously desugared here to `LitStrExpr` of the
        // written name → typeof returned a string.)
        return Ok(new TypeofExpr(ty, tok.Span), cursor);
    }

    /// `default(T)` — preserve T as a TypeExpr so TypeChecker can resolve and
    /// validate it (rejecting generic type-parameters with E0421). IrGen later
    /// dispatches on the resolved Z42Type to emit the right `Const*` opcode.
    /// (add-default-expression, 2026-05-06)
    private static ParseResult<Expr> ParseDefault(
        TokenCursor cursor, Token tok, LanguageFeatures _, DiagnosticBag? __)
    {
        Expect(ref cursor, TokenKind.LParen);
        var ty = TypeParser.Parse(cursor).Unwrap(ref cursor);
        Expect(ref cursor, TokenKind.RParen);
        return Ok(new DefaultExpr(ty, tok.Span), cursor);
    }

    private static ParseResult<Expr> ParseLParen(
        TokenCursor cursor, Token tok, LanguageFeatures feat, DiagnosticBag? diags)
    {
        // Try cast: `(TypeName)expr` (single ident) — fast-path lookahead.
        // 2026-05-07 add-as-cast-on-arrays: also recognise `(TypeName[])expr` so
        // `(int[])arr.Clone()` parses as a cast. Higher-order forms (jagged
        // `T[][]`, generic-with-array `Foo<T>[]`) remain out-of-scope and use
        // explicit type-binding workarounds.
        bool simpleCast = TypeParser.IsTypeToken(cursor.Current.Kind)
            && cursor.Peek(1).Kind == TokenKind.RParen;
        bool arrayCast  = TypeParser.IsTypeToken(cursor.Current.Kind)
            && cursor.Peek(1).Kind == TokenKind.LBracket
            && cursor.Peek(2).Kind == TokenKind.RBracket
            && cursor.Peek(3).Kind == TokenKind.RParen;
        if (simpleCast || arrayCast)
        {
            var tyR = TypeParser.Parse(cursor);
            if (tyR.IsOk && tyR.Remainder.Current.Kind == TokenKind.RParen)
            {
                var afterClose = tyR.Remainder.Advance();
                var operandR   = Parse(afterClose, feat, BpUnary, diags);
                if (operandR.IsOk)
                    return Ok(new CastExpr(tyR.Value, operandR.Value, tok.Span),
                              operandR.Remainder);
            }
        }
        // Grouping: `(expr)`
        var innerR = Parse(cursor, feat, diags: diags);
        if (!innerR.IsOk) return innerR;
        cursor = innerR.Remainder;
        Expect(ref cursor, TokenKind.RParen);
        return Ok(innerR.Value, cursor);
    }

    // ── Lambda parsing ────────────────────────────────────────────────────────

    /// Returns true if the cursor points at the start of a lambda literal:
    ///   • `IDENT '=>'` — single untyped param
    ///   • `'(' ... ')' '=>'` — paren-wrapped param list (possibly empty / typed)
    /// Does not consume tokens.
    /// See docs/design/language/closure.md §3.1.
    internal static bool IsLambdaStart(TokenCursor cursor)
    {
        if (cursor.Current.Kind == TokenKind.Identifier
            && cursor.Peek(1).Kind == TokenKind.FatArrow)
            return true;

        if (cursor.Current.Kind != TokenKind.LParen) return false;

        // Scan to matching `)` at depth 0, then check for `=>`.
        // Bounded by IsEnd so we don't loop forever on malformed input.
        int depth = 1;
        int i = 1;
        while (depth > 0)
        {
            var tok = cursor.Peek(i);
            if (tok.Kind == TokenKind.Eof) return false;
            if (tok.Kind == TokenKind.LParen) depth++;
            else if (tok.Kind == TokenKind.RParen) depth--;
            i++;
        }
        return cursor.Peek(i).Kind == TokenKind.FatArrow;
    }

    /// Parse a lambda literal. Caller has confirmed `IsLambdaStart(cursor)`.
    /// See docs/design/language/closure.md §3.1.
    internal static ParseResult<Expr> ParseLambda(TokenCursor cursor, LanguageFeatures feat)
    {
        var startSpan = cursor.Current.Span;
        var paramList = new List<LambdaParam>();

        if (cursor.Current.Kind == TokenKind.Identifier)
        {
            // Form 1: `IDENT => body`
            var name = cursor.Current.Text;
            var pSpan = cursor.Current.Span;
            paramList.Add(new LambdaParam(name, null, pSpan));
            cursor = cursor.Advance(); // consume IDENT
        }
        else
        {
            // Form 2: `( ... ) => body`
            cursor = cursor.Advance(); // consume (
            while (cursor.Current.Kind != TokenKind.RParen && !cursor.IsEnd)
            {
                paramList.Add(ParseLambdaParam(ref cursor));
                if (cursor.Current.Kind != TokenKind.Comma) break;
                cursor = cursor.Advance();
            }
            Expect(ref cursor, TokenKind.RParen);
        }

        Expect(ref cursor, TokenKind.FatArrow);

        // Body: block `{ ... }` or expression
        LambdaBody body;
        if (cursor.Current.Kind == TokenKind.LBrace)
        {
            var blockR = StmtParser.ParseBlock(cursor, feat);
            if (!blockR.IsOk) return ParseResult<Expr>.Fail(cursor, blockR.Error!);
            cursor = blockR.Remainder;
            body = new LambdaBlockBody(blockR.Value, blockR.Value.Span);
        }
        else
        {
            // Lambda body expression: parse at BpAssign-1 so that comma at
            // call sites and `,` in arg lists terminate the lambda.
            var exprR = Parse(cursor, feat, BpAssign - 1);
            if (!exprR.IsOk) return exprR;
            cursor = exprR.Remainder;
            body = new LambdaExprBody(exprR.Value, exprR.Value.Span);
        }

        return Ok(new LambdaExpr(paramList, body, startSpan), cursor);
    }

    /// Parse a single lambda parameter inside `(...)`:
    ///   • `IDENT`               — untyped
    ///   • `Type IDENT`          — typed (e.g. `int x`)
    private static LambdaParam ParseLambdaParam(ref TokenCursor cursor)
    {
        var span = cursor.Current.Span;

        // If the current token is a type token (or could start a type),
        // try to parse a type followed by identifier.
        if (TypeParser.IsTypeToken(cursor.Current.Kind)
            && cursor.Peek(1).Kind == TokenKind.Identifier)
        {
            var tyR = TypeParser.Parse(cursor);
            if (tyR.IsOk && tyR.Remainder.Current.Kind == TokenKind.Identifier)
            {
                cursor = tyR.Remainder;
                var nameTok = cursor.Current;
                cursor = cursor.Advance();
                return new LambdaParam(nameTok.Text, tyR.Value, span);
            }
        }

        // Plain identifier — untyped param.
        if (cursor.Current.Kind != TokenKind.Identifier)
            throw new ParseException(
                $"expected lambda parameter name, got `{cursor.Current.Text}`",
                cursor.Current.Span,
                DiagnosticCodes.ExpectedToken);
        var n = cursor.Current.Text;
        cursor = cursor.Advance();
        return new LambdaParam(n, null, span);
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

    /// Parse a comma-separated expression list (no modifiers, no names),
    /// stopping at `stop` token. Used for array / collection literal element
    /// lists where neither ref/out/in nor `name:` form applies.
    ///
    /// `diags` 透传给子 `Parse()` 调用：传入时单个 arg 失败转 ErrorExpr，
    /// 后续 arg 继续解析（spec enhance-expr-recovery）。
    private static List<Expr> ParseArgList(
        ref TokenCursor cursor, TokenKind stop, LanguageFeatures feat,
        DiagnosticBag? diags = null)
    {
        var args = new List<Expr>();
        while (cursor.Current.Kind != stop && !cursor.IsEnd)
        {
            args.Add(Parse(cursor, feat, diags: diags).Unwrap(ref cursor));
            if (cursor.Current.Kind != TokenKind.Comma) break;
            cursor = cursor.Advance();
        }
        return args;
    }

    /// Parse a comma-separated **call-site** argument list, returning
    /// `List<Argument>` with optional `Name` per argument (spec
    /// add-named-arguments). Each argument may also carry a `ref`/`out`/`in`
    /// modifier (wrapped as `ModifiedArg` inside Argument.Value). Used by
    /// CallExpr and NewExpr (`new T(args)`); not for array element lists.
    internal static List<Argument> ParseCallArgumentList(
        ref TokenCursor cursor, TokenKind stop, LanguageFeatures feat,
        DiagnosticBag? diags = null)
    {
        var args = new List<Argument>();
        while (cursor.Current.Kind != stop && !cursor.IsEnd)
        {
            args.Add(ParseCallArgumentWithOptionalNameAndModifier(ref cursor, feat, diags));
            if (cursor.Current.Kind != TokenKind.Comma) break;
            cursor = cursor.Advance();
        }
        return args;
    }

    /// Parse a single call-site argument that may be prefixed with `<ident> :`
    /// (named argument; spec add-named-arguments) and/or `ref` / `out` / `in`
    /// (spec define-ref-out-in-parameters). When the modifier is `out` and the
    /// next tokens are `var <ident>`, an inline `OutVarDecl` is constructed.
    ///
    /// Named-arg lookahead: `IDENT :` at argument start triggers the named
    /// form. The ternary `a ? b : c` is *not* misread because `?` is required
    /// before `:` in that production, and an arg-start `IDENT` followed
    /// directly by `:` does not appear in any non-named expression form.
    private static Argument ParseCallArgumentWithOptionalNameAndModifier(
        ref TokenCursor cursor, LanguageFeatures feat,
        DiagnosticBag? diags = null)
    {
        var startSpan = cursor.Current.Span;

        // Named-arg lookahead: IDENT followed by `:` at argument start.
        string? name = null;
        Span?   nameSpan = null;
        if (cursor.Current.Kind == TokenKind.Identifier
            && cursor.Advance().Current.Kind == TokenKind.Colon)
        {
            var nameTok = cursor.Current;
            name = nameTok.Text;
            nameSpan = nameTok.Span;
            cursor = cursor.Advance(); // consume identifier
            cursor = cursor.Advance(); // consume `:`
        }

        var modifier = cursor.Current.Kind switch
        {
            TokenKind.Ref => ArgModifier.Ref,
            TokenKind.Out => ArgModifier.Out,
            TokenKind.In  => ArgModifier.In,
            _             => ArgModifier.None,
        };
        if (modifier == ArgModifier.None)
        {
            var bare = Parse(cursor, feat, diags: diags).Unwrap(ref cursor);
            return new Argument(name, bare, startSpan, nameSpan);
        }

        cursor = cursor.Advance(); // consume ref/out/in

        OutVarDecl? outDecl = null;
        Expr inner;
        if (modifier == ArgModifier.Out && cursor.Current.Kind == TokenKind.Var)
        {
            // `out var x` — inline declaration.
            cursor = cursor.Advance(); // consume `var`
            if (cursor.Current.Kind != TokenKind.Identifier)
                throw new ParseException(
                    $"expected identifier after `out var`, got `{cursor.Current.Text}`",
                    cursor.Current.Span,
                    DiagnosticCodes.ExpectedToken);
            var nameTok = cursor.Current;
            cursor = cursor.Advance();
            outDecl = new OutVarDecl(nameTok.Text, AnnotatedType: null, nameTok.Span);
            inner = new IdentExpr(nameTok.Text, nameTok.Span);
        }
        else
        {
            inner = Parse(cursor, feat, diags: diags).Unwrap(ref cursor);
        }

        var modified = new ModifiedArg(inner, modifier, outDecl, startSpan);
        return new Argument(name, modified, startSpan, nameSpan);
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
