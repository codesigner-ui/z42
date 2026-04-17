using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser.Core;

namespace Z42.Syntax.Parser;

/// Statement parsers. All functions are static; the token stream is threaded
/// via TokenCursor (value type), so no mutable state is held.
internal static class StmtParser
{
    // ── Delegate / table ──────────────────────────────────────────────────────

    private delegate ParseResult<Stmt> StmtFn(
        TokenCursor cursor, Token kw, LanguageFeatures feat);

    private sealed record StmtEntry(StmtFn Fn, LanguageFeature? Feature = null);

    private static readonly Dictionary<TokenKind, StmtEntry> s_table = new()
    {
        [TokenKind.Var]      = new(ParseVarDecl),
        [TokenKind.Return]   = new(ParseReturn),
        [TokenKind.If]       = new(ParseIf,       LanguageFeature.ControlFlow),
        [TokenKind.While]    = new(ParseWhile,    LanguageFeature.ControlFlow),
        [TokenKind.Do]       = new(ParseDoWhile,  LanguageFeature.ControlFlow),
        [TokenKind.For]      = new(ParseFor,      LanguageFeature.ControlFlow),
        [TokenKind.Foreach]  = new(ParseForeach,  LanguageFeature.ControlFlow),
        [TokenKind.Break]    = new(ParseBreak,    LanguageFeature.ControlFlow),
        [TokenKind.Continue] = new(ParseContinue, LanguageFeature.ControlFlow),
        [TokenKind.Switch]   = new(ParseSwitch,   LanguageFeature.PatternMatch),
        [TokenKind.Try]      = new(ParseTryCatch, LanguageFeature.Exceptions),
        [TokenKind.Throw]    = new(ParseThrow,    LanguageFeature.Exceptions),
    };

    // ── Public entry points ───────────────────────────────────────────────────

    internal static ParseResult<BlockStmt> ParseBlock(
        TokenCursor cursor, LanguageFeatures feat, DiagnosticBag? diags = null)
    {
        var span = cursor.Current.Span;
        if (cursor.Current.Kind != TokenKind.LBrace)
            return ParseResult<BlockStmt>.Fail(cursor, "expected `{`");
        cursor = cursor.Advance();
        var stmts = new List<Stmt>();
        while (cursor.Current.Kind != TokenKind.RBrace && !cursor.IsEnd)
        {
            try
            {
                var r = ParseStmt(cursor, feat);
                if (!r.IsOk)
                {
                    if (diags != null)
                    {
                        diags.Error(DiagnosticCodes.UnexpectedToken, r.Error!, r.ErrorSpan);
                        stmts.Add(new ErrorStmt(r.Error!, r.ErrorSpan));
                        if (!cursor.IsEnd) cursor = cursor.Advance(); // always progress past failing token
                        cursor = SkipToNextStmt(cursor);
                        continue;
                    }
                    return r.AsFailure<BlockStmt>();
                }
                stmts.Add(r.Value);
                cursor = r.Remainder;
            }
            catch (ParseException ex) when (diags != null)
            {
                diags.Error(DiagnosticCodes.UnexpectedToken, ex.Message, ex.Span);
                stmts.Add(new ErrorStmt(ex.Message, ex.Span));
                if (!cursor.IsEnd) cursor = cursor.Advance(); // always progress past failing token
                cursor = SkipToNextStmt(cursor);
            }
        }
        if (cursor.Current.Kind != TokenKind.RBrace)
        {
            var errMsg = $"unexpected end of block; expected `{Combinators.KindDisplay(TokenKind.RBrace)}`";
            if (diags != null)
            {
                diags.Error(DiagnosticCodes.UnexpectedToken, errMsg, cursor.Current.Span);
                return ParseResult<BlockStmt>.Ok(new BlockStmt(stmts, span), cursor);
            }
            throw new ParseException(errMsg, cursor.Current.Span);
        }
        cursor = cursor.Advance();
        return ParseResult<BlockStmt>.Ok(new BlockStmt(stmts, span), cursor);
    }

    /// Skip tokens until we reach a plausible start of the next statement.
    private static TokenCursor SkipToNextStmt(TokenCursor cursor)
    {
        while (!cursor.IsEnd)
        {
            var kind = cursor.Current.Kind;
            // After a semicolon, the next statement starts
            if (kind == TokenKind.Semicolon) { cursor = cursor.Advance(); break; }
            // Stop at `}` (end of block — don't consume it)
            if (kind == TokenKind.RBrace) break;
            // Stop at statement-starting keywords
            if (kind is TokenKind.Var or TokenKind.Return
                or TokenKind.If or TokenKind.While or TokenKind.Do
                or TokenKind.For or TokenKind.Foreach
                or TokenKind.Break or TokenKind.Continue
                or TokenKind.Switch or TokenKind.Try or TokenKind.Throw
                or TokenKind.LBrace)
                break;
            cursor = cursor.Advance();
        }
        return cursor;
    }

