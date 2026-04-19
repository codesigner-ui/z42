using Z42.Core.Text;
using Z42.Core.Features;
using Z42.Core.Diagnostics;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser.Core;

namespace Z42.Syntax.Parser;

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
///
/// Error recovery: the parser accumulates syntax errors into a DiagnosticBag
/// and continues parsing. Statement and top-level declaration boundaries serve
/// as recovery points. ErrorExpr/ErrorStmt placeholder nodes are inserted where
/// parsing failed. Callers should check Diagnostics.HasErrors after parsing.
public sealed class Parser
{
    private readonly List<Token>      _tokens;
    private readonly LanguageFeatures _feat;
    private readonly DiagnosticBag    _diags = new();

    /// Syntax errors accumulated during parsing.
    /// Check HasErrors after ParseCompilationUnit() instead of catching ParseException.
    public DiagnosticBag Diagnostics => _diags;

    public Parser(List<Token> tokens, LanguageFeatures? features = null)
    {
        _tokens = tokens;
        _feat   = features ?? LanguageFeatures.Phase1;
    }

    public CompilationUnit ParseCompilationUnit()
    {
        var cursor = TokenCursor.From(_tokens);
        return TopLevelParser.ParseCompilationUnit(cursor, _feat, _diags);
    }

    public Expr ParseExpr()
    {
        var cursor = TokenCursor.From(_tokens);
        return ExprParser.Parse(cursor, _feat).OrThrow();
    }
}

/// Raised when the parser encounters an unrecoverable syntax error.
/// Carries the source location (Span) for accurate error reporting.
public sealed class ParseException(string message, Span span, string? code = null) : Exception(message)
{
    public Span    Span { get; } = span;
    public string? Code { get; } = code;
}
