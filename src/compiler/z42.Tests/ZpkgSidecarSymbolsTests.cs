using FluentAssertions;
using Xunit;
using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Project;

namespace Z42.Tests;

/// <summary>
/// Coverage for the zpkg-level sidecar mechanism (1.5b split-debug-symbols):
/// ZpkgWriter.WritePackedWithSidecar / ZpkgReader.ReadSidecar +
/// ApplyDebugInfo, and zpkg version 0.3 + FlagSymOnly enforcement.
/// </summary>
public class ZpkgSidecarSymbolsTests
{
    // ── Test fixture: tiny packed zpkg with two modules carrying debug info ──
    private static ZpkgFile MakeZpkg(bool withDebug = true)
    {
        IrFunction MakeFn(string name, bool debug) => new(
            Name: name,
            ParamCount: 0,
            RetType: "void",
            ExecMode: "Interp",
            Blocks: [new IrBlock("entry", [], new RetTerm(null))],
            LineTable: debug
                ? [new IrLineEntry(0, 0, 5, "Mod.z42", 3),
                   new IrLineEntry(0, 1, 7, "Mod.z42", 9)]
                : null,
            LocalVarTable: debug
                ? [new IrLocalVarEntry("x", 0)]
                : null);

        var modA = new IrModule("Demo.A", [], [], [MakeFn("Demo.A.f1", withDebug), MakeFn("Demo.A.f2", withDebug)]);
        var modB = new IrModule("Demo.B", [], [], [MakeFn("Demo.B.g1", withDebug)]);

        var zbcA = new ZbcFile(
            ZbcVersion: ZbcFile.CurrentVersion,
            SourceFile: "src/A.z42",
            SourceHash: "hashA",
            Namespace:  "Demo.A",
            Exports:    ["Demo.A.f1", "Demo.A.f2"],
            Imports:    [],
            Module:     modA);
        var zbcB = new ZbcFile(
            ZbcVersion: ZbcFile.CurrentVersion,
            SourceFile: "src/B.z42",
            SourceHash: "hashB",
            Namespace:  "Demo.B",
            Exports:    ["Demo.B.g1"],
            Imports:    [],
            Module:     modB);

        return new ZpkgFile(
            Name:         "demo",
            Version:      "0.1.0",
            Kind:         ZpkgKind.Lib,
            Mode:         ZpkgMode.Packed,
            Namespaces:   ["Demo.A", "Demo.B"],
            Exports:      [
                new ZpkgExport("Demo.A.f1", "func"),
                new ZpkgExport("Demo.A.f2", "func"),
                new ZpkgExport("Demo.B.g1", "func"),
            ],
            Dependencies: [],
            Files:        [],
            Modules:      [zbcA, zbcB]);
    }

    [Fact]
    public void StripTrue_MainHasNoDbug_SidecarHasMdbgAndBlid()
    {
        var zpkg = MakeZpkg(withDebug: true);
        var (main, sidecar) = ZpkgWriter.WritePackedWithSidecar(zpkg, stripSymbols: true);

        sidecar.Should().NotBeNull("strip=true on packed zpkg must produce a sidecar");

        // Main has BLID; sidecar has BLID; bytes equal.
        byte[]? mainBlid = ZpkgReader.ReadBuildId(main);
        byte[]? symBlid  = ZpkgReader.ReadBuildId(sidecar!);
        mainBlid.Should().NotBeNull();
        symBlid.Should().NotBeNull();
        mainBlid.Should().Equal(symBlid);
        mainBlid!.Length.Should().Be(BuildId.Size);

        // Loading main should still work (DBUG bodies empty in MODS).
        var mods = ZpkgReader.ReadModules(main);
        mods.Should().HaveCount(2);
        mods[0].Module.Functions[0].LineTable.Should().BeNull(
            "strip=true means line tables are absent in main");
    }

    [Fact]
    public void StripFalse_NoSidecar_NoBlid()
    {
        var zpkg = MakeZpkg(withDebug: true);
        var (main, sidecar) = ZpkgWriter.WritePackedWithSidecar(zpkg, stripSymbols: false);

        sidecar.Should().BeNull();
        ZpkgReader.ReadBuildId(main).Should().BeNull();

        // Line tables should be inline in main when not stripped.
        var mods = ZpkgReader.ReadModules(main);
        mods[0].Module.Functions[0].LineTable.Should().NotBeNull();
        mods[0].Module.Functions[0].LineTable!.Should().HaveCount(2);
    }