    internal static ParseResult<Stmt> ParseStmt(
        TokenCursor cursor, LanguageFeatures feat)
    {
        var span = cursor.Current.Span;

        // 1. Keyword-driven dispatch
        if (s_table.TryGetValue(cursor.Current.Kind, out var entry))
        {
            if (entry.Feature is { } f && !feat.IsEnabled(f))
                throw new ParseException($"feature `{LanguageFeatures.Metadata[f].Name}` is disabled", span);
            var kw = cursor.Current;
            cursor = cursor.Advance();
            return entry.Fn(cursor, kw, feat);
        }

        // 2. Type-annotated local: `Type name = expr;` or `Type name;`
        if (IsTypeAnnotatedVarDecl(cursor))
        {
            var ty     = TypeParser.Parse(cursor).Unwrap(ref cursor);
            var vname  = Expect(ref cursor, TokenKind.Identifier).Text;
            Expr? init = null;
            if (cursor.Current.Kind == TokenKind.Eq)
            {
                cursor = cursor.Advance();
                init = ExprParser.Parse(cursor, feat).Unwrap(ref cursor);
            }
            Expect(ref cursor, TokenKind.Semicolon);
            return ParseResult<Stmt>.Ok(new VarDeclStmt(vname, ty, init, span), cursor);
        }

        // 3. Nested block
        if (cursor.Current.Kind == TokenKind.LBrace)
            return ParseBlock(cursor, feat).Map(b => (Stmt)b);

        // 4. Expression statement
        {
            var exprR = ExprParser.Parse(cursor, feat);
            if (!exprR.IsOk)
                throw new ParseException(exprR.Error!, exprR.ErrorSpan);
            cursor = exprR.Remainder;
            if (cursor.Current.Kind == TokenKind.Semicolon)
                cursor = cursor.Advance();
            return ParseResult<Stmt>.Ok(new ExprStmt(exprR.Value, span), cursor);
        }
    }

    // ── Statement handlers ────────────────────────────────────────────────────

    private static ParseResult<Stmt> ParseVarDecl(
        TokenCursor cursor, Token kw, LanguageFeatures feat)
    {
        var name  = Expect(ref cursor, TokenKind.Identifier).Text;
        Expr? init = null;
        if (cursor.Current.Kind == TokenKind.Eq)
        {
            cursor = cursor.Advance();
            init = ExprParser.Parse(cursor, feat).Unwrap(ref cursor);
        }
        Expect(ref cursor, TokenKind.Semicolon);
        return ParseResult<Stmt>.Ok(new VarDeclStmt(name, null, init, kw.Span), cursor);
    }

    private static ParseResult<Stmt> ParseReturn(
        TokenCursor cursor, Token kw, LanguageFeatures feat)
    {
        Expr? val = cursor.Current.Kind == TokenKind.Semicolon
            ? null
            : ExprParser.Parse(cursor, feat).Unwrap(ref cursor);
        Expect(ref cursor, TokenKind.Semicolon);
        return ParseResult<Stmt>.Ok(new ReturnStmt(val, kw.Span), cursor);
    }

    private static ParseResult<Stmt> ParseIf(
        TokenCursor cursor, Token kw, LanguageFeatures feat)
    {
        Expect(ref cursor, TokenKind.LParen);
        var cond = ExprParser.Parse(cursor, feat).Unwrap(ref cursor);
        Expect(ref cursor, TokenKind.RParen);
        var then = BlockOrSingle(cursor, feat, out cursor);

        Stmt? else_ = null;
        if (cursor.Current.Kind == TokenKind.Else)
        {
            cursor = cursor.Advance();
            if (cursor.Current.Kind == TokenKind.If)
            {
                var ifKw = cursor.Current;
                cursor = cursor.Advance();
                else_ = ParseIf(cursor, ifKw, feat).Unwrap(ref cursor);
            }
            else
            {
                else_ = BlockOrSingle(cursor, feat, out cursor);
            }
        }
        return ParseResult<Stmt>.Ok(new IfStmt(cond, then, else_, kw.Span), cursor);
    }

    private static ParseResult<Stmt> ParseWhile(
        TokenCursor cursor, Token kw, LanguageFeatures feat)
    {
        Expect(ref cursor, TokenKind.LParen);
        var cond = ExprParser.Parse(cursor, feat).Unwrap(ref cursor);
        Expect(ref cursor, TokenKind.RParen);
        var body = ParseBlock(cursor, feat).Unwrap(ref cursor);
        return ParseResult<Stmt>.Ok(new WhileStmt(cond, body, kw.Span), cursor);
    }

