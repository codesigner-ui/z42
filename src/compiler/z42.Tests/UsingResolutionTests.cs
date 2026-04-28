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
}
