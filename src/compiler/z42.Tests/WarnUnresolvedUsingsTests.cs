using FluentAssertions;
using Xunit;
using Z42.IR;
using Z42.Pipeline;

namespace Z42.Tests;

/// fix-warn-unresolved-intrapkg (2026-05-28): coverage for the
/// `PackageCompiler.FindUnresolvedUsings` decision: a `using` that names a
/// sibling namespace declared by another file in the SAME package being
/// compiled is legitimate (symbols resolve via intra-package fall-through)
/// and must not emit the "namespace not found in any library" warning.
public class WarnUnresolvedUsingsTests
{
    static CompiledUnit MakeUnit(string sourceFile, string ns, params string[] usings) =>
        new CompiledUnit(
            SourceFile:        sourceFile,
            Namespace:         ns,
            SourceHash:        "stub",
            Exports:           [],
            Module:            new IrModule(sourceFile, [], [], []),
            Usings:            [.. usings],
            UsedDepNamespaces: []);

    [Fact]
    public void UsingExternalNamespace_ResolvedViaNsMap_NoWarning()
    {
        var unit = MakeUnit("Foo.z42", "Std.MyApp", "Std.Encoding");
        var nsMap = new Dictionary<string, string> { ["Std.Encoding"] = "z42.encoding.zpkg" };

        PackageCompiler.FindUnresolvedUsings([unit], nsMap)
            .Should().BeEmpty();
    }

    [Fact]
    public void UsingExternalNamespace_NotInNsMap_EmitsWarning()
    {
        var unit = MakeUnit("Foo.z42", "Std.MyApp", "Std.Encoding");
        var nsMap = new Dictionary<string, string>();   // empty: not in any library

        var hits = PackageCompiler.FindUnresolvedUsings([unit], nsMap).ToList();
        hits.Should().HaveCount(1);
        hits[0].Unit.SourceFile.Should().Be("Foo.z42");
        hits[0].UsingNs.Should().Be("Std.Encoding");
    }

    /// THE CORE FIX: a using that names a namespace declared by another
    /// unit in the same package being compiled must NOT warn. Without this,
    /// z42.net would emit ~7 warnings every build (HttpClient using
    /// Std.Net.Sockets, etc.) and z42.compression would emit ~1 (Zip using
    /// Std.Compression).
    [Fact]
    public void UsingSiblingIntraPackageNamespace_NoWarning()
    {
        var sockets = MakeUnit("TcpClient.z42", "Std.Net.Sockets");
        var http    = MakeUnit("HttpClient.z42", "Std.Net.Http", "Std.Net.Sockets");
        var nsMap = new Dictionary<string, string>();

        PackageCompiler.FindUnresolvedUsings([sockets, http], nsMap)
            .Should().BeEmpty();
    }

    /// Multi-sibling case modelling z42.net which hosts Sockets + Http +
    /// WebSockets in one package: every cross-namespace `using` is intra-
    /// package, so the warning set is empty.
    [Fact]
    public void MultipleSiblingNamespaces_SamePackage_NoWarning()
    {
        var sockets = MakeUnit("TcpClient.z42",          "Std.Net.Sockets");
        var http    = MakeUnit("HttpClient.z42",         "Std.Net.Http",       "Std.Net.Sockets");
        var ws      = MakeUnit("WebSocketClient.z42",    "Std.Net.WebSockets", "Std.Net.Sockets", "Std.Net.Http");

        PackageCompiler.FindUnresolvedUsings([sockets, http, ws], new Dictionary<string, string>())
            .Should().BeEmpty();
    }

    /// External + intra-package usings mixed in one unit. Only the
    /// external-not-in-nsMap should warn.
    [Fact]
    public void MixedExternalAndIntra_OnlyTrulyUnresolvedWarns()
    {
        var sockets = MakeUnit("TcpClient.z42", "Std.Net.Sockets");
        var http    = MakeUnit("HttpClient.z42", "Std.Net.Http",
                               "Std.Net.Sockets",   // intra — OK
                               "Std.Encoding",      // external in nsMap — OK
                               "Std.Bogus");        // external NOT in nsMap — warns
        var nsMap = new Dictionary<string, string> { ["Std.Encoding"] = "z42.encoding.zpkg" };

        var hits = PackageCompiler.FindUnresolvedUsings([sockets, http], nsMap).ToList();
        hits.Should().HaveCount(1);
        hits[0].Unit.SourceFile.Should().Be("HttpClient.z42");
        hits[0].UsingNs.Should().Be("Std.Bogus");
    }

    /// Edge: own namespace declared and used (the unit's own ns isn't in
    /// nsMap but is in the intra-package set built from declared
    /// namespaces). Realistic when a file does `using Std.Net.Sockets` in
    /// a Sockets-namespace file — silly but not warning-worthy.
    [Fact]
    public void UsingOwnNamespace_NoWarning()
    {
        var unit = MakeUnit("Foo.z42", "Std.MyNs", "Std.MyNs");
        PackageCompiler.FindUnresolvedUsings([unit], new Dictionary<string, string>())
            .Should().BeEmpty();
    }

    /// Edge: empty inputs short-circuit (both nsMap and units empty →
    /// no decisions to make).
    [Fact]
    public void EmptyUnits_EmptyOutput()
    {
        PackageCompiler.FindUnresolvedUsings([], new Dictionary<string, string>())
            .Should().BeEmpty();
    }
}
