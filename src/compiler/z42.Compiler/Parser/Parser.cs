using Z42.Compiler.Features;
using Z42.Compiler.Lexer;
using Z42.Compiler.Parser.Core;

namespace Z42.Compiler.Parser;

/// Public façade for the z42 Phase 1 parser.
///
/// Architecture (Superpower-style, hand-written for self-hosting):
///   TokenCursor  — immutable cursor; all navigation returns a new cursor
///   ParseResult<T> — success (value + remainder) or failure (error + span)
///   Parser<T>    — delegate TokenCursor → ParseResult<T>
///   P            — combinator library (Or, Many, SeparatedBy, Between, …)
///
/// Parse logic is split by concern:
///   TypeParser       — type expressions
///   ExprParser       — Pratt expression engine (NudTable / LedTable)
///   StmtParser       — statements and blocks
///   TopLevelParser   — compilation unit, class, function, enum, interface
public sealed class Parser
{
    private readonly List<Token>      _tokens;
    private readonly LanguageFeatures _feat;

    public Parser(List<Token> tokens, LanguageFeatures? features = null)
    {
        _tokens = tokens;
        _feat   = features ?? LanguageFeatures.Phase1;
    }

    public CompilationUnit ParseCompilationUnit()
    {
        var cursor = TokenCursor.From(_tokens);
        return TopLevelParser.ParseCompilationUnit(cursor, _feat);
    }

    public Expr ParseExpr()
    {
        var cursor = TokenCursor.From(_tokens);
        return ExprParser.Parse(cursor, _feat).OrThrow();
    }
}

/// Raised when the parser encounters an unrecoverable syntax error.
/// Carries the source location (Span) for accurate error reporting.
public sealed class ParseException(string message, Span span) : Exception(message)
{
    public Span Span { get; } = span;
}
