using System.Text;
using Z42.Compiler.Features;
using Z42.Compiler.Lexer;

namespace Z42.Compiler.Parser;

/// Core parsing engine. Owns the token stream and drives both the Pratt
/// expression parser and the statement dispatch table.
///
/// Implementation is split across partial class files:
/// • ParserContext.cs          — token stream, Pratt loop, statement dispatch
/// • ParserContext.TopLevel.cs — compilation unit, class, function, enum, type parsing
internal sealed partial class ParserContext
{
    private readonly List<Token>      _tokens;
    private readonly LanguageFeatures _feat;
    private int _pos;

    // Parse context stack — each frame is a human-readable label pushed by major
    // parse methods (e.g. "function declaration", "parameter list").  When
    // Expect() fails, the stack is appended to the error message so the user
    // sees WHERE in the grammar the error occurred, not just which token was
    // unexpected.  This mirrors nom's context() combinator approach.
    private readonly Stack<string> _parseCtx = new();

    internal ParserContext(List<Token> tokens, LanguageFeatures feat)
    {
        _tokens = tokens;
        _feat   = feat;
    }

    // ── Token navigation ──────────────────────────────────────────────────────

    internal Token Current =>
        _pos < _tokens.Count ? _tokens[_pos] : Token.Eof(0);

    internal Token Peek(int offset = 1) =>
        _pos + offset < _tokens.Count ? _tokens[_pos + offset] : Token.Eof(0);

    internal Token Advance()
    {
        var t = Current;
        if (_pos < _tokens.Count) _pos++;
        return t;
    }

    internal Token Expect(TokenKind kind)
    {
        if (Current.Kind != kind)
        {
            string got = Current.Kind == TokenKind.Eof
                ? "unexpected end of file"
                : $"unexpected token `{Current.Text}`";
            throw new ParseException(
                $"{got}, expected `{KindName(kind)}`{ContextSuffix()}",
                Current.Span);
        }
        return Advance();
    }

    internal bool Check(TokenKind kind) => Current.Kind == kind;

    internal bool Match(TokenKind kind)
    {
        if (Current.Kind == kind) { Advance(); return true; }
        return false;
    }

    // ── Feature gate ──────────────────────────────────────────────────────────

    internal void RequireFeature(string name, Span span)
    {
        if (!_feat.IsEnabled(name))
            throw new ParseException($"feature `{name}` is disabled", span);
    }

    // ── Parse context (richer error messages) ─────────────────────────────────

    /// Push a human-readable label onto the parse context stack.
    /// Dispose the returned scope to pop it.  Use with `using var _ = EnterContext(…)`.
    ///
    /// Example error with context:
    ///   unexpected token `}`, expected `)` (while parsing: class declaration `Foo` > parameter list)
    internal ParseContextScope EnterContext(string label)
    {
        _parseCtx.Push(label);
        return new ParseContextScope(_parseCtx);
    }

    /// Formats the current context stack as a parenthetical suffix for error messages.
    /// Returns empty string when the stack is empty.
    /// Example: " (while parsing: class declaration `Foo` > parameter list)"
    private string ContextSuffix() =>
        _parseCtx.Count == 0
            ? ""
            : $" (while parsing: {string.Join(" > ", _parseCtx.Reverse())})";

    /// RAII scope that pops one frame from the parse context stack on dispose.
    internal sealed class ParseContextScope(Stack<string> stack) : IDisposable
    {
        public void Dispose() { if (stack.Count > 0) stack.Pop(); }
    }

    // ── Block & statement parsing ─────────────────────────────────────────────

    internal BlockStmt ParseBlock()
    {
        var span = Current.Span;
        Expect(TokenKind.LBrace);
        var stmts = new List<Stmt>();
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
            stmts.Add(ParseStmt());
        Expect(TokenKind.RBrace);
        return new BlockStmt(stmts, span);
    }

