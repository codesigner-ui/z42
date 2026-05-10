using FluentAssertions;
using Xunit;
using Z42.Core.Diagnostics;
using Z42.Core.Text;

namespace Z42.Tests;

public sealed class DiagnosticRendererTests
{
    // ── Plain mode preserves legacy MSBuild format ──────────────────────────

    [Fact]
    public void Plain_emits_legacy_msbuild_single_line()
    {
        var d = Diagnostic.Error("E0402", "type mismatch: expected int, got string",
            new Span(0, 7, 3, 13, "src/foo.z42"));

        var text = DiagnosticRenderer.Render(d, sourceText: null,
            DiagnosticOutputFormat.Plain, useColor: false);

        text.Should().Be("src/foo.z42(3,13): error E0402: type mismatch: expected int, got string");
    }

    [Fact]
    public void Plain_ignores_source_text_and_color()
    {
        var d = Diagnostic.Warning("W0603", "reserved namespace",
            new Span(0, 5, 1, 11, "ws.toml"));

        // Even with source / color requested, Plain stays plain.
        var text = DiagnosticRenderer.Render(d, sourceText: "namespace Std.Acme;",
            DiagnosticOutputFormat.Plain, useColor: true);

        text.Should().NotContain("\u001b[");      // no ANSI
        text.Should().NotContain("-->");           // no Pretty header
        text.Should().StartWith("ws.toml(1,11): warning W0603:");
    }

    // ── Pretty mode header + position ───────────────────────────────────────

    [Fact]
    public void Pretty_header_uses_catalog_title_when_available()
    {
        // E0402 is in DiagnosticCatalog → header shows the catalog Title,
        // and the original message becomes the caret-line annotation.
        var d = Diagnostic.Error("E0402", "expected int, got string",
            new Span(8, 15, 3, 9, "x.z42"));

        var text = DiagnosticRenderer.Render(d,
            sourceText: "int x;\nint y;\nint z = \"hi\";\n",
            DiagnosticOutputFormat.Pretty, useColor: false);

        text.Should().StartWith("error[E0402]: Type mismatch");
        text.Should().Contain("--> x.z42:3:9");
    }

    [Fact]
    public void Pretty_falls_back_to_message_when_catalog_misses()
    {
        var d = Diagnostic.Error("E9999", "unknown error",
            new Span(0, 1, 1, 1, "foo.z42"));

        var text = DiagnosticRenderer.Render(d, sourceText: null,
            DiagnosticOutputFormat.Pretty, useColor: false);

        text.Should().StartWith("error[E9999]: unknown error");
    }

    // ── Pretty mode source block + caret ────────────────────────────────────

    [Fact]
    public void Pretty_renders_source_line_and_caret()
    {
        var src = "int a = 1;\nint b = 2;\nint x = bad;\nint c = 3;\n";
        // Span pointing at "bad" on line 3, column 9 (1-based), 3 chars long.
        var d = Diagnostic.Error("E0401", "undefined symbol 'bad'",
            new Span(Start: 30, End: 33, Line: 3, Column: 9, File: "x.z42"));

        var text = DiagnosticRenderer.Render(d, sourceText: src,
            DiagnosticOutputFormat.Pretty, useColor: false);

        // Source line shown with gutter "  3 |"
        text.Should().Contain("  3 | int x = bad;");
        // Caret line has at least one ^ and the message
        text.Should().Contain("^");
        text.Should().Contain("undefined symbol 'bad'");
    }

    [Fact]
    public void Pretty_caret_alignment_under_column()
    {
        var src = "void f() { return 42; }\n";
        // Span on column 12 (the 'r' of 'return'), len 6
        var d = Diagnostic.Error("E0501", "test",
            new Span(Start: 11, End: 17, Line: 1, Column: 12, File: "f.z42"));

        var text = DiagnosticRenderer.Render(d, sourceText: src,
            DiagnosticOutputFormat.Pretty, useColor: false);

        // Find the caret line (the one with ^)
        var caretLine = text.Split('\n').First(line => line.Contains('^'));
        // caret position must be under column 12 — count leading whitespace + gutter "      | "
        // Gutter prefix: "      | " is 8 chars; then column 12 means 11 leading spaces.
        int caretIdx = caretLine.IndexOf('^');
        caretIdx.Should().BeGreaterThanOrEqualTo(8 + 11);  // gutter + column-1 spaces
    }

    [Fact]
    public void Pretty_handles_missing_source_with_note_fallback()
    {
        var d = Diagnostic.Error("E0401", "undefined symbol 'x'",
            new Span(0, 1, 5, 3, "missing.z42"));

        var text = DiagnosticRenderer.Render(d, sourceText: null,
            DiagnosticOutputFormat.Pretty, useColor: false);

        text.Should().Contain("--> missing.z42:5:3");
        text.Should().Contain("note: undefined symbol 'x'");
    }

    // ── Color toggle ────────────────────────────────────────────────────────

    [Fact]
    public void Pretty_emits_no_ansi_when_useColor_false()
    {
        var d = Diagnostic.Error("E0401", "undefined symbol 'y'",
            new Span(0, 1, 1, 1, "f.z42"));

        var text = DiagnosticRenderer.Render(d, sourceText: "int x = y;\n",
            DiagnosticOutputFormat.Pretty, useColor: false);

        text.Should().NotContain("\u001b[");
    }

    [Fact]
    public void Pretty_emits_ansi_when_useColor_true()
    {
        var d = Diagnostic.Error("E0401", "undefined symbol 'y'",
            new Span(0, 1, 1, 1, "f.z42"));

        var text = DiagnosticRenderer.Render(d, sourceText: "int x = y;\n",
            DiagnosticOutputFormat.Pretty, useColor: true);

        text.Should().Contain("\u001b[31m");   // red for error
        text.Should().Contain("\u001b[0m");    // reset
    }

    // ── Severity ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(DiagnosticSeverity.Error,   "error")]
    [InlineData(DiagnosticSeverity.Warning, "warning")]
    [InlineData(DiagnosticSeverity.Info,    "info")]
    public void Pretty_severity_word_in_header(DiagnosticSeverity sev, string expected)
    {
        var d = new Diagnostic(sev, "X0000", "msg", new Span(0, 1, 1, 1, "f"));

        var text = DiagnosticRenderer.Render(d, sourceText: null,
            DiagnosticOutputFormat.Pretty, useColor: false);

        text.Should().StartWith($"{expected}[X0000]");
    }
}