    private static ParseResult<Stmt> ParseDoWhile(
        TokenCursor cursor, Token kw, LanguageFeatures feat)
    {
        var body = ParseBlock(cursor, feat).Unwrap(ref cursor);
        Expect(ref cursor, TokenKind.While);
        Expect(ref cursor, TokenKind.LParen);
        var cond = ExprParser.Parse(cursor, feat).Unwrap(ref cursor);
        Expect(ref cursor, TokenKind.RParen);
        if (cursor.Current.Kind == TokenKind.Semicolon) cursor = cursor.Advance();
        return ParseResult<Stmt>.Ok(new DoWhileStmt(body, cond, kw.Span), cursor);
    }

    private static ParseResult<Stmt> ParseFor(
        TokenCursor cursor, Token kw, LanguageFeatures feat)
    {
        Expect(ref cursor, TokenKind.LParen);

        // init
        Stmt? init = null;
        if (cursor.Current.Kind != TokenKind.Semicolon)
        {
            var r = ParseStmt(cursor, feat);
            if (!r.IsOk) return r;
            init   = r.Value;
            cursor = r.Remainder;
        }
        else cursor = cursor.Advance(); // consume `;`

        // condition
        Expr? cond = null;
        if (cursor.Current.Kind != TokenKind.Semicolon)
            cond = ExprParser.Parse(cursor, feat).Unwrap(ref cursor);
        Expect(ref cursor, TokenKind.Semicolon);

        // increment
        Expr? incr = null;
        if (cursor.Current.Kind != TokenKind.RParen)
            incr = ExprParser.Parse(cursor, feat).Unwrap(ref cursor);
        Expect(ref cursor, TokenKind.RParen);

        var body = ParseBlock(cursor, feat).Unwrap(ref cursor);
        return ParseResult<Stmt>.Ok(new ForStmt(init, cond, incr, body, kw.Span), cursor);
    }

    private static ParseResult<Stmt> ParseForeach(
        TokenCursor cursor, Token kw, LanguageFeatures feat)
    {
        Expect(ref cursor, TokenKind.LParen);
        if (cursor.Current.Kind == TokenKind.Var) cursor = cursor.Advance(); // optional var
        var vname      = Expect(ref cursor, TokenKind.Identifier).Text;
        Expect(ref cursor, TokenKind.In);
        var collection = ExprParser.Parse(cursor, feat).Unwrap(ref cursor);
        Expect(ref cursor, TokenKind.RParen);
        var body = ParseBlock(cursor, feat).Unwrap(ref cursor);
        return ParseResult<Stmt>.Ok(new ForeachStmt(vname, collection, body, kw.Span), cursor);
    }

    private static ParseResult<Stmt> ParseBreak(
        TokenCursor cursor, Token kw, LanguageFeatures feat)
    {
        if (cursor.Current.Kind == TokenKind.Semicolon) cursor = cursor.Advance();
        return ParseResult<Stmt>.Ok(new BreakStmt(kw.Span), cursor);
    }

    private static ParseResult<Stmt> ParseContinue(
        TokenCursor cursor, Token kw, LanguageFeatures feat)
    {
        if (cursor.Current.Kind == TokenKind.Semicolon) cursor = cursor.Advance();
        return ParseResult<Stmt>.Ok(new ContinueStmt(kw.Span), cursor);
    }

    private static ParseResult<Stmt> ParseSwitch(
        TokenCursor cursor, Token kw, LanguageFeatures feat)
    {
        Expect(ref cursor, TokenKind.LParen);
        var subject = ExprParser.Parse(cursor, feat).Unwrap(ref cursor);
        Expect(ref cursor, TokenKind.RParen);
        Expect(ref cursor, TokenKind.LBrace);

        var cases = new List<SwitchCase>();
        while (cursor.Current.Kind != TokenKind.RBrace && !cursor.IsEnd)
        {
            var caseSpan = cursor.Current.Span;
            Expr? pattern;
            if (cursor.Current.Kind == TokenKind.Default)
            {
                cursor  = cursor.Advance();
                pattern = null;
                Expect(ref cursor, TokenKind.Colon);
            }
            else
            {
                Expect(ref cursor, TokenKind.Case);
                pattern = ExprParser.Parse(cursor, feat, 11).Unwrap(ref cursor);
                Expect(ref cursor, TokenKind.Colon);
            }

            var body = new List<Stmt>();
            while (cursor.Current.Kind is not (TokenKind.Case or TokenKind.Default
                                               or TokenKind.RBrace or TokenKind.Eof))
            {
                var r = ParseStmt(cursor, feat);
                if (!r.IsOk) return r.AsFailure<Stmt>();
                body.Add(r.Value);
                cursor = r.Remainder;
            }
            cases.Add(new SwitchCase(pattern, body, caseSpan));
        }

        Expect(ref cursor, TokenKind.RBrace);
        return ParseResult<Stmt>.Ok(new SwitchStmt(subject, cases, kw.Span), cursor);
    }

