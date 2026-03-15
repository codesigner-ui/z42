using System.Text;
using Z42.Compiler.Lexer;

namespace Z42.Compiler.Parser;

/// <summary>
/// Recursive-descent parser for z42 Phase 1 (C# syntax).
/// </summary>
public sealed class Parser(List<Token> tokens)
{
    private int _pos;

    private Token Current    => _pos < tokens.Count ? tokens[_pos] : Token.Eof(0);
    private Token Peek(int offset = 1) =>
        _pos + offset < tokens.Count ? tokens[_pos + offset] : Token.Eof(0);

    private Token Advance()
    {
        var t = Current;
        if (_pos < tokens.Count) _pos++;
        return t;
    }

    private Token Expect(TokenKind kind)
    {
        if (Current.Kind != kind)
            throw new ParseException(
                $"expected `{KindName(kind)}` but got `{Current.Text}`", Current.Span);
        return Advance();
    }

    private bool Check(TokenKind kind) => Current.Kind == kind;
    private bool Match(TokenKind kind) { if (Check(kind)) { Advance(); return true; } return false; }

    // ── Top level ─────────────────────────────────────────────────────────────

    public CompilationUnit ParseCompilationUnit()
    {
        var start = Current.Span;
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
            // Skip stray [ExecMode(...)] attribute annotations
            if (Check(TokenKind.LBracket)) { SkipAttribute(); continue; }
            // Duplicate namespace / using (for exec mode + namespace re-declaration)
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

        // Skip access / other modifiers
        while (Current.Kind is TokenKind.Public or TokenKind.Private or TokenKind.Protected
                             or TokenKind.Internal or TokenKind.Static or TokenKind.Async
                             or TokenKind.Abstract or TokenKind.Override or TokenKind.Virtual
                             or TokenKind.Sealed)
            Advance();

        var returnType = ParseTypeExpr();
        var name = Expect(TokenKind.Identifier).Text;

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

    private TypeExpr ParseTypeExpr()
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

        TypeExpr ty = name == "void"
            ? new VoidType(span)
            : new NamedType(name, span);

        // T[] array type
        if (Check(TokenKind.LBracket) && Peek().Kind == TokenKind.RBracket)
        {
            Advance(); Advance(); // [ ]
            ty = new ArrayType(ty, span);
        }
        // Generic: Type<T> — consume and discard for now
        else if (Check(TokenKind.Lt))
        {
            Advance(); // <
            int depth = 1;
            while (!Check(TokenKind.Eof) && depth > 0)
            {
                if (Check(TokenKind.Lt))  depth++;
                if (Check(TokenKind.Gt))  depth--;
                Advance();
            }
        }

        // T? nullable
        if (Match(TokenKind.Question))
            ty = new OptionType(ty, span);

        return ty;
    }

    // ── Block & statements ────────────────────────────────────────────────────

    private BlockStmt ParseBlock()
    {
        var span = Current.Span;
        Expect(TokenKind.LBrace);
        var stmts = new List<Stmt>();
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
            stmts.Add(ParseStmt());
        Expect(TokenKind.RBrace);
        return new BlockStmt(stmts, span);
    }

