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

    // 2026-05-11 retire-z-codes: the Rust-side Z#### catalog and its
    // C# embedding (RustErrorCatalog) were removed. `z42c explain Z####`
    // now falls into the unknown-code path with a friendly retirement hint.
    [Fact]
    public void Explain_Z0905_returns_retired_hint()
    {
        var text = DiagnosticCatalog.Explain("Z0905");
        text.Should().Contain("retired");
        text.Should().NotContain("error[Z0905]");
    }

    [Fact]
    public void Explain_unknown_code_returns_friendly_message()
    {
        var text = DiagnosticCatalog.Explain("X9999");
        text.Should().Contain("No documentation found");
        text.Should().Contain("z42c errors");
    }

    [Fact]
    public void ListAll_includes_compiler_and_workspace_groups()
    {
        var text = DiagnosticCatalog.ListAll();
        text.Should().Contain("Compiler diagnostics (E####)");
        text.Should().Contain("Workspace / manifest diagnostics (WS###)");
        text.Should().Contain("E0402");
        text.Should().Contain("WS003");
        text.Should().NotContain("Z0905");
    }

    [Fact]
    public void TryGet_finds_workspace_codes_through_central_catalog()
    {
        var entry = DiagnosticCatalog.TryGet("WS006");
        entry.Should().NotBeNull();
        entry!.Title.Should().Contain("Circular dependency");
    }

    /// Guards against drift between the `DiagnosticCodes` / `Z42Errors`
    /// constant tables and their catalog entries. If you add a new error
    /// code without a catalog entry (or vice versa), this fires.
    [Fact]
    public void Every_defined_code_has_a_catalog_entry()
    {
        var unregistered = new List<string>();

        // Compiler codes (DiagnosticCodes → CompilerCatalog.All)
        foreach (var field in typeof(DiagnosticCodes).GetFields(
                     System.Reflection.BindingFlags.Public |
                     System.Reflection.BindingFlags.Static))
        {
            if (field.FieldType != typeof(string)) continue;
            var code = (string?)field.GetRawConstantValue();
            if (code is null) continue;
            if (DiagnosticCatalog.TryGet(code) is null)
                unregistered.Add($"DiagnosticCodes.{field.Name} = \"{code}\"");
        }

        // Workspace codes (Z42Errors → WorkspaceCatalog.All)
        foreach (var field in typeof(Z42Errors).GetFields(
                     System.Reflection.BindingFlags.Public |
                     System.Reflection.BindingFlags.Static))
        {
            if (field.FieldType != typeof(string)) continue;
            var code = (string?)field.GetRawConstantValue();
            if (code is null) continue;
            if (DiagnosticCatalog.TryGet(code) is null)
                unregistered.Add($"Z42Errors.{field.Name} = \"{code}\"");
        }

        unregistered.Should().BeEmpty(
            because: "every defined diagnostic code must have a catalog entry " +
                     "(z42c explain <code> needs to resolve). Add an entry to " +
                     "DiagnosticCatalog.cs / WorkspaceCatalog.cs for each code listed.");
    }
}
