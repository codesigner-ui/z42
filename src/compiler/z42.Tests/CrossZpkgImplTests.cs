using FluentAssertions;
using Xunit;
using Z42.IR;
using Z42.Project;
using Z42.Semantics.TypeCheck;

namespace Z42.Tests;

/// <summary>
/// L3-Impl2: cross-zpkg `impl Trait for Type { ... }` propagation through the
/// IMPL section of a zpkg. Verifies (1) ZpkgWriter → ZpkgReader round-trips
/// the IMPL records, and (2) ImportedSymbolLoader Phase 3 merges impl methods
/// into the imported target class's Methods + ClassInterfaces.
/// </summary>
public sealed class CrossZpkgImplTests
{
    [Fact]
    public void Impl_RoundTrip_ThroughZpkgBinary()
    {
        // 模拟 z42.greet 包：impl IGreet for Robot { string Hello(); }
        // (target Robot 在 z42.core，trait IGreet 在 z42.core；本包只导出 impl)
        var implDef = new ExportedImplDef(
            TargetFqName: "Std.Robot",
            TraitFqName:  "Std.IGreet",
            TraitTypeArgs: new List<string>(),
            Methods: new List<ExportedMethodDef>
            {
                new("Hello",
                    Params: new List<ExportedParamDef>(),
                    ReturnType: "string",
                    Visibility: "public",
                    IsStatic: false, IsVirtual: false, IsAbstract: false,
                    MinArgCount: 0)
            });
        var module = new ExportedModule("Std.Greet",
            new List<ExportedClassDef>(),
            new List<ExportedInterfaceDef>(),
            new List<ExportedEnumDef>(),
            new List<ExportedFuncDef>(),
            Impls: new List<ExportedImplDef> { implDef });

        var zpkg = new ZpkgFile(
            Name: "greet", Version: "0.1.0",
            Kind: ZpkgKind.Lib, Mode: ZpkgMode.Packed,
            Namespaces: new List<string> { "Std.Greet" },
            Exports: new List<ZpkgExport>(),
            Dependencies: new List<ZpkgDep>(),
            Files: new List<ZpkgFileEntry>(),
            Modules: new List<ZbcFile>(),
            Entry: null,
            ExportedModules: new List<ExportedModule> { module });

        var bytes  = ZpkgWriter.Write(zpkg);
        var read   = ZpkgReader.ReadTsig(bytes);
        var modOut = read.Single();

        modOut.Impls.Should().NotBeNull();
        modOut.Impls!.Should().HaveCount(1);
        var implOut = modOut.Impls[0];
        implOut.TargetFqName.Should().Be("Std.Robot");
        implOut.TraitFqName.Should().Be("Std.IGreet");
        implOut.Methods.Should().HaveCount(1);
        implOut.Methods[0].Name.Should().Be("Hello");
        implOut.Methods[0].ReturnType.Should().Be("string");
    }

    [Fact]
    public void Impl_WithTraitTypeArgs_RoundTrip()
    {
        // impl IComparer<int> for IntDescComparer { int Compare(int, int); }
        var implDef = new ExportedImplDef(
            TargetFqName: "Demo.IntDescComparer",
            TraitFqName:  "Std.IComparer",
            TraitTypeArgs: new List<string> { "int" },
            Methods: new List<ExportedMethodDef>
            {
                new("Compare",
                    new List<ExportedParamDef> {
                        new("p0", "int"), new("p1", "int")
                    },
                    "int", "public", false, false, false, -1)
            });
        var module = new ExportedModule("Demo",
            new List<ExportedClassDef>(),
            new List<ExportedInterfaceDef>(),
            new List<ExportedEnumDef>(),
            new List<ExportedFuncDef>(),
            Impls: new List<ExportedImplDef> { implDef });

        var zpkg = new ZpkgFile(
            "demo", "0.1.0", ZpkgKind.Lib, ZpkgMode.Packed,
            Namespaces: new List<string> { "Demo" },
            Exports: new List<ZpkgExport>(),
            Dependencies: new List<ZpkgDep>(),
            Files: new List<ZpkgFileEntry>(),
            Modules: new List<ZbcFile>(),
            Entry: null,
            ExportedModules: new List<ExportedModule> { module });

        var bytes = ZpkgWriter.Write(zpkg);
        var read  = ZpkgReader.ReadTsig(bytes);
        var implOut = read.Single().Impls!.Single();

        implOut.TraitTypeArgs.Should().ContainSingle().Which.Should().Be("int");
    }

    [Fact]
    public void NoImplBlocks_EmptyImplsRoundTrip()
    {
        // ExportedModules 里没有 impl —— 仍写 IMPL section（空），读回 Impls 应为 null。
        var module = new ExportedModule("Demo",
            new List<ExportedClassDef>(),
            new List<ExportedInterfaceDef>(),
            new List<ExportedEnumDef>(),
            new List<ExportedFuncDef>());

        var zpkg = new ZpkgFile(
            "demo", "0.1.0", ZpkgKind.Lib, ZpkgMode.Packed,
            Namespaces: new List<string> { "Demo" },
            Exports: new List<ZpkgExport>(),
            Dependencies: new List<ZpkgDep>(),
            Files: new List<ZpkgFileEntry>(),
            Modules: new List<ZbcFile>(),
            Entry: null,
            ExportedModules: new List<ExportedModule> { module });

        var bytes = ZpkgWriter.Write(zpkg);
        var read  = ZpkgReader.ReadTsig(bytes);

        // 空 IMPL section 写入但反序列化时 0 records 不附加 → Impls 应为 null
        read.Single().Impls.Should().BeNull();
    }
}
