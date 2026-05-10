using FluentAssertions;
using Xunit;
using Z42.Core.Diagnostics;
using Z42.Project;

namespace Z42.Tests;

/// <summary>
/// Cross-catalog routing — `z42c explain WS003` should resolve via
/// WorkspaceCatalog (registered by Z42.Project's module initializer).
/// </summary>
public sealed class DiagnosticCatalogRoutingTests
{
    public DiagnosticCatalogRoutingTests()
    {
        // Touch a Z42.Project type to guarantee the assembly's module
        // initializer (which calls WorkspaceCatalog.Register) has fired.
        _ = Z42Errors.WS003;
    }

    [Fact]
    public void Explain_E0402_uses_compiler_catalog()
    {
        var text = DiagnosticCatalog.Explain("E0402");
        text.Should().Contain("error[E0402]");
        text.Should().Contain("Type mismatch");
    }

    [Fact]
    public void Explain_WS003_uses_workspace_catalog()
    {
        var text = DiagnosticCatalog.Explain("WS003");
        text.Should().Contain("error[WS003]");
        text.Should().Contain("Forbidden section in member manifest");
    }

    [Fact]
    public void Explain_unknown_Z_code_includes_runtime_hint()
    {
        var text = DiagnosticCatalog.Explain("Z9999");
        text.Should().Contain("No documentation found");
        text.Should().Contain("VM runtime errors");
    }

    [Fact]
    public void Explain_unknown_E_code_no_runtime_hint()
    {
        var text = DiagnosticCatalog.Explain("E9999");
        text.Should().Contain("No documentation found");
        text.Should().NotContain("VM runtime errors");
    }

    [Fact]
    public void ListAll_includes_E_and_W_groups()
    {
        var text = DiagnosticCatalog.ListAll();
        text.Should().Contain("Compiler diagnostics (E####)");
        text.Should().Contain("Workspace / manifest diagnostics (WS###)");
        text.Should().Contain("E0402");
        text.Should().Contain("WS003");
    }

    [Fact]
    public void TryGet_finds_workspace_codes_through_central_catalog()
    {
        var entry = DiagnosticCatalog.TryGet("WS006");
        entry.Should().NotBeNull();
        entry!.Title.Should().Contain("Circular dependency");
    }
}
