using FluentAssertions;
using Xunit;
using Z42.Core;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// <summary>
/// strict-using-resolution (2026-04-28) 端到端验证：
///   - prelude (z42.core) 默认激活
///   - 非 prelude 包必须 using
///   - using 不存在的 namespace → E0602
///   - 跨包同 (ns, class-name) → E0601
/// </summary>
public sealed class UsingResolutionTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (CompilationUnit Cu, DiagnosticBag Diags) Parse(string source)
    {
        var diags  = new DiagnosticBag();
        var tokens = new Lexer(source, "test.z42").Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var cu     = parser.ParseCompilationUnit();
        foreach (var d in parser.Diagnostics.All) diags.Add(d);
        return (cu, diags);
    }

    private static ExportedFieldDef Field(string name, string typeName) =>
        new(name, typeName, "public", IsStatic: false);

    private static ExportedClassDef Class(string name, params ExportedFieldDef[] fields) =>
        new(name, BaseClass: null,
            IsAbstract: false, IsSealed: false, IsStatic: false,
            Fields:     fields.ToList(),
            Methods:    new List<ExportedMethodDef>(),
            Interfaces: new List<string>(),
            TypeParams: null);

    private static ExportedModule Module(string ns, params ExportedClassDef[] classes) =>
        new(ns, classes.ToList(),
            new List<ExportedInterfaceDef>(),
            new List<ExportedEnumDef>(),
            new List<ExportedFuncDef>());

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TypeChecker_E0602_Reports_UnresolvedUsing()
    {
        var src = @"
            namespace App;
            using NoSuch.Pkg;
            void Main() {}
        ";
        var (cu, diags) = Parse(src);
        diags.HasErrors.Should().BeFalse(because: "parse should succeed");

        var coreMod = Module("Std", Class("Object"));
        var packageOf = new Dictionary<ExportedModule, string> { [coreMod] = "z42.core" };
        var imported = ImportedSymbolLoader.Load(
            new[] { coreMod }, packageOf,
            activatedPackages: new HashSet<string>(),
            preludePackages:   PreludePackages.Names);

        var tc = new TypeChecker(diags, LanguageFeatures.Phase1);
        tc.Check(cu, imported);

        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.UnresolvedUsing && d.Message.Contains("NoSuch.Pkg"));
    }

    [Fact]
    public void TypeChecker_E0601_Reports_NamespaceCollision()
    {
        var src = @"
            namespace App;
            using Foo;
            void Main() {}
        ";
        var (cu, diags) = Parse(src);

        // 两个 mock 包都在 namespace `Foo` 下声明 class `Util`，都被激活
        var modA = Module("Foo", Class("Util"));
        var modB = Module("Foo", Class("Util"));
        var packageOf = new Dictionary<ExportedModule, string>
        {
            [modA] = "packageA",
            [modB] = "packageB",
        };
        var imported = ImportedSymbolLoader.Load(
            new[] { modA, modB }, packageOf,
            activatedPackages: new HashSet<string> { "packageA", "packageB" },
            preludePackages:   new HashSet<string>());

        var tc = new TypeChecker(diags, LanguageFeatures.Phase1);
        tc.Check(cu, imported);

        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.NamespaceCollision
            && d.Message.Contains("Foo.Util")
            && d.Message.Contains("packageA")
            && d.Message.Contains("packageB"));
    }

    [Fact]
    public void TypeChecker_NoError_When_PreludeProvidesType()
    {
        var src = @"
            namespace App;
            void Main() {}
        ";
        var (cu, diags) = Parse(src);

        var coreMod = Module("Std", Class("Object"));
        var packageOf = new Dictionary<ExportedModule, string> { [coreMod] = "z42.core" };
        var imported = ImportedSymbolLoader.Load(
            new[] { coreMod }, packageOf,
            activatedPackages: new HashSet<string>(),
            preludePackages:   PreludePackages.Names);

        var tc = new TypeChecker(diags, LanguageFeatures.Phase1);
        tc.Check(cu, imported);

        diags.All.Where(d => d.IsError).Should().BeEmpty(
            because: "无 using 也无未知符号引用，应零错误");
    }

    [Fact]
    public void TypeChecker_NoError_When_UsedNamespaceHasOnlyImpls()
    {
        // Regression: fix-cross-zpkg-using-resolution (2026-05-06).
        // 一个 activated package 只贡献 `impl Trait for Type` 块、没有 class /
        // interface / enum / function 时，`using <ns>;` 也必须能解析（不报 E0602）。
        // Cross-zpkg L3-Impl2 的真实使用场景。
        var src = @"
            namespace App;
            using Demo.Greeter;
            void Main() {}
        ";
        var (cu, diags) = Parse(src);

        // demo.target 提供 Robot 类（Demo.Target 命名空间）
        var targetMod = new ExportedModule(
            "Demo.Target",
            new List<ExportedClassDef> { Class("Robot") },
            new List<ExportedInterfaceDef>(),
            new List<ExportedEnumDef>(),
            new List<ExportedFuncDef>());
        // demo.greeter 只有 impl 块，无 class — Demo.Greeter 不出现在 ClassNamespaces
        var greeterMod = new ExportedModule(
            "Demo.Greeter",
            new List<ExportedClassDef>(),
            new List<ExportedInterfaceDef>(),
            new List<ExportedEnumDef>(),
            new List<ExportedFuncDef>(),
            Impls: new List<ExportedImplDef>());

        var packageOf = new Dictionary<ExportedModule, string>
        {
            [targetMod]  = "demo.target",
            [greeterMod] = "demo.greeter",
        };
        var imported = ImportedSymbolLoader.Load(
            new[] { targetMod, greeterMod }, packageOf,
            activatedPackages: new HashSet<string> { "demo.target", "demo.greeter" },
            preludePackages:   new HashSet<string>());

        imported.ResolvedNamespaces.Should().NotBeNull();
        imported.ResolvedNamespaces!.Should().Contain("Demo.Greeter",
            because: "impl-only 包的 namespace 必须进入 ResolvedNamespaces");

        var tc = new TypeChecker(diags, LanguageFeatures.Phase1);
        tc.Check(cu, imported);

        diags.All.Where(d => d.Code == DiagnosticCodes.UnresolvedUsing).Should().BeEmpty(
            because: "Demo.Greeter 由 demo.greeter 包提供（虽只有 impl），E0602 不应触发");
    }

    [Fact]
    public void PreludePackages_HasZ42Core()
    {
        PreludePackages.Names.Should().Contain("z42.core");
        PreludePackages.IsPrelude("z42.core").Should().BeTrue();
        PreludePackages.IsPrelude("z42.io").Should().BeFalse();
    }

    [Fact]
    public void PreludePackages_DetectsReservedPrefix()
    {
        PreludePackages.IsReservedNamespace("Std").Should().BeTrue();
        PreludePackages.IsReservedNamespace("Std.IO").Should().BeTrue();
        PreludePackages.IsReservedNamespace("Std.Collections").Should().BeTrue();
        PreludePackages.IsReservedNamespace("MyCorp").Should().BeFalse();
        PreludePackages.IsReservedNamespace("Standard").Should().BeFalse(
            because: "前缀必须以 . 分隔，避免误匹配 Standard 这类无关名");
    }

    [Fact]
    public void PreludePackages_StdlibPrefixDetection()
    {
        PreludePackages.IsStdlibPackage("z42.core").Should().BeTrue();
        PreludePackages.IsStdlibPackage("z42.io").Should().BeTrue();
        PreludePackages.IsStdlibPackage("z42.collections").Should().BeTrue();
        PreludePackages.IsStdlibPackage("acme.utils").Should().BeFalse();
        PreludePackages.IsStdlibPackage("z42").Should().BeFalse(
            because: "必须 z42. 前缀（含点），不是 z42 本身");
    }

    /// fix-intra-package-resolved-ns (2026-05-28): a `using` declaration
    /// targeting a SIBLING namespace declared by another unit in the same
    /// package being compiled must NOT raise E0602. Previously the
    /// TypeChecker's resolvedNs set only included the unit's own
    /// namespace plus external dep namespaces; intra-package siblings
    /// were invisible on a clean build (no stale .zpkg yet). The fix
    /// seeds ResolvedNamespaces on intraSymbols with every namespace
    /// declared by the units being compiled.
    [Fact]
    public void TypeChecker_NoE0602_For_IntraPackageSiblingUsing()
    {
        // Unit B declares Std.Net.Http and uses Std.Net.Sockets (sibling
        // namespace declared by Unit A in the same package). Clean build
        // means no z42.net.zpkg yet → tsigCache has no entry for either
        // namespace. Only an ImportedSymbols whose ResolvedNamespaces
        // includes Std.Net.Sockets keeps E0602 silent.
        var src = @"
            namespace Std.Net.Http;
            using Std.Net.Sockets;
            void Main() {}
        ";
        var (cu, diags) = Parse(src);
        diags.HasErrors.Should().BeFalse(because: "parse should succeed");

        var coreMod = Module("Std", Class("Object"));
        var packageOf = new Dictionary<ExportedModule, string> { [coreMod] = "z42.core" };
        // Simulate intraSymbols seeded with sibling namespaces via the
        // ResolvedNamespaces parameter on the loader (the production
        // path threads this through ExtractIntraSymbols).
        var imported = ImportedSymbolLoader.Load(
            new[] { coreMod }, packageOf,
            activatedPackages: new HashSet<string>(),
            preludePackages:   PreludePackages.Names);
        var withSibling = imported with
        {
            ResolvedNamespaces = new HashSet<string>(StringComparer.Ordinal)
            {
                "Std",
                "Std.Net.Sockets",   // declared by sibling unit
                "Std.Net.Http",      // own namespace
            },
        };

        var tc = new TypeChecker(diags, LanguageFeatures.Phase1);
        tc.Check(cu, withSibling);

        diags.All.Should().NotContain(d => d.Code == DiagnosticCodes.UnresolvedUsing,
            because: "intra-package sibling namespace is now in ResolvedNamespaces");
    }

    /// Regression guard for the original E0602 case: when ResolvedNamespaces
    /// does NOT include the sibling (the broken state before the fix),
    /// E0602 still fires. Ensures the fix didn't accidentally hide all
    /// E0602 errors.
    [Fact]
    public void TypeChecker_StillE0602_When_NamespaceNotInResolvedSet()
    {
        var src = @"
            namespace Std.Net.Http;
            using Truly.Bogus;
            void Main() {}
        ";
        var (cu, diags) = Parse(src);

        var coreMod = Module("Std", Class("Object"));
        var packageOf = new Dictionary<ExportedModule, string> { [coreMod] = "z42.core" };
        var imported = ImportedSymbolLoader.Load(
            new[] { coreMod }, packageOf,
            activatedPackages: new HashSet<string>(),
            preludePackages:   PreludePackages.Names);

        var tc = new TypeChecker(diags, LanguageFeatures.Phase1);
        tc.Check(cu, imported);

        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.UnresolvedUsing && d.Message.Contains("Truly.Bogus"));
    }
}