    private static ParseResult<Stmt> ParseTryCatch(
        TokenCursor cursor, Token kw, LanguageFeatures feat)
    {
        var tryBody = ParseBlock(cursor, feat).Unwrap(ref cursor);

        var catches = new List<CatchClause>();
        while (cursor.Current.Kind == TokenKind.Catch)
        {
            var cSpan  = cursor.Current.Span;
            cursor = cursor.Advance(); // consume catch
            string? exType  = null;
            string? varName = null;
            if (cursor.Current.Kind == TokenKind.LParen)
            {
                cursor = cursor.Advance();
                if (cursor.Current.Kind == TokenKind.Identifier)
                {
                    exType = cursor.Current.Text;
                    cursor = cursor.Advance();
                    if (cursor.Current.Kind == TokenKind.Identifier)
                    {
                        varName = cursor.Current.Text;
                        cursor  = cursor.Advance();
                    }
                }
                Expect(ref cursor, TokenKind.RParen);
            }
            var catchBody = ParseBlock(cursor, feat).Unwrap(ref cursor);
            catches.Add(new CatchClause(exType, varName, catchBody, cSpan));
        }

        BlockStmt? finally_ = null;
        if (cursor.Current.Kind == TokenKind.Finally)
        {
            cursor   = cursor.Advance();
            finally_ = ParseBlock(cursor, feat).Unwrap(ref cursor);
        }

        return ParseResult<Stmt>.Ok(
            new TryCatchStmt(tryBody, catches, finally_, kw.Span), cursor);
    }

    private static ParseResult<Stmt> ParseThrow(
        TokenCursor cursor, Token kw, LanguageFeatures feat)
    {
        var val = ExprParser.Parse(cursor, feat).Unwrap(ref cursor);
        Expect(ref cursor, TokenKind.Semicolon);
        return ParseResult<Stmt>.Ok(new ThrowStmt(val, kw.Span), cursor);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// Parse `{ block }` or a single statement, returning a BlockStmt either way.
    private static BlockStmt BlockOrSingle(
        TokenCursor cursor, LanguageFeatures feat, out TokenCursor rest)
    {
        if (cursor.Current.Kind == TokenKind.LBrace)
        {
            var r = ParseBlock(cursor, feat);
            rest = r.Remainder;
            return r.OrThrow();
        }
        var span  = cursor.Current.Span;
        var stmtR = ParseStmt(cursor, feat);
        rest = stmtR.Remainder;
        return new BlockStmt([stmtR.OrThrow()], span);
    }

    /// Lookahead: is `cursor` pointing at a type-annotated variable declaration?
    private static bool IsTypeAnnotatedVarDecl(TokenCursor cursor)
    {
        if (!TypeParser.IsTypeToken(cursor.Current.Kind)) return false;
        // T name = ...  or  T name;
        if (cursor.Peek(1).Kind == TokenKind.Identifier
            && cursor.Peek(2).Kind is TokenKind.Eq or TokenKind.Semicolon)
            return true;
        // T? name = ...  or  T? name;
        if (cursor.Peek(1).Kind == TokenKind.Question
            && cursor.Peek(2).Kind == TokenKind.Identifier
            && cursor.Peek(3).Kind is TokenKind.Eq or TokenKind.Semicolon)
            return true;
        // T[] name = ...  or  T[] name;
        if (cursor.Peek(1).Kind == TokenKind.LBracket
            && cursor.Peek(2).Kind == TokenKind.RBracket
            && cursor.Peek(3).Kind == TokenKind.Identifier
            && cursor.Peek(4).Kind is TokenKind.Eq or TokenKind.Semicolon)
            return true;
        // T?[] name = ...  or  T?[] name;
        if (cursor.Peek(1).Kind == TokenKind.Question
            && cursor.Peek(2).Kind == TokenKind.LBracket
            && cursor.Peek(3).Kind == TokenKind.RBracket
            && cursor.Peek(4).Kind == TokenKind.Identifier
            && cursor.Peek(5).Kind is TokenKind.Eq or TokenKind.Semicolon)
            return true;
        return false;
    }

    /// Consume an expected token or throw ParseException.
    private static Token Expect(ref TokenCursor cursor, TokenKind kind)
    {
        if (cursor.Current.Kind != kind)
            throw new ParseException(
                $"expected `{Combinators.KindDisplay(kind)}`, got `{cursor.Current.Text}`",
                cursor.Current.Span);
        var tok = cursor.Current;
        cursor  = cursor.Advance();
        return tok;
    }
}
