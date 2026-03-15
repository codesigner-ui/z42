using Z42.Compiler.Lexer;

namespace Z42.Compiler.Diagnostics;

/// <summary>
/// Collects diagnostics produced during compilation.
/// Allows reporting multiple errors before aborting.
/// </summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = new();

    public IReadOnlyList<Diagnostic> All      => _items;
    public bool HasErrors   => _items.Any(d => d.IsError);
    public bool HasWarnings => _items.Any(d => d.IsWarning);
    public int  ErrorCount  => _items.Count(d => d.IsError);

    // ── Add ────────────────────────────────────────────────────────────────

    public void Add(Diagnostic d) => _items.Add(d);

    public void Error(string code, string message, Span span) =>
        Add(Diagnostic.Error(code, message, span));

    public void Warning(string code, string message, Span span) =>
        Add(Diagnostic.Warning(code, message, span));

    public void Info(string code, string message, Span span) =>
        Add(Diagnostic.Info(code, message, span));

    // ── Common error helpers ───────────────────────────────────────────────

    /// "expected <expected> but got <got> ('<text>')"
    public void Expected(string expected, Token got) =>
        Error(DiagnosticCodes.ExpectedToken,
              $"expected {expected} but got {got.Kind} ('{got.Text}')",
              got.Span);

    /// "unexpected token <tok>"
    public void Unexpected(Token tok) =>
        Error(DiagnosticCodes.UnexpectedToken,
              $"unexpected token {tok.Kind} ('{tok.Text}')",
              tok.Span);

    /// "feature '<name>' is not enabled (use --features <name> or set LanguageFeatures.<prop> = true)"
    public void FeatureDisabled(string featureName, Span span) =>
        Error(DiagnosticCodes.FeatureDisabled,
              $"language feature '{featureName}' is not enabled. " +
              $"Add it to LanguageFeatures or use a pre-built profile.",
              span);

    /// "undefined symbol '<name>'"
    public void UndefinedSymbol(string name, Span span) =>
        Error(DiagnosticCodes.UndefinedSymbol,
              $"undefined symbol '{name}'",
              span);

    // ── Output ────────────────────────────────────────────────────────────

    /// Print all diagnostics to stderr; return true if there are errors.
    public bool PrintAll(TextWriter? writer = null)
    {
        writer ??= Console.Error;
        foreach (var d in _items.OrderBy(d => d.Span.File).ThenBy(d => d.Span.Line))
            writer.WriteLine(d);
        return HasErrors;
    }

    /// Throw a <see cref="CompilationException"/> if there are any errors.
    public void ThrowIfErrors()
    {
        if (HasErrors)
            throw new CompilationException(this);
    }
}

/// <summary>
/// Thrown when compilation fails with one or more errors.
/// Carries the full DiagnosticBag for structured error handling.
/// </summary>
public sealed class CompilationException(DiagnosticBag bag) : Exception(
    $"Compilation failed with {bag.ErrorCount} error(s):\n" +
    string.Join("\n", bag.All.Where(d => d.IsError)))
{
    public DiagnosticBag Bag { get; } = bag;
}
