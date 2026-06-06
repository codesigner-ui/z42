using FluentAssertions;
using Xunit;
using Z42.Core.Diagnostics;
using Z42.Core.Text;
using Z42.Pipeline;

namespace Z42.Tests;

/// simplify-stdlib-auto-import (2026-06-06): coverage for the E0605 hard error —
/// a third-party package (name not starting with `z42.`) declaring a namespace
/// under the reserved `Std` / `Std.*` prefix in its own source is rejected, so
/// every `Std.*` a program uses resolves to the official, auto-available stdlib
/// and can never be shadowed. z42.* packages are exempt (they ARE the stdlib).
public class ReservedNamespaceDeclarationTests
{
    static readonly Span AnySpan = new(0, 0, 0, 0, "Foo.z42");

    [Theory]
    [InlineData("acme.app", "Std")]            // bare reserved root
    [InlineData("acme.app", "Std.Widgets")]    // reserved sub-namespace
    [InlineData("my-utils", "Std.Collections")] // different third-party name
    public void ThirdPartyDeclaresReservedNamespace_EmitsE0605(string pkg, string ns)
    {
        var diag = PackageCompiler.CheckReservedNamespaceDeclaration(pkg, ns, AnySpan);

        diag.Should().NotBeNull();
        diag!.Code.Should().Be(DiagnosticCodes.ReservedNamespaceDeclaration);
        diag.Code.Should().Be("E0605");
        diag.IsError.Should().BeTrue();
        diag.Message.Should().Contain(ns);
        diag.Message.Should().Contain(pkg);
    }

    [Theory]
    [InlineData("z42.io", "Std.IO")]           // stdlib package: legitimate Std.* use
    [InlineData("z42.core", "Std")]            // prelude declares bare Std
    [InlineData("z42.collections", "Std.Collections")]
    public void StdlibPackageDeclaresReservedNamespace_Exempt(string pkg, string ns)
    {
        PackageCompiler.CheckReservedNamespaceDeclaration(pkg, ns, AnySpan)
            .Should().BeNull();
    }

    [Theory]
    [InlineData("acme.app", "Acme.Widgets")]   // own prefix — fine
    [InlineData("acme.app", "Standard")]       // `Std` is NOT a prefix of `Standard` (dot-boundary)
    [InlineData("acme.app", "StdLib")]         // same: no dot boundary
    public void ThirdPartyNonReservedNamespace_NoError(string pkg, string ns)
    {
        PackageCompiler.CheckReservedNamespaceDeclaration(pkg, ns, AnySpan)
            .Should().BeNull();
    }

    [Fact]
    public void ThirdPartyDefaultNamespace_NoError()
    {
        // null namespace = the default/implicit namespace; nothing reserved is declared.
        PackageCompiler.CheckReservedNamespaceDeclaration("acme.app", null, AnySpan)
            .Should().BeNull();
    }
}
