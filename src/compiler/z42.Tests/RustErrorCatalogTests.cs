using FluentAssertions;
using Xunit;
using Z42.Core.Diagnostics;

namespace Z42.Tests;

/// <summary>
/// Verifies that <see cref="RustErrorCatalog"/> loads its embedded Z.json
/// resource and that all five Z codes resolve through both the catalog
/// directly and the central <see cref="DiagnosticCatalog"/> routing.
/// </summary>
public sealed class RustErrorCatalogTests
{
    [Fact]
    public void Embedded_resource_loads_at_least_five_entries()
    {
        RustErrorCatalog.All.Count.Should().BeGreaterThanOrEqualTo(5);
    }

    [Theory]
    [InlineData("Z0905", "Native type registration")]
    [InlineData("Z0906", "ABI version mismatch")]
    [InlineData("Z0907", "CallNativeVtable")]
    [InlineData("Z0908", "Pin / marshal")]
    [InlineData("Z0910", "Native library load")]
    public void Each_known_code_resolves_with_expected_title(string code, string titleFragment)
    {
        var entry = RustErrorCatalog.TryGet(code);
        entry.Should().NotBeNull();
        entry!.Title.Should().Contain(titleFragment);
        entry.Description.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Unknown_code_returns_null()
    {
        RustErrorCatalog.TryGet("Z9999").Should().BeNull();
    }

    [Fact]
    public void Codes_route_through_central_catalog()
    {
        var entry = DiagnosticCatalog.TryGet("Z0908");
        entry.Should().NotBeNull();
        entry!.Title.Should().Contain("Pin / marshal");
    }

    [Fact]
    public void Code_lookup_is_case_insensitive_for_explain()
    {
        // Driver upper-cases user input before calling Explain — but TryGet
        // itself uses OrdinalIgnoreCase via the dictionary comparer.
        RustErrorCatalog.TryGet("z0905").Should().NotBeNull();
        RustErrorCatalog.TryGet("Z0905").Should().NotBeNull();
    }
}