    internal Stmt ParseStmt()
    {
        var span = Current.Span;

        // Keyword-driven dispatch via StmtRules table
        if (ParseTable.StmtRules.TryGetValue(Current.Kind, out var rule))
        {
            if (rule.Feature != null) RequireFeature(rule.Feature, span);
            var kw = Advance();
            return rule.Fn(this, kw);
        }

        // Type-annotated local variable: <Type> <Ident> (= | ;)
        if (IsTypeAnnotatedVarDecl())
        {
            var typeAnn = ParseTypeExpr();
            var vname   = Expect(TokenKind.Identifier).Text;
            Expr? init  = Match(TokenKind.Eq) ? ParseExpr() : null;
            Expect(TokenKind.Semicolon);
            return new VarDeclStmt(vname, typeAnn, init, span);
        }

        // Nested block
        if (Check(TokenKind.LBrace)) return ParseBlock();

        // Expression statement
        var expr = ParseExpr();
        Match(TokenKind.Semicolon);
        return new ExprStmt(expr, span);
    }

    // ── Pratt expression parser ───────────────────────────────────────────────

    /// Parse an expression with the given minimum binding power.
    /// minBp = 0 parses a full expression.
    internal Expr ParseExpr(int minBp = 0)
    {
        var span = Current.Span;

        // Nud: prefix or atom
        if (!ParseTable.ExprRules.TryGetValue(Current.Kind, out var nudRule) || nudRule.Nud == null)
            throw new ParseException(
                $"unexpected token `{Current.Text}` in expression{ContextSuffix()}", span);

        if (nudRule.Feature != null) RequireFeature(nudRule.Feature, span);

        var nudTok = Advance();
        var left   = nudRule.Nud(this, nudTok);

        // Led: infix or postfix loop
        while (true)
        {
            if (!ParseTable.ExprRules.TryGetValue(Current.Kind, out var ledRule)
                || ledRule.Led == null
                || ledRule.LeftBp <= minBp)
                break;

            if (ledRule.Feature != null) RequireFeature(ledRule.Feature, Current.Span);

            var ledTok = Advance();
            left = ledRule.Led(this, left, ledTok);
        }

        return left;
    }

    // ── Interpolated string ───────────────────────────────────────────────────

    internal InterpolatedStrExpr ParseInterpolatedString(Token tok)
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
                var innerCtx    = new ParserContext(innerTokens, _feat);
                parts.Add(new ExprPart(innerCtx.ParseExpr(), tok.Span));
            }
            else
            {
                sb.Append(body[i++]);
            }
        }
        if (sb.Length > 0) parts.Add(new TextPart(sb.ToString(), tok.Span));
        return new InterpolatedStrExpr(parts, tok.Span);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// Lookahead: is the current position a type-annotated variable declaration?
    private bool IsTypeAnnotatedVarDecl()
    {
        if (!IsTypeToken(Current.Kind)) return false;
        // T name = ...  or  T name;
        if (Peek().Kind == TokenKind.Identifier
            && Peek(2).Kind is TokenKind.Eq or TokenKind.Semicolon)
            return true;
        // T[] name = ...  or  T[] name;
        if (Peek().Kind == TokenKind.LBracket
            && Peek(2).Kind == TokenKind.RBracket
            && Peek(3).Kind == TokenKind.Identifier
            && Peek(4).Kind is TokenKind.Eq or TokenKind.Semicolon)
            return true;
        return false;
    }

    /// Wrap a single statement in a synthetic BlockStmt (for brace-free bodies).
    internal BlockStmt WrapInBlock(Stmt stmt) =>
        new BlockStmt([stmt], stmt.Span);

    internal bool IsTypeToken(TokenKind k) => k is
        TokenKind.Void   or TokenKind.String or TokenKind.Int    or TokenKind.Long  or
        TokenKind.Short  or TokenKind.Double or TokenKind.Float  or TokenKind.Byte  or
        TokenKind.Uint   or TokenKind.Ulong  or TokenKind.Ushort or TokenKind.Sbyte or
        TokenKind.Object or TokenKind.Bool   or TokenKind.Char   or
        TokenKind.I8 or TokenKind.I16 or TokenKind.I32 or TokenKind.I64 or
        TokenKind.U8 or TokenKind.U16 or TokenKind.U32 or TokenKind.U64 or
        TokenKind.F32 or TokenKind.F64 or TokenKind.Identifier;

    private static string KindName(TokenKind k) => k.ToString().ToLower();
}
