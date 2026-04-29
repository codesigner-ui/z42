using FluentAssertions;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// Spec C11a (`manifest-reader-import`) — Lexer + Parser.
/// Manifest-reader cases live in <see cref="NativeManifestReaderTests"/>.
public sealed class NativeImportParserTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static (CompilationUnit Cu, Z42.Core.Diagnostics.DiagnosticBag Diags) Parse(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var cu     = parser.ParseCompilationUnit();
        return (cu, parser.Diagnostics);
    }

    // ── Lexer ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Lexer_ImportKeyword_TokenizesAsImport()
    {
        var tokens = new Lexer("import Counter from \"numz42\";").Tokenize();
        tokens[0].Kind.Should().Be(TokenKind.Import);
        // `from` stays as a contextual identifier (not a keyword)
        tokens[2].Kind.Should().Be(TokenKind.Identifier);
        tokens[2].Text.Should().Be("from");
    }

    // ── Parser — happy paths ───────────────────────────────────────────────────

    [Fact]
    public void Parser_ImportFromString_BasicForm()
    {
        var (cu, diags) = Parse("import Counter from \"numz42\";");
        diags.HasErrors.Should().BeFalse(because: string.Join("\n", diags.All));

        cu.NativeImports.Should().NotBeNull();
        cu.NativeImports!.Should().ContainSingle();
        var imp = cu.NativeImports![0];
        imp.Name.Should().Be("Counter");
        imp.LibName.Should().Be("numz42");
    }

    [Fact]
    public void Parser_MultipleImports_OrderPreserved()
    {
        var src = """
                  import A from "lib1";
                  import B from "lib2";
                  """;
        var (cu, diags) = Parse(src);
        diags.HasErrors.Should().BeFalse(because: string.Join("\n", diags.All));

        cu.NativeImports.Should().NotBeNull();
        cu.NativeImports!.Select(i => (i.Name, i.LibName))
            .Should().Equal(("A", "lib1"), ("B", "lib2"));
    }

    [Fact]
    public void Parser_ImportCoexists_With_Namespace_And_Using()
    {
        var src = """
                  namespace Demo;
                  using Std.IO;
                  import Counter from "numz42";
                  """;
        var (cu, diags) = Parse(src);
        diags.HasErrors.Should().BeFalse(because: string.Join("\n", diags.All));

        cu.Namespace.Should().Be("Demo");
        cu.Usings.Should().Equal("Std.IO");
        cu.NativeImports.Should().NotBeNull();
        cu.NativeImports![0].Name.Should().Be("Counter");
        cu.NativeImports![0].LibName.Should().Be("numz42");
    }

    [Fact]
    public void Parser_NoImports_NativeImportsIsNull()
    {
        var (cu, diags) = Parse("class Foo { }");
        diags.HasErrors.Should().BeFalse();
        cu.NativeImports.Should().BeNull();
    }

    // ── Parser — error paths ───────────────────────────────────────────────────

    [Fact]
    public void Parser_MissingFrom_RaisesError()
    {
        var (_, diags) = Parse("import Counter \"numz42\";");
        diags.HasErrors.Should().BeTrue();
        diags.All.Any(d => d.Message.Contains("from")).Should().BeTrue(
            because: "diagnostic should mention the missing `from` keyword: " +
                     string.Join("\n", diags.All));
    }

    [Fact]
    public void Parser_MissingSemicolon_RaisesError()
    {
        // No `;` after the string literal — parser should report a missing `;`.
        var (_, diags) = Parse("import Counter from \"numz42\"");
        diags.HasErrors.Should().BeTrue();
    }
}
