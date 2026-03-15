using Z42.Compiler.Lexer;

namespace Z42.Compiler.Parser;

/// <summary>
/// Recursive-descent parser for z42.
/// Produces a typed AST from a token list.
/// </summary>
public sealed class Parser(List<Token> tokens)
{
    private int _pos;

    private Token Current => _pos < tokens.Count ? tokens[_pos] : Token.Eof(0);
    private Token Peek(int offset = 1) => _pos + offset < tokens.Count ? tokens[_pos + offset] : Token.Eof(0);

    private Token Advance() => tokens[_pos < tokens.Count ? _pos++ : _pos];

    private Token Expect(TokenKind kind)
    {
        if (Current.Kind != kind)
            throw new ParseException($"Expected {kind} but got {Current.Kind} ({Current.Text})", Current.Span);
        return Advance();
    }

    private bool Check(TokenKind kind) => Current.Kind == kind;
    private bool Match(TokenKind kind) { if (Check(kind)) { Advance(); return true; } return false; }

    // ── Module ────────────────────────────────────────────────────────────────

    public Module ParseModule()
    {
        var start = Current.Span;
        string moduleName = "main";
        if (Match(TokenKind.Module))
        {
            moduleName = Expect(TokenKind.Identifier).Text;
        }

        var items = new List<Item>();
        while (!Check(TokenKind.Eof))
        {
            items.Add(ParseItem());
        }

        return new Module(moduleName, items, start);
    }

    // ── Items ─────────────────────────────────────────────────────────────────

    private Item ParseItem()
    {
        return Current.Kind switch
        {
            TokenKind.Fn     => ParseFunction(),
            TokenKind.Struct => ParseStruct(),
            TokenKind.Enum   => ParseEnum(),
            TokenKind.Trait  => ParseTrait(),
            TokenKind.Impl   => ParseImpl(),
            TokenKind.Use    => ParseUse(),
            _ => throw new ParseException($"Unexpected token at top level: {Current}", Current.Span)
        };
    }

    private FunctionItem ParseFunction()
    {
        var start = Current.Span;
        Expect(TokenKind.Fn);
        var name = Expect(TokenKind.Identifier).Text;

        Expect(TokenKind.LParen);
        var @params = new List<Param>();
        while (!Check(TokenKind.RParen) && !Check(TokenKind.Eof))
        {
            var pStart = Current.Span;
            var pName = Expect(TokenKind.Identifier).Text;
            Expect(TokenKind.Colon);
            var pType = ParseTypeExpr();
            @params.Add(new Param(pName, pType, pStart));
            if (!Match(TokenKind.Comma)) break;
        }
        Expect(TokenKind.RParen);

        TypeExpr returnType = new VoidType(Current.Span);
        if (Match(TokenKind.Arrow))
            returnType = ParseTypeExpr();

        var body = ParseBlock();
        return new FunctionItem(name, @params, returnType, body, start);
    }