    [Fact]
    public void NoLineTable_StillProducesSidecar_ConsistentArtifact()
    {
        var zpkg = MakeZpkg(withDebug: false);
        var (main, sidecar) = ZpkgWriter.WritePackedWithSidecar(zpkg, stripSymbols: true);

        sidecar.Should().NotBeNull();
        var sd = ZpkgReader.ReadSidecar(sidecar!);
        sd.Modules.Should().HaveCount(2);
        sd.Modules[0].Functions[0].Lines.Should().BeNull();
        sd.Modules[0].Functions[0].Vars.Should().BeNull();
    }

    [Fact]
    public void BuildId_StableAcrossRepeatedWrites()
    {
        var zpkg = MakeZpkg();
        var (a, _) = ZpkgWriter.WritePackedWithSidecar(zpkg, stripSymbols: true);
        var (b, _) = ZpkgWriter.WritePackedWithSidecar(zpkg, stripSymbols: true);
        ZpkgReader.ReadBuildId(a).Should().Equal(ZpkgReader.ReadBuildId(b));
    }

    [Fact]
    public void BuildId_ChangesWhenAnyByteChanges()
    {
        var z1 = MakeZpkg();
        var z2 = z1 with { Name = "demoX" };
        var (a, _) = ZpkgWriter.WritePackedWithSidecar(z1, stripSymbols: true);
        var (b, _) = ZpkgWriter.WritePackedWithSidecar(z2, stripSymbols: true);
        ZpkgReader.ReadBuildId(a).Should().NotEqual(ZpkgReader.ReadBuildId(b));
    }

    [Fact]
    public void ApplyDebugInfo_RestoresLineTablesOnMatchedBuildId()
    {
        var zpkg = MakeZpkg(withDebug: true);
        var (main, sidecar) = ZpkgWriter.WritePackedWithSidecar(zpkg, stripSymbols: true);

        // Main: line tables stripped.
        var mods = ZpkgReader.ReadModules(main);
        mods[0].Module.Functions[0].LineTable.Should().BeNull();

        // Apply sidecar.
        var sd = ZpkgReader.ReadSidecar(sidecar!);
        var merged = ZpkgReader.ApplyDebugInfo(mods, main, sd);

        merged[0].Module.Functions[0].LineTable.Should().NotBeNull();
        merged[0].Module.Functions[0].LineTable!.Should().HaveCount(2);
        merged[0].Module.Functions[0].LineTable![0].Line.Should().Be(5);
        merged[0].Module.Functions[0].LineTable![0].File.Should().Be("Mod.z42");
        merged[0].Module.Functions[0].LineTable![0].Column.Should().Be(3);
        merged[0].Module.Functions[0].LocalVarTable.Should().NotBeNull();
        merged[0].Module.Functions[0].LocalVarTable!.Should().HaveCount(1);
        merged[1].Module.Functions[0].LineTable.Should().NotBeNull();
        merged[1].Module.Functions[0].LineTable!.Should().HaveCount(2);
    }

    [Fact]
    public void ApplyDebugInfo_RejectsMismatchedBuildId()
    {
        var z1 = MakeZpkg();
        var z2 = z1 with { Name = "different" };
        var (main1, _)       = ZpkgWriter.WritePackedWithSidecar(z1, stripSymbols: true);
        var (_,    sidecar2) = ZpkgWriter.WritePackedWithSidecar(z2, stripSymbols: true);

        var mods = ZpkgReader.ReadModules(main1);
        var sd   = ZpkgReader.ReadSidecar(sidecar2!);

        Action act = () => ZpkgReader.ApplyDebugInfo(mods, main1, sd);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*build_id mismatch*");
    }

    [Fact]
    public void ReadModules_RejectsSymOnlyZpkgAsMain()
    {
        var zpkg = MakeZpkg();
        var (_, sidecar) = ZpkgWriter.WritePackedWithSidecar(zpkg, stripSymbols: true);

        Action act = () => ZpkgReader.ReadModules(sidecar!);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*SymOnly*sidecar*");
    }

    [Fact]
    public void ReadSidecar_RejectsNonSymOnlyZpkg()
    {
        var zpkg = MakeZpkg();
        var (main, _) = ZpkgWriter.WritePackedWithSidecar(zpkg, stripSymbols: true);

        Action act = () => ZpkgReader.ReadSidecar(main);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*SymOnly*");
    }
}
