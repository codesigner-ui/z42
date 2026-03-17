using System.Text;
using Z42.Compiler.Features;
using Z42.Compiler.Lexer;

namespace Z42.Compiler.Parser;

/// Core parsing engine. Owns the token stream and drives both the Pratt
/// expression parser and the statement dispatch table.
/// <para>
/// Public surface: <see cref="ParseCompilationUnit"/>, <see cref="ParseExpr(int)"/>.
/// All other members are internal, consumed by Nuds / Leds / Stmts handlers.
/// </para>
internal sealed class ParserContext
{
    private readonly List<Token>     _tokens;
    private readonly LanguageFeatures _feat;
    private int _pos;

    internal ParserContext(List<Token> tokens, LanguageFeatures feat)
    {
        _tokens = tokens;
        _feat   = feat;
    }

    // ── Token navigation ─────────────────────────────────────────────────────

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
                $"{got}, expected `{KindName(kind)}`", Current.Span);
        }
        return Advance();
    }

    internal bool Check(TokenKind kind) => Current.Kind == kind;

    internal bool Match(TokenKind kind)
    {
        if (Current.Kind == kind) { Advance(); return true; }
        return false;
    }

    // ── Feature gate ─────────────────────────────────────────────────────────

    internal void RequireFeature(string name, Span span)
    {
        if (!_feat.IsEnabled(name))
            throw new ParseException(
                $"feature `{name}` is disabled", span);
    }

    // ── Top level ─────────────────────────────────────────────────────────────

    internal CompilationUnit ParseCompilationUnit()
    {
        var start  = Current.Span;
        string? ns = null;
        var usings = new List<string>();

        if (Match(TokenKind.Namespace))
        {
            ns = ParseQualifiedName();
            Match(TokenKind.Semicolon);
        }

        while (Check(TokenKind.Using))
        {
            Advance();
            usings.Add(ParseQualifiedName());
            Match(TokenKind.Semicolon);
        }

        var functions = new List<FunctionDecl>();
        while (!Check(TokenKind.Eof))
        {
            if (Check(TokenKind.LBracket)) { SkipAttribute(); continue; }
            if (Check(TokenKind.Namespace)) { Advance(); ParseQualifiedName(); Match(TokenKind.Semicolon); continue; }
            if (Check(TokenKind.Using))     { Advance(); ParseQualifiedName(); Match(TokenKind.Semicolon); continue; }
            functions.Add(ParseFunctionDecl());
        }

        return new CompilationUnit(ns, usings, functions, start);
    }

    private string ParseQualifiedName()
    {
        var sb = new StringBuilder(Expect(TokenKind.Identifier).Text);
        while (Match(TokenKind.Dot))
            sb.Append('.').Append(Expect(TokenKind.Identifier).Text);
        return sb.ToString();
    }

    private void SkipAttribute()
    {
        Expect(TokenKind.LBracket);
        int depth = 1;
        while (!Check(TokenKind.Eof) && depth > 0)
        {
            if (Check(TokenKind.LBracket)) depth++;
            if (Check(TokenKind.RBracket)) depth--;
            Advance();
        }
    }

    // ── Function declaration ──────────────────────────────────────────────────

    private FunctionDecl ParseFunctionDecl()
    {
        var start = Current.Span;

        while (Current.Kind is TokenKind.Public or TokenKind.Private or TokenKind.Protected
                             or TokenKind.Internal or TokenKind.Static or TokenKind.Async
                             or TokenKind.Abstract or TokenKind.Override or TokenKind.Virtual
                             or TokenKind.Sealed)
            Advance();

        var returnType = ParseTypeExpr();
        var name       = Expect(TokenKind.Identifier).Text;

        Expect(TokenKind.LParen);
        var parms = new List<Param>();
        while (!Check(TokenKind.RParen) && !Check(TokenKind.Eof))
        {
            var pSpan = Current.Span;
            var pType = ParseTypeExpr();
            var pName = Expect(TokenKind.Identifier).Text;
            parms.Add(new Param(pName, pType, pSpan));
            if (!Match(TokenKind.Comma)) break;
        }
        Expect(TokenKind.RParen);

        var body = ParseBlock();
        return new FunctionDecl(name, parms, returnType, body, start);
    }

    // ── Types ─────────────────────────────────────────────────────────────────

    internal TypeExpr ParseTypeExpr()
    {
        var span = Current.Span;

        string name = Current.Kind switch
        {
            TokenKind.Void   => Advance().Text,
            TokenKind.String => Advance().Text,
            TokenKind.Int    => Advance().Text,
            TokenKind.Long   => Advance().Text,
            TokenKind.Short  => Advance().Text,
            TokenKind.Double => Advance().Text,
            TokenKind.Float  => Advance().Text,
            TokenKind.Byte   => Advance().Text,
            TokenKind.Uint   => Advance().Text,
            TokenKind.Ulong  => Advance().Text,
            TokenKind.Ushort => Advance().Text,
            TokenKind.Sbyte  => Advance().Text,
            TokenKind.Object => Advance().Text,
            TokenKind.Bool   => Advance().Text,
            TokenKind.Char   => Advance().Text,
            TokenKind.I8 or TokenKind.I16 or TokenKind.I32 or TokenKind.I64 or
            TokenKind.U8 or TokenKind.U16 or TokenKind.U32 or TokenKind.U64 or
            TokenKind.F32 or TokenKind.F64 => Advance().Text,
            TokenKind.Identifier => Advance().Text,
            _ => throw new ParseException($"expected type name, got `{Current.Text}`", span)
        };

        TypeExpr ty = name == "void" ? new VoidType(span) : new NamedType(name, span);

        if (Check(TokenKind.LBracket) && Peek().Kind == TokenKind.RBracket)
        {
            Advance(); Advance();
            ty = new ArrayType(ty, span);
        }
        else if (Check(TokenKind.Lt))
        {
            Advance();
            int depth = 1;
            while (!Check(TokenKind.Eof) && depth > 0)
            {
                if (Check(TokenKind.Lt)) depth++;
                if (Check(TokenKind.Gt)) depth--;
                Advance();
            }
        }

        if (Match(TokenKind.Question))
            ty = new OptionType(ty, span);

        return ty;
    }

    // ── Block & statements ────────────────────────────────────────────────────

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

        // ── Keyword-driven dispatch via StmtRules table ──
        if (ParseTable.StmtRules.TryGetValue(Current.Kind, out var rule))
        {
            if (rule.Feature != null)
                RequireFeature(rule.Feature, span);
            var kw = Advance();
            return rule.Fn(this, kw);
        }

        // ── Type-annotated local variable: <Type> <Ident> (= | ;) ──
        // Handles: int x = ...; string s; int[] arr = ...; etc.
        if (IsTypeAnnotatedVarDecl())
        {
            var typeAnn = ParseTypeExpr();
            var vname   = Expect(TokenKind.Identifier).Text;
            Expr? init  = Match(TokenKind.Eq) ? ParseExpr() : null;
            Expect(TokenKind.Semicolon);
            return new VarDeclStmt(vname, typeAnn, init, span);
        }

        // ── Nested block ──
        if (Check(TokenKind.LBrace)) return ParseBlock();

        // ── Expression statement ──
        var expr = ParseExpr();
        Match(TokenKind.Semicolon);
        return new ExprStmt(expr, span);
    }

    // ── Pratt expression parser ───────────────────────────────────────────────

    /// Parse an expression with the given minimum binding power.
    /// <paramref name="minBp"/> = 0 parses a full expression.
    internal Expr ParseExpr(int minBp = 0)
    {
        var span = Current.Span;

        // Nud: prefix or atom
        if (!ParseTable.ExprRules.TryGetValue(Current.Kind, out var nudRule) || nudRule.Nud == null)
            throw new ParseException(
                $"unexpected token `{Current.Text}` in expression", span);

        if (nudRule.Feature != null)
            RequireFeature(nudRule.Feature, span);

        var nudTok = Advance();
        var left   = nudRule.Nud(this, nudTok);

        // Led: infix or postfix loop
        while (true)
        {
            if (!ParseTable.ExprRules.TryGetValue(Current.Kind, out var ledRule)
                || ledRule.Led == null
                || ledRule.LeftBp <= minBp)
                break;

            if (ledRule.Feature != null)
                RequireFeature(ledRule.Feature, Current.Span);

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
                var innerTokens = new Z42.Compiler.Lexer.Lexer(exprSrc.ToString()).Tokenize();
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
    /// Handles:  T name = ...  and  T[] name = ...
    private bool IsTypeAnnotatedVarDecl()
    {
        if (!IsTypeToken(Current.Kind)) return false;

        // T name = ...  (plain type)
        if (Peek().Kind == TokenKind.Identifier
            && (Peek(2).Kind is TokenKind.Eq or TokenKind.Semicolon))
            return true;

        // T[] name = ...  (array type: current is type, next is "[", then "]", then ident)
        if (Peek().Kind == TokenKind.LBracket
            && Peek(2).Kind == TokenKind.RBracket
            && Peek(3).Kind == TokenKind.Identifier
            && (Peek(4).Kind is TokenKind.Eq or TokenKind.Semicolon))
            return true;

        return false;
    }

    /// Wrap a single statement in a synthetic BlockStmt (for brace-free if/else/while bodies).
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