    private StructItem ParseStruct()
    {
        var start = Current.Span;
        Expect(TokenKind.Struct);
        var name = Expect(TokenKind.Identifier).Text;
        Expect(TokenKind.LBrace);
        var fields = new List<FieldDef>();
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            var fStart = Current.Span;
            var fName = Expect(TokenKind.Identifier).Text;
            Expect(TokenKind.Colon);
            var fType = ParseTypeExpr();
            fields.Add(new FieldDef(fName, fType, fStart));
            Match(TokenKind.Comma);
        }
        Expect(TokenKind.RBrace);
        return new StructItem(name, fields, start);
    }

    private EnumItem ParseEnum()
    {
        var start = Current.Span;
        Expect(TokenKind.Enum);
        var name = Expect(TokenKind.Identifier).Text;
        Expect(TokenKind.LBrace);
        var variants = new List<VariantDef>();
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            var vStart = Current.Span;
            var vName = Expect(TokenKind.Identifier).Text;
            var payload = new List<TypeExpr>();
            if (Match(TokenKind.LParen))
            {
                while (!Check(TokenKind.RParen) && !Check(TokenKind.Eof))
                {
                    payload.Add(ParseTypeExpr());
                    if (!Match(TokenKind.Comma)) break;
                }
                Expect(TokenKind.RParen);
            }
            variants.Add(new VariantDef(vName, payload, vStart));
            Match(TokenKind.Comma);
        }
        Expect(TokenKind.RBrace);
        return new EnumItem(name, variants, start);
    }

    private TraitItem ParseTrait()
    {
        var start = Current.Span;
        Expect(TokenKind.Trait);
        var name = Expect(TokenKind.Identifier).Text;
        Expect(TokenKind.LBrace);
        var methods = new List<FunctionItem>();
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
            methods.Add(ParseFunction());
        Expect(TokenKind.RBrace);
        return new TraitItem(name, methods, start);
    }

    private ImplItem ParseImpl()
    {
        var start = Current.Span;
        Expect(TokenKind.Impl);
        string? traitName = null;
        var target = ParseTypeExpr();
        if (Match(TokenKind.For))
        {
            traitName = (target as NamedType)?.Name;
            target = ParseTypeExpr();
        }
        Expect(TokenKind.LBrace);
        var methods = new List<FunctionItem>();
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
            methods.Add(ParseFunction());
        Expect(TokenKind.RBrace);
        return new ImplItem(target, traitName, methods, start);
    }

    private UseItem ParseUse()
    {
        var start = Current.Span;
        Expect(TokenKind.Use);
        var parts = new List<string> { Expect(TokenKind.Identifier).Text };
        while (Match(TokenKind.Dot))
            parts.Add(Expect(TokenKind.Identifier).Text);
        return new UseItem(string.Join(".", parts), start);
    }

    // ── Types ─────────────────────────────────────────────────────────────────

    private TypeExpr ParseTypeExpr()
    {
        var start = Current.Span;

        if (Match(TokenKind.Ampersand))
        {
            bool isMut = Match(TokenKind.Mut);
            var inner = ParseTypeExpr();
            return new RefType(inner, isMut, start);
        }

        var name = Current.Kind switch
        {
            TokenKind.I8    or TokenKind.I16   or TokenKind.I32 or TokenKind.I64 or
            TokenKind.U8    or TokenKind.U16   or TokenKind.U32 or TokenKind.U64 or
            TokenKind.F32   or TokenKind.F64   or TokenKind.Bool or TokenKind.Char or
            TokenKind.Str   or TokenKind.Void  or TokenKind.Identifier => Advance().Text,
            _ => throw new ParseException($"Expected type, got {Current}", Current.Span)
        };

        TypeExpr ty = new NamedType(name, start);

        if (Match(TokenKind.Question)) ty = new OptionType(ty, start);
        else if (Match(TokenKind.Bang)) ty = new ResultType(ty, start);

        return ty;
    }

    // ── Blocks & Statements ───────────────────────────────────────────────────

    private BlockExpr ParseBlock()
    {
        var start = Current.Span;
        Expect(TokenKind.LBrace);
        var stmts = new List<Stmt>();
        Expr? tail = null;

        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            var stmt = ParseStmt(out bool isTailExpr);
            if (isTailExpr && Check(TokenKind.RBrace))
            {
                tail = ((ExprStmt)stmt).Expr;
                break;
            }
            stmts.Add(stmt);
        }

        Expect(TokenKind.RBrace);
        return new BlockExpr(stmts, tail, start);
    }

    private Stmt ParseStmt(out bool isTailExpr)
    {
        isTailExpr = false;
        var start = Current.Span;

        if (Check(TokenKind.Let))
        {
            Advance();
            bool isMut = Match(TokenKind.Mut);
            var name = Expect(TokenKind.Identifier).Text;
            TypeExpr? annotation = null;
            if (Match(TokenKind.Colon)) annotation = ParseTypeExpr();
            Expr? init = null;
            if (Match(TokenKind.Eq)) init = ParseExpr();
            return new LetStmt(name, isMut, annotation, init, start);
        }

        if (Check(TokenKind.Return))
        {
            Advance();
            Expr? val = Check(TokenKind.RBrace) ? null : ParseExpr();
            return new ReturnStmt(val, start);
        }

        var expr = ParseExpr();
        if (!Match(TokenKind.Semicolon)) isTailExpr = true;
        return new ExprStmt(expr, start);
    }

    // ── Expressions ───────────────────────────────────────────────────────────

    private Expr ParseExpr() => ParseAssignment();

    private Expr ParseAssignment()
    {
        // TODO: assignment expressions
        return ParseOr();
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
        var left = ParseComparison();
        while (Check(TokenKind.EqEq) || Check(TokenKind.BangEq))
        {
            var op = Advance().Text;
            left = new BinaryExpr(op, left, ParseComparison(), left.Span);
        }
        return left;
    }

    private Expr ParseComparison()
    {
        var left = ParseAddSub();
        while (Check(TokenKind.Lt) || Check(TokenKind.LtEq) || Check(TokenKind.Gt) || Check(TokenKind.GtEq))
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
        while (Check(TokenKind.Star) || Check(TokenKind.Slash) || Check(TokenKind.Percent))
        {
            var op = Advance().Text;
            left = new BinaryExpr(op, left, ParseUnary(), left.Span);
        }
        return left;
    }

    private Expr ParseUnary()
    {
        if (Check(TokenKind.Bang) || Check(TokenKind.Minus))
        {
            var op = Advance().Text;
            return new UnaryExpr(op, ParseUnary(), tokens[_pos - 1].Span);
        }
        if (Check(TokenKind.Await))
        {
            var span = Advance().Span;
            return new AwaitExpr(ParsePostfix(), span);
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
                var field = Expect(TokenKind.Identifier).Text;
                expr = new FieldExpr(expr, field, expr.Span);
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
                return new LitIntExpr(long.Parse(Advance().Text), span);
            case TokenKind.FloatLiteral:
                return new LitFloatExpr(double.Parse(Advance().Text), span);
            case TokenKind.StringLiteral:
                var raw = Advance().Text;
                return new LitStrExpr(raw[1..^1], span); // strip quotes
            case TokenKind.True:
                Advance(); return new LitBoolExpr(true, span);
            case TokenKind.False:
                Advance(); return new LitBoolExpr(false, span);
            case TokenKind.None:
                Advance(); return new NoneExpr(span);
            case TokenKind.Identifier:
                return new IdentExpr(Advance().Text, span);
            case TokenKind.LParen:
                Advance();
                var inner = ParseExpr();
                Expect(TokenKind.RParen);
                return inner;
            case TokenKind.LBrace:
                return ParseBlock();
            case TokenKind.If:
                return ParseIf();
            case TokenKind.Match:
                return ParseMatch();
            default:
                throw new ParseException($"Unexpected token in expression: {Current}", Current.Span);
        }
    }

    private IfExpr ParseIf()
    {
        var span = Current.Span;
        Expect(TokenKind.If);
        var cond = ParseExpr();
        var then = ParseBlock();
        Expr? else_ = null;
        if (Match(TokenKind.Else))
            else_ = Check(TokenKind.If) ? ParseIf() : ParseBlock();
        return new IfExpr(cond, then, else_, span);
    }

    private MatchExpr ParseMatch()
    {
        var span = Current.Span;
        Expect(TokenKind.Match);
        var subject = ParseExpr();
        Expect(TokenKind.LBrace);
        var arms = new List<MatchArm>();
        while (!Check(TokenKind.RBrace) && !Check(TokenKind.Eof))
        {
            var aSpan = Current.Span;
            var pattern = ParsePattern();
            Expect(TokenKind.FatArrow);
            var body = ParseExpr();
            arms.Add(new MatchArm(pattern, body, aSpan));
            Match(TokenKind.Comma);
        }
        Expect(TokenKind.RBrace);
        return new MatchExpr(subject, arms, span);
    }

    private Pattern ParsePattern()
    {
        var span = Current.Span;
        if (Current.Kind == TokenKind.Identifier)
        {
            var name = Advance().Text;
            if (Check(TokenKind.LParen))
            {
                Advance();
                var fields = new List<Pattern>();
                while (!Check(TokenKind.RParen) && !Check(TokenKind.Eof))
                {
                    fields.Add(ParsePattern());
                    if (!Match(TokenKind.Comma)) break;
                }
                Expect(TokenKind.RParen);
                return new VariantPattern(name, fields, span);
            }
            return new IdentPattern(name, span);
        }
        if (Current.Kind == TokenKind.Underscore) { Advance(); return new WildcardPattern(span); }
        return new LitPattern(ParseExpr(), span);
    }
}

public sealed class ParseException(string message, Span span) : Exception(message)
{
    public Span Span { get; } = span;
}