    private Stmt ParseStmt()
    {
        var span = Current.Span;

        // var decl
        if (Check(TokenKind.Var))
        {
            Advance();
            var name = Expect(TokenKind.Identifier).Text;
            Expr? init = null;
            if (Match(TokenKind.Eq)) init = ParseExpr();
            Expect(TokenKind.Semicolon);
            return new VarDeclStmt(name, null, init, span);
        }

        // Type-annotated decl: string msg = ... or string name; (no assignment)
        // Detect: <TypeToken> <Identifier> followed by '=' or ';'
        if (IsTypeToken(Current.Kind) && Peek().Kind == TokenKind.Identifier
            && (Peek(2).Kind is TokenKind.Eq or TokenKind.Semicolon))
        {
            var typeAnn = ParseTypeExpr();
            var vname   = Expect(TokenKind.Identifier).Text;
            Expr? init  = null;
            if (Match(TokenKind.Eq)) init = ParseExpr();
            Expect(TokenKind.Semicolon);
            return new VarDeclStmt(vname, typeAnn, init, span);
        }

        // return
        if (Check(TokenKind.Return))
        {
            Advance();
            Expr? val = Check(TokenKind.Semicolon) ? null : ParseExpr();
            Expect(TokenKind.Semicolon);
            return new ReturnStmt(val, span);
        }

        // if
        if (Check(TokenKind.If)) return ParseIf();

        // while
        if (Check(TokenKind.While))
        {
            Advance();
            Expect(TokenKind.LParen);
            var cond = ParseExpr();
            Expect(TokenKind.RParen);
            var body = ParseBlock();
            return new WhileStmt(cond, body, span);
        }

        // for
        if (Check(TokenKind.For))
        {
            Advance();
            Expect(TokenKind.LParen);
            Stmt? init = Check(TokenKind.Semicolon) ? null : ParseStmt();
            Expr? cond = Check(TokenKind.Semicolon) ? null : ParseExpr();
            Match(TokenKind.Semicolon);
            Expr? incr = Check(TokenKind.RParen) ? null : ParseExpr();
            Expect(TokenKind.RParen);
            var body = ParseBlock();
            return new ForStmt(init, cond, incr, body, span);
        }

        // foreach
        if (Check(TokenKind.Foreach))
        {
            Advance();
            Expect(TokenKind.LParen);
            Match(TokenKind.Var); // optional var
            var vname = Expect(TokenKind.Identifier).Text;
            Expect(TokenKind.In);
            var collection = ParseExpr();
            Expect(TokenKind.RParen);
            var body = ParseBlock();
            return new ForeachStmt(vname, collection, body, span);
        }

        // block
        if (Check(TokenKind.LBrace)) return ParseBlock();

        // Expression statement
        var expr = ParseExpr();
        Match(TokenKind.Semicolon);
        return new ExprStmt(expr, span);
    }

    private IfStmt ParseIf()
    {
        var span = Current.Span;
        Expect(TokenKind.If);
        Expect(TokenKind.LParen);
        var cond = ParseExpr();
        Expect(TokenKind.RParen);
        var then = ParseBlock();
        Stmt? else_ = null;
        if (Match(TokenKind.Else))
            else_ = Check(TokenKind.If) ? ParseIf() : ParseBlock();
        return new IfStmt(cond, then, else_, span);
    }

    // ── Expressions ───────────────────────────────────────────────────────────

    public Expr ParseExpr() => ParseAssign();

    private Expr ParseAssign()
    {
        var left = ParseTernary();
        if (Current.Kind is TokenKind.Eq or TokenKind.PlusEq or TokenKind.MinusEq
            or TokenKind.StarEq or TokenKind.SlashEq or TokenKind.PercentEq)
        {
            var op   = Advance().Text;
            var right = ParseAssign();
            if (op != "=")
            {
                // desugar: left += right → left = left + right
                var binOp = op[..^1];
                right = new BinaryExpr(binOp, left, right, left.Span);
            }
            return new AssignExpr(left, right, left.Span);
        }
        return left;
    }

    private Expr ParseTernary()
    {
        var expr = ParseOr();
        if (Match(TokenKind.Question))
        {
            var then = ParseExpr();
            Expect(TokenKind.Colon);
            var else_ = ParseExpr();
            return new ConditionalExpr(expr, then, else_, expr.Span);
        }
        return expr;
    }

    private Expr ParseOr()
    {
        var left = ParseAnd();
        while (Check(TokenKind.PipePipe))
        {
            var op = Advance().Text;
            left = new BinaryExpr(op, left, ParseAnd(), left.Span);
        }
        return left;
    }

    private Expr ParseAnd()
    {
        var left = ParseEquality();
        while (Check(TokenKind.AmpAmp))
        {
            var op = Advance().Text;
            left = new BinaryExpr(op, left, ParseEquality(), left.Span);
        }
        return left;
    }

    private Expr ParseEquality()
    {
        var left = ParseRelational();
        while (Check(TokenKind.EqEq) || Check(TokenKind.BangEq))
        {
            var op = Advance().Text;
            left = new BinaryExpr(op, left, ParseRelational(), left.Span);
        }
        return left;
    }

