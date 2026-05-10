using Z42.Core.Text;

namespace Z42.Core.Diagnostics;

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

    /// "feature '<name>' is not enabled"
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

    /// Output format. Defaults to <see cref="DiagnosticOutputFormat.Plain"/>
    /// (legacy MSBuild style) for golden-test stability; CLI driver flips to
    /// <see cref="DiagnosticOutputFormat.Pretty"/> when stderr is a TTY.
    public static DiagnosticOutputFormat DefaultFormat { get; set; } = DiagnosticOutputFormat.Plain;

    /// Whether to emit ANSI color codes in Pretty mode. Set by the CLI driver
    /// based on TTY detection / NO_COLOR env / explicit user opt-out.
    public static bool DefaultUseColor { get; set; } = false;

    /// Print all diagnostics to stderr (sorted by file then line).
    /// Pretty mode reads each unique source file once and renders with caret
    /// + context; Plain mode keeps the legacy single-line format.
    public void PrintAll(TextWriter? writer = null)
        => PrintAll(writer, DefaultFormat, DefaultUseColor);

    public void PrintAll(TextWriter? writer, DiagnosticOutputFormat format, bool useColor)
    {
        writer ??= Console.Error;

        // Cache file → source text so repeated diagnostics in the same file
        // pay one I/O hit. Null entry caches "tried and failed".
        var fileCache = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var d in _items.OrderBy(d => d.Span.File).ThenBy(d => d.Span.Line))
        {
            string? sourceText = null;
            if (format == DiagnosticOutputFormat.Pretty && !string.IsNullOrEmpty(d.Span.File))
            {
                if (!fileCache.TryGetValue(d.Span.File, out sourceText))
                {
                    sourceText = TryReadSource(d.Span.File);
                    fileCache[d.Span.File] = sourceText;
                }
            }
            writer.WriteLine(DiagnosticRenderer.Render(d, sourceText, format, useColor));
        }
    }

    private static string? TryReadSource(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
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
