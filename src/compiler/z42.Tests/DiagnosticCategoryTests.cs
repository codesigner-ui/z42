using System.Collections.Immutable;
using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Text;

namespace Z42.Tests;

/// <summary>
/// docs/review.md Part 6 F5 #2 (DiagnosticCategory) + #3 (Diagnostic.Properties)
/// (2026-05-25).
/// </summary>
public class DiagnosticCategoryTests
{
    [Theory]
    [InlineData("E0101", DiagnosticCategory.Lexer)]
    [InlineData("E0103", DiagnosticCategory.Lexer)]
    [InlineData("E0201", DiagnosticCategory.Parser)]
    [InlineData("E0205", DiagnosticCategory.Parser)]
    [InlineData("E0301", DiagnosticCategory.FeatureGate)]
    [InlineData("E0401", DiagnosticCategory.TypeCheck)]
    [InlineData("E0424", DiagnosticCategory.TypeCheck)]
    [InlineData("E0501", DiagnosticCategory.IrGen)]
    [InlineData("E0601", DiagnosticCategory.Package)]
    [InlineData("W0603", DiagnosticCategory.Package)]
    [InlineData("E0903", DiagnosticCategory.Native)]
    [InlineData("E0908a", DiagnosticCategory.Native)]
    [InlineData("E0916", DiagnosticCategory.Native)]
    public void Of_BuiltInBuckets(string code, DiagnosticCategory expected)
    {
        DiagnosticCategories.Of(code).Should().Be(expected);
    }

    [Theory]
    [InlineData("E0900", DiagnosticCategory.InternalCompilerError)]
    [InlineData("E0911", DiagnosticCategory.Test)]
    [InlineData("E0912", DiagnosticCategory.Test)]
    [InlineData("E0915", DiagnosticCategory.Test)]
    public void Of_SingleCodeCarveOutsInE09xx(string code, DiagnosticCategory expected)
    {
        DiagnosticCategories.Of(code).Should().Be(expected);
    }

    [Theory]
    [InlineData("E1001", DiagnosticCategory.ArgumentBind)]
    [InlineData("E1002", DiagnosticCategory.ArgumentBind)]
    [InlineData("E1005", DiagnosticCategory.ArgumentBind)]
    public void Of_ArgumentBindE10xx(string code, DiagnosticCategory expected)
    {
        DiagnosticCategories.Of(code).Should().Be(expected);
    }

    [Theory]
    [InlineData("WS001", DiagnosticCategory.Workspace)]
    [InlineData("WS123", DiagnosticCategory.Workspace)]
    [InlineData("Z001",  DiagnosticCategory.User)]
    [InlineData("Z456",  DiagnosticCategory.User)]
    public void Of_ExternalPrefixes(string code, DiagnosticCategory expected)
    {
        DiagnosticCategories.Of(code).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("X")]
    [InlineData("XY")]
    [InlineData("XYZ123")]
    [InlineData("E0X01")]    // non-digit bucket
    [InlineData("Q0101")]    // wrong leading letter
    [InlineData("E1X01")]    // E1 but not E10
    public void Of_UnknownWhenMalformed(string code)
    {
        DiagnosticCategories.Of(code).Should().Be(DiagnosticCategory.Unknown);
    }

    [Fact]
    public void Of_KnownDiagnosticCodesAllClassify()
    {
        // Every DiagnosticCodes const string must classify into a non-Unknown
        // bucket. This guards against adding a new code without updating the
        // category classifier (or its tests).
        var codeConsts = typeof(DiagnosticCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        codeConsts.Should().NotBeEmpty();
        foreach (var code in codeConsts)
        {
            var cat = DiagnosticCategories.Of(code);
            cat.Should().NotBe(DiagnosticCategory.Unknown,
                because: $"DiagnosticCodes.{code} is a registered code; classifier must bucket it");
        }
    }

    // ── Diagnostic.Properties (Part 6 F5 #3) ──────────────────────────────────

    [Fact]
    public void Diagnostic_PropertiesDefaultsToEmptyDictionary()
    {
        var d = Diagnostic.Error("E0401", "msg", new Span(0, 0, 1, 1, "f"));

        d.Props.Should().BeEmpty();
        d.Properties.Should().BeNull();
    }

    [Fact]
    public void Diagnostic_WithPropertyAdds()
    {
        var d = Diagnostic.Error("E0401", "msg", new Span(0, 0, 1, 1, "f"))
            .WithProperty("symbol", "Foo")
            .WithProperty("expected", "int");

        d.Props.Should().HaveCount(2);
        d.Props["symbol"].Should().Be("Foo");
        d.Props["expected"].Should().Be("int");
    }

    [Fact]
    public void Diagnostic_WithPropertyDoesNotMutateOriginal()
    {
        var original = Diagnostic.Error("E0401", "msg", new Span(0, 0, 1, 1, "f"));
        var updated  = original.WithProperty("k", "v");

        original.Props.Should().BeEmpty();
        updated.Props["k"].Should().Be("v");
    }
}
