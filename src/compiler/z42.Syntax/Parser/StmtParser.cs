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
        // Spec C5 (`impl-pinned-syntax`) — `pinned p = s { ... }` block.
        [TokenKind.Pinned]   = new(ParsePinned),
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
                diags.Error(ex.Code ?? DiagnosticCodes.UnexpectedToken, ex.Message, ex.Span);
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
            throw new ParseException(errMsg, cursor.Current.Span,
                DiagnosticCodes.ExpectedToken);
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
                or TokenKind.Pinned
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
                throw new ParseException($"feature `{LanguageFeatures.Metadata[f].Name}` is disabled", span,
                    DiagnosticCodes.FeatureDisabled);
            var kw = cursor.Current;
            cursor = cursor.Advance();
            return entry.Fn(cursor, kw, feat);
        }

        // 2a. Local function declaration: `Type Name(params) => expr;` or
        //     `Type Name(params) { body }`. Must be checked BEFORE
        //     `IsTypeAnnotatedVarDecl` since both start with `Type Identifier`,
        //     and BEFORE the func-type var-decl path so we don't misinterpret
        //     a local fn whose return type is `(T) -> R`.
        //     See docs/design/closure.md §3.4 + impl-local-fn-l2 design.
        if (IsLocalFunctionDecl(cursor))
        {
            return ParseLocalFunctionStmt(ref cursor, feat, span);
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

    /// Spec C5 (`impl-pinned-syntax`):
    ///   pinned <ident> = <expr> { <body> }
    /// Borrows a `string` for the body's duration so it can be handed to
    /// native code as `(p.ptr, p.len)`. The body is parsed with the standard
    /// `ParseBlock`; control-flow restrictions (no `return`/`break`/etc.)
    /// are enforced at type-check time, not in the parser.
    private static ParseResult<Stmt> ParsePinned(
        TokenCursor cursor, Token kw, LanguageFeatures feat)
    {
        var name = Expect(ref cursor, TokenKind.Identifier).Text;
        Expect(ref cursor, TokenKind.Eq);
        var source = ExprParser.Parse(cursor, feat).Unwrap(ref cursor);
        var body = ParseBlock(cursor, feat).Unwrap(ref cursor);
        return ParseResult<Stmt>.Ok(new PinnedStmt(name, source, body, kw.Span), cursor);
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
        // Function type form `(T1, ...) -> R IDENT [= init];` — see closure.md §3.2.
        if (cursor.Current.Kind == TokenKind.LParen)
        {
            return IsFuncTypeVarDecl(cursor);
        }
        if (!TypeParser.IsTypeToken(cursor.Current.Kind)) return false;
        // 2026-04-27 fix-generic-array-type-parsing：扫过类型表达式（含 `<...>`、
        // `[]`、`?` 任意组合），停在第一个非类型 token；若后接 `Identifier`
        // + (`=` | `;`) 即为带类型注解的 var decl。
        int i = 1;
        // 可选 `<T1, T2, ...>` —— 用深度计数支持嵌套泛型（GtGt 计为 2 个 close）
        if (cursor.Peek(i).Kind == TokenKind.Lt)
        {
            int depth = 1;
            i++;
            while (depth > 0 && cursor.Peek(i).Kind != TokenKind.Eof)
            {
                var k = cursor.Peek(i).Kind;
                if      (k == TokenKind.Lt) depth++;
                else if (k == TokenKind.Gt) depth--;
                else if (k == TokenKind.GtGt) depth -= 2;
                i++;
            }
            if (depth != 0) return false;
        }
        // 2026-05-04 D-6: 可选 `.Ident.Ident...` dotted-path（嵌套 delegate 外部引用）
        while (cursor.Peek(i).Kind == TokenKind.Dot
            && cursor.Peek(i + 1).Kind == TokenKind.Identifier)
        {
            i += 2;
        }
        // 可选 `?` 或 `[]`（顺序：先 `?` 再 `[]`，或仅一种）
        if (cursor.Peek(i).Kind == TokenKind.Question) i++;
        if (cursor.Peek(i).Kind == TokenKind.LBracket
            && cursor.Peek(i + 1).Kind == TokenKind.RBracket)
            i += 2;
        // 终态：Identifier + (= | ;)
        return cursor.Peek(i).Kind == TokenKind.Identifier
            && cursor.Peek(i + 1).Kind is TokenKind.Eq or TokenKind.Semicolon;
    }

    /// Lookahead: is `cursor` pointing at a local-function declaration of the
    /// form `Type Name(params) => expr;` or `Type Name(params) { body }`?
    /// Distinguishes from var-decl (`Type Name = init;`) by the `(` after Name.
    /// See docs/design/closure.md §3.4 + impl-local-fn-l2 design.
    private static bool IsLocalFunctionDecl(TokenCursor cursor)
    {
        // Skip the return type expression and check whether the next two
        // tokens form `Identifier '('`. Two starting forms:
        //   1) Type-keyword (`int`, `string`, generic, array, nullable): walk
        //      via the same skip logic used by `IsTypeAnnotatedVarDecl`
        //   2) Function-type return `(T) -> R`: walk to matching `)` then `->` then type
        if (!TypeParser.IsTypeToken(cursor.Current.Kind)
            && cursor.Current.Kind != TokenKind.LParen)
            return false;

        int i = SkipTypeExprForLookahead(cursor, 0);
        if (i <= 0) return false;
        return cursor.Peek(i).Kind == TokenKind.Identifier
            && cursor.Peek(i + 1).Kind == TokenKind.LParen;
    }

    /// Walk a type expression from `cursor.Peek(start)` and return the index
    /// just past it. Returns -1 if no valid type expression starts there.
    /// Handles type-keyword + generics + array + nullable; also `(T) -> R`.
    /// Bounded by Eof to avoid infinite loops.
    private static int SkipTypeExprForLookahead(TokenCursor cursor, int start)
    {
        int i = start;
        var first = cursor.Peek(i).Kind;

        // Function type `(T) -> R` (possibly nested return)
        if (first == TokenKind.LParen)
        {
            int depth = 1; i++;
            while (depth > 0)
            {
                var k = cursor.Peek(i).Kind;
                if (k == TokenKind.Eof) return -1;
                if (k == TokenKind.LParen) depth++;
                else if (k == TokenKind.RParen) depth--;
                i++;
            }
            if (cursor.Peek(i).Kind != TokenKind.Arrow) return -1;
            i++;
            return SkipTypeExprForLookahead(cursor, i);
        }

        // Type-keyword (int / string / generic-class / etc.)
        if (!TypeParser.IsTypeToken(first)) return -1;
        i++;
        // Optional `<T1, T2, ...>` — GtGt 计为 2 个 close（嵌套泛型）
        if (cursor.Peek(i).Kind == TokenKind.Lt)
        {
            int depth = 1; i++;
            while (depth > 0 && cursor.Peek(i).Kind != TokenKind.Eof)
            {
                var k = cursor.Peek(i).Kind;
                if (k == TokenKind.Lt) depth++;
                else if (k == TokenKind.Gt) depth--;
                else if (k == TokenKind.GtGt) depth -= 2;
                i++;
            }
            if (depth != 0) return -1;
        }
        // 2026-05-04 D-6: Optional dotted-path `.Ident.Ident...`（嵌套 delegate 外部引用）
        while (cursor.Peek(i).Kind == TokenKind.Dot
            && cursor.Peek(i + 1).Kind == TokenKind.Identifier)
        {
            i += 2;
        }
        // Optional `?` then optional `[]`
        if (cursor.Peek(i).Kind == TokenKind.Question) i++;
        if (cursor.Peek(i).Kind == TokenKind.LBracket
            && cursor.Peek(i + 1).Kind == TokenKind.RBracket)
            i += 2;
        return i;
    }

    /// Parse a local-function declaration as a statement. Caller must have
    /// confirmed `IsLocalFunctionDecl(cursor)`.
    /// Reuses `TopLevelParser.ParseFunctionDecl` so the body grammar (block,
    /// expression body, etc.) stays consistent with top-level functions.
    private static ParseResult<Stmt> ParseLocalFunctionStmt(
        ref TokenCursor cursor, LanguageFeatures feat, Span span)
    {
        var decl = TopLevelParser.ParseFunctionDecl(ref cursor, feat, Visibility.Internal);
        return ParseResult<Stmt>.Ok(new LocalFunctionStmt(decl, span), cursor);
    }

    /// Lookahead: is `cursor` (at `(`) pointing at a function-type-annotated
    /// variable declaration of the form `(T1, ...) -> R IDENT [= init];`?
    /// Differs from a parenthesised expression because of the `-> Type IDENT`
    /// continuation.
    private static bool IsFuncTypeVarDecl(TokenCursor cursor)
    {
        if (cursor.Current.Kind != TokenKind.LParen) return false;

        // Walk to matching `)` at depth 0.
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

        // After `)`, expect `->` then a type expression then IDENT then `=` or `;`.
        if (cursor.Peek(i).Kind != TokenKind.Arrow) return false;
        i++;

        // Skip the return type — accept either a type keyword (possibly followed
        // by `<...>`, `?`, `[]`) or a nested `(...) -> ...` chain. We only need
        // to confirm the pattern terminates in `IDENT (= | ;)`, so a simple
        // bracket-balanced walk that stops on a free-standing IDENT works.
        // Bound `i` by Eof to avoid infinite loops.
        int start = i;
        while (cursor.Peek(i).Kind != TokenKind.Eof)
        {
            var k = cursor.Peek(i).Kind;
            if (k == TokenKind.LParen)
            {
                int d = 1; i++;
                while (d > 0 && cursor.Peek(i).Kind != TokenKind.Eof)
                {
                    var ki = cursor.Peek(i).Kind;
                    if (ki == TokenKind.LParen) d++;
                    else if (ki == TokenKind.RParen) d--;
                    i++;
                }
                continue;
            }
            if (k == TokenKind.Lt)
            {
                int d = 1; i++;
                while (d > 0 && cursor.Peek(i).Kind != TokenKind.Eof)
                {
                    var ki = cursor.Peek(i).Kind;
                    if (ki == TokenKind.Lt) d++;
                    else if (ki == TokenKind.Gt) d--;
                    else if (ki == TokenKind.GtGt) d -= 2;
                    i++;
                }
                continue;
            }
            // Once we encounter an Identifier with `=` or `;` next, we have
            // found the variable name.
            if (k == TokenKind.Identifier
                && cursor.Peek(i + 1).Kind is TokenKind.Eq or TokenKind.Semicolon)
                return i > start;
            // Continue walking through type tokens / `?` / `[]` / `,` / `Arrow`.
            i++;
        }
        return false;
    }

    /// Consume an expected token or throw ParseException.
    private static Token Expect(ref TokenCursor cursor, TokenKind kind)
    {
        if (cursor.Current.Kind != kind)
            throw new ParseException(
                $"expected `{Combinators.KindDisplay(kind)}`, got `{cursor.Current.Text}`",
                cursor.Current.Span,
                DiagnosticCodes.ExpectedToken);
        var tok = cursor.Current;
        cursor  = cursor.Advance();
        return tok;
    }
}