    private Expr ParseRelational()
    {
        var left = ParseAddSub();
        while (Current.Kind is TokenKind.Lt or TokenKind.LtEq or TokenKind.Gt or TokenKind.GtEq
                             or TokenKind.Is or TokenKind.As)
        {
            var op = Advance().Text;
            left = new BinaryExpr(op, left, ParseAddSub(), left.Span);
        }
        return left;
    }

    private Expr ParseAddSub()
    {
        var left = ParseMulDiv();
        while (Check(TokenKind.Plus) || Check(TokenKind.Minus))
        {
            var op = Advance().Text;
            left = new BinaryExpr(op, left, ParseMulDiv(), left.Span);
        }
        return left;
    }

    private Expr ParseMulDiv()
    {
        var left = ParseUnary();
        while (Current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
        {
            var op = Advance().Text;
            left = new BinaryExpr(op, left, ParseUnary(), left.Span);
        }
        return left;
    }

    private Expr ParseUnary()
    {
        var span = Current.Span;
        if (Check(TokenKind.Bang) || Check(TokenKind.Minus) || Check(TokenKind.Tilde))
            return new UnaryExpr(Advance().Text, ParseUnary(), span);
        if (Check(TokenKind.PlusPlus) || Check(TokenKind.MinusMinus))
            return new UnaryExpr(Advance().Text, ParsePostfix(), span);
        if (Check(TokenKind.Await))
        {
            Advance();
            return new UnaryExpr("await", ParseUnary(), span);
        }
        // Cast: (Type)expr  — only if ( TypeToken ) follows
        if (Check(TokenKind.LParen) && IsTypeToken(Peek().Kind) && Peek(2).Kind == TokenKind.RParen)
        {
            Advance(); // (
            var ty = ParseTypeExpr();
            Expect(TokenKind.RParen);
            return new CastExpr(ty, ParseUnary(), span);
        }
        return ParsePostfix();
    }

    private Expr ParsePostfix()
    {
        var expr = ParsePrimary();
        while (true)
        {
            if (Match(TokenKind.LParen))
            {
                var args = new List<Expr>();
                while (!Check(TokenKind.RParen) && !Check(TokenKind.Eof))
                {
                    args.Add(ParseExpr());
                    if (!Match(TokenKind.Comma)) break;
                }
                Expect(TokenKind.RParen);
                expr = new CallExpr(expr, args, expr.Span);
            }
            else if (Match(TokenKind.Dot))
            {
                var member = Expect(TokenKind.Identifier).Text;
                expr = new MemberExpr(expr, member, expr.Span);
            }
            else if (Match(TokenKind.LBracket))
            {
                var index = ParseExpr();
                Expect(TokenKind.RBracket);
                expr = new IndexExpr(expr, index, expr.Span);
            }
            else if (Check(TokenKind.PlusPlus) || Check(TokenKind.MinusMinus))
            {
                expr = new PostfixExpr(Advance().Text, expr, expr.Span);
            }
            else if (Check(TokenKind.Question) && Peek().Kind == TokenKind.Dot)
            {
                // null-conditional: a?.b  — just lex as member access for now
                Advance(); Advance(); // ?  .
                var member = Expect(TokenKind.Identifier).Text;
                expr = new MemberExpr(expr, "?." + member, expr.Span);
            }
            else break;
        }
        return expr;
    }

    private Expr ParsePrimary()
    {
        var span = Current.Span;
        switch (Current.Kind)
        {
            case TokenKind.IntLiteral:
            {
                var text = Advance().Text.Replace("_", "").TrimEnd('L', 'l', 'u', 'U');
                return new LitIntExpr(long.Parse(text), span);
            }
            case TokenKind.FloatLiteral:
            {
                var text = Advance().Text.Replace("_", "").TrimEnd('f', 'F', 'd', 'D', 'm', 'M');
                return new LitFloatExpr(double.Parse(text, System.Globalization.CultureInfo.InvariantCulture), span);
            }
            case TokenKind.StringLiteral:
            {
                var raw = Advance().Text;
                return new LitStrExpr(raw[1..^1], span); // strip outer quotes
            }
            case TokenKind.InterpolatedStringLiteral:
                return ParseInterpolatedString(span);
            case TokenKind.True:
                Advance(); return new LitBoolExpr(true, span);
            case TokenKind.False:
                Advance(); return new LitBoolExpr(false, span);
            case TokenKind.Null:
                Advance(); return new LitNullExpr(span);
            case TokenKind.CharLiteral:
            {
                var raw = Advance().Text;
                char c = raw.Length >= 3 ? raw[1] : '\0';
                return new LitCharExpr(c, span);
            }
            case TokenKind.Identifier:
                return new IdentExpr(Advance().Text, span);
            case TokenKind.New:
            {
                Advance();
                var ty = ParseTypeExpr();
                Expect(TokenKind.LParen);
                var args = new List<Expr>();
                while (!Check(TokenKind.RParen) && !Check(TokenKind.Eof))
                {
                    args.Add(ParseExpr());
                    if (!Match(TokenKind.Comma)) break;
                }
                Expect(TokenKind.RParen);
                return new NewExpr(ty, args, span);
            }
            case TokenKind.LParen:
            {
                Advance();
                var inner = ParseExpr();
                Expect(TokenKind.RParen);
                return inner;
            }
            // switch expression: expr switch { ... }  — parsed as postfix in ParsePostfix above
            // Handle lambda: (x, y) => body or x => body
            default:
                throw new ParseException(
                    $"unexpected token `{Current.Text}` in expression", span);
        }
    }

    // ── Interpolated string ───────────────────────────────────────────────────

    private InterpolatedStrExpr ParseInterpolatedString(Span span)
    {
        var raw = Advance().Text;      // e.g.  $"Hello, {name}!"
        // Strip leading $" and trailing "
        var body = raw.StartsWith("$\"") ? raw[2..^1] : raw[1..^1];

        var parts = new List<InterpolationPart>();
        var sb    = new StringBuilder();
        int i     = 0;

        while (i < body.Length)
        {
            if (body[i] == '{')
            {
                if (sb.Length > 0) { parts.Add(new TextPart(sb.ToString(), span)); sb.Clear(); }
                i++; // consume {
                int depth = 1;
                var exprSrc = new StringBuilder();
                while (i < body.Length && depth > 0)
                {
                    if (body[i] == '{') depth++;
                    else if (body[i] == '}') { depth--; if (depth == 0) { i++; break; } }
                    if (depth > 0) exprSrc.Append(body[i]);
                    i++;
                }
                // Re-lex and re-parse the inner expression
                var innerTokens = new Z42.Compiler.Lexer.Lexer(exprSrc.ToString()).Tokenize();
                var innerParser = new Parser(innerTokens);
                var innerExpr   = innerParser.ParseExpr();
                parts.Add(new ExprPart(innerExpr, span));
            }
            else
            {
                sb.Append(body[i++]);
            }
        }
        if (sb.Length > 0) parts.Add(new TextPart(sb.ToString(), span));
        return new InterpolatedStrExpr(parts, span);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsTypeToken(TokenKind k) => k is
        TokenKind.Void   or TokenKind.String or TokenKind.Int    or TokenKind.Long  or
        TokenKind.Short  or TokenKind.Double or TokenKind.Float  or TokenKind.Byte  or
        TokenKind.Uint   or TokenKind.Ulong  or TokenKind.Ushort or TokenKind.Sbyte or
        TokenKind.Object or TokenKind.Bool   or TokenKind.Char   or
        TokenKind.I8 or TokenKind.I16 or TokenKind.I32 or TokenKind.I64 or
        TokenKind.U8 or TokenKind.U16 or TokenKind.U32 or TokenKind.U64 or
        TokenKind.F32 or TokenKind.F64 or TokenKind.Identifier;

    private static string KindName(TokenKind k) => k.ToString().ToLower();
}

public sealed class ParseException(string message, Span span) : Exception(message)
{
    public Span Span { get; } = span;
}
