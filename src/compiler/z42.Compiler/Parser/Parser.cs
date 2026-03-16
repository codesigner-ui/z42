using Z42.Compiler.Features;
using Z42.Compiler.Lexer;

namespace Z42.Compiler.Parser;

/// <summary>
/// Public façade for the z42 Phase 1 parser.
/// All parsing logic lives in <see cref="ParserContext"/> (Pratt expression engine)
/// plus the static handler tables in <see cref="ParseTable"/>, <see cref="Nuds"/>,
/// <see cref="Leds"/>, and <see cref="Stmts"/>.
/// </summary>
public sealed class Parser
{
    private readonly ParserContext _ctx;

    public Parser(List<Token> tokens, LanguageFeatures? features = null)
    {
        _ctx = new ParserContext(tokens, features ?? LanguageFeatures.Phase1);
    }

    public CompilationUnit ParseCompilationUnit() => _ctx.ParseCompilationUnit();

    public Expr ParseExpr() => _ctx.ParseExpr();
}

public sealed class ParseException(string message, Span span) : Exception(message)
{
    public Span Span { get; } = span;
}
