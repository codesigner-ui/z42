using System.Text;
using Z42.Core.Text;

namespace Z42.Core.Diagnostics;

/// <summary>
/// Output formats for diagnostic rendering.
///
///   <see cref="Pretty"/> — rust/clang-style with source context, caret, and optional ANSI color.
///   <see cref="Plain"/>  — single-line MSBuild format `file(line,col): severity code: msg`,
///                          stable for IDE integration and golden-test comparisons.
/// </summary>
public enum DiagnosticOutputFormat { Pretty, Plain }

/// <summary>
/// Renders a <see cref="Diagnostic"/> to a human-readable string.
///
/// Pretty layout (rust/clang style):
/// <code>
/// error[E0402]: type mismatch
///   --> src/foo.z42:3:13
///    |
///  3 |     int x = "hello";
///    |             ^^^^^^^ &lt;message&gt;
///    |
/// </code>
///
/// Plain layout (legacy MSBuild style — unchanged from prior behavior):
/// <code>
/// src/foo.z42(3,13): error E0402: type mismatch ...
/// </code>
/// </summary>
public static class DiagnosticRenderer
{
    /// Render a single diagnostic. <paramref name="sourceText"/> may be null
    /// (or unavailable for the diagnostic's file) — Pretty mode then falls back
    /// to header + position only, no source context block.
    public static string Render(
        Diagnostic d,
        string?    sourceText,
        DiagnosticOutputFormat format,
        bool useColor)
    {
        return format switch
        {
            DiagnosticOutputFormat.Plain  => d.ToString(),
            DiagnosticOutputFormat.Pretty => RenderPretty(d, sourceText, useColor),
            _ => d.ToString(),
        };
    }

    // ── Pretty layout ────────────────────────────────────────────────────────

    private static string RenderPretty(Diagnostic d, string? sourceText, bool useColor)
    {
        var sb = new StringBuilder();

        // Header: `error[CODE]: title` (severity-colored)
        var sevColor = useColor ? AnsiColor(d.Severity) : "";
        var bold     = useColor ? Ansi.Bold : "";
        var reset    = useColor ? Ansi.Reset : "";
        var dim      = useColor ? Ansi.Dim : "";

        var title = ResolveTitle(d.Code) ?? d.Message;
        sb.Append(sevColor).Append(bold).Append(SeverityWord(d.Severity)).Append('[').Append(d.Code).Append(']').Append(reset);
        sb.Append(bold).Append(": ").Append(title).Append(reset).AppendLine();

        // Position: `  --> file:line:col`
        sb.Append("  ").Append(dim).Append("-->").Append(reset)
          .Append(' ').Append(d.Span.File).Append(':').Append(d.Span.Line).Append(':').Append(d.Span.Column).AppendLine();

        // Source context block — only when source text is available and span is in-range.
        if (sourceText != null && d.Span.Line > 0)
        {
            AppendSourceBlock(sb, sourceText, d.Span, d.Message, useColor, sevColor);
        }
        else
        {
            // No source context — still surface the message as a note line so
            // it's not lost (title may be a generic catalog string).
            sb.Append("  ").Append(dim).Append('=').Append(reset)
              .Append(' ').Append(bold).Append("note").Append(reset).Append(": ").Append(d.Message).AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendSourceBlock(
        StringBuilder sb, string source, Span span, string message, bool useColor, string sevColor)
    {
        var lines      = SplitLines(source);
        int lineIdx    = span.Line - 1;
        if (lineIdx < 0 || lineIdx >= lines.Length) return;

        string lineText  = lines[lineIdx];
        string gutterFmt = "  {0,3} ";
        string emptyGut  = "      ";   // 6 spaces (matches `  NNN ` shape)

        var dim   = useColor ? Ansi.Dim   : "";
        var bold  = useColor ? Ansi.Bold  : "";
        var reset = useColor ? Ansi.Reset : "";

        // Blank gutter row (rust style).
        sb.Append(emptyGut).Append(dim).Append('|').Append(reset).AppendLine();

        // Numbered source line.
        sb.AppendFormat(gutterFmt, span.Line).Append(dim).Append('|').Append(reset)
          .Append(' ').Append(lineText).AppendLine();

        // Caret/underline row. Span columns are 1-based.
        int col = Math.Max(1, span.Column);
        int len = ComputeUnderlineLen(span, lineText, col);

        sb.Append(emptyGut).Append(dim).Append('|').Append(reset).Append(' ');
        for (int i = 1; i < col; i++)
            sb.Append(i <= lineText.Length && lineText[i - 1] == '\t' ? '\t' : ' ');
        sb.Append(sevColor).Append(bold);
        for (int i = 0; i < len; i++) sb.Append('^');
        if (!string.IsNullOrWhiteSpace(message))
            sb.Append(' ').Append(message);
        sb.Append(reset).AppendLine();

        // Trailing blank gutter.
        sb.Append(emptyGut).Append(dim).Append('|').Append(reset).AppendLine();
    }

    private static int ComputeUnderlineLen(Span span, string lineText, int col)
    {
        // If span has byte range, prefer that (clamped to line end + minimum 1).
        // Span.End is exclusive byte offset; Span.Start is inclusive.
        int rangeLen = span.End > span.Start ? span.End - span.Start : 0;
        if (rangeLen <= 0) return 1;

        int remaining = Math.Max(0, lineText.Length - (col - 1));
        return Math.Min(Math.Max(rangeLen, 1), Math.Max(remaining, 1));
    }

    private static string[] SplitLines(string text)
    {
        // Preserve empty trailing line — Split with None, normalize CRLF.
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    // ── Title lookup (degrades to message when catalog has no entry) ────────

    private static string? ResolveTitle(string code) =>
        DiagnosticCatalog.TryGet(code)?.Title;

    private static string SeverityWord(DiagnosticSeverity sev) => sev switch
    {
        DiagnosticSeverity.Error   => "error",
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Info    => "info",
        _ => "diagnostic",
    };

    // ── ANSI helpers ────────────────────────────────────────────────────────

    private static string AnsiColor(DiagnosticSeverity sev) => sev switch
    {
        DiagnosticSeverity.Error   => Ansi.Red,
        DiagnosticSeverity.Warning => Ansi.Yellow,
        DiagnosticSeverity.Info    => Ansi.Blue,
        _ => "",
    };

    private static class Ansi
    {
        public const string Reset  = "\u001b[0m";
        public const string Bold   = "\u001b[1m";
        public const string Dim    = "\u001b[2m";
        public const string Red    = "\u001b[31m";
        public const string Yellow = "\u001b[33m";
        public const string Blue   = "\u001b[34m";
    }
}
