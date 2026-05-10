using FluentAssertions;
using Xunit;
using Z42.IR;
using Z42.IR.BinaryFormat;

namespace Z42.Tests;

/// <summary>
/// Coverage for the split-debug-symbols (zbc 1.2) sidecar mechanism:
/// WriteWithSidecar / ReadSidecar / ApplyDebugInfo + BuildId stability.
/// </summary>
public class SidecarSymbolsTests
{
    // ── Test fixture: two tiny functions, one with a LineTable + LocalVarTable ──
    private static IrModule MakeModule(bool withDebug = true)
    {
        var entry = new IrBlock("entry", [], new RetTerm(null));
        var fn0 = new IrFunction(
            Name: "Demo.main",
            ParamCount: 0,
            RetType: "void",
            ExecMode: "Interp",
            Blocks: [entry],
            LineTable: withDebug
                ? [new IrLineEntry(0, 0, 5, "Demo.z42", 3),
                   new IrLineEntry(0, 1, 7, "Demo.z42", 9)]
                : null,
            LocalVarTable: withDebug
                ? [new IrLocalVarEntry("x", 0)]
                : null);

        var fn1 = new IrFunction(
            Name: "Demo.helper",
            ParamCount: 0,
            RetType: "i32",
            ExecMode: "Interp",
            Blocks: [new IrBlock("entry", [], new RetTerm(null))],
            LineTable: withDebug
                ? [new IrLineEntry(0, 0, 12, "Demo.z42", 4)]
                : null);

        return new IrModule("Demo", [], [], [fn0, fn1]);
    }

    // ── Strip = true: main without DBUG, sidecar with DBUG + BLID ───────────
    [Fact]
    public void StripTrue_MainHasNoDbug_SidecarHasDbugAndBlid()
    {
        var m = MakeModule(withDebug: true);
        var (main, sidecar) = ZbcWriter.WriteWithSidecar(m, stripSymbols: true);

        sidecar.Should().NotBeNull("strip=true must produce a sidecar");
        var mainFlags    = ZbcReader.ReadFlags(main);
        var sidecarFlags = ZbcReader.ReadFlags(sidecar!);
        mainFlags.HasFlag(ZbcFlags.HasDebug).Should().BeFalse(
            "strip=true main file should not advertise inline DBUG");
        sidecarFlags.HasFlag(ZbcFlags.SymOnly).Should().BeTrue(
            "sidecar must set SymOnly flag");

        // BLID round-trips and is identical between main & sidecar
        byte[]? mainBlid    = ZbcReader.ReadBuildId(main);
        byte[]? sidecarBlid = ZbcReader.ReadBuildId(sidecar!);
        mainBlid.Should().NotBeNull();
        sidecarBlid.Should().NotBeNull();
        mainBlid.Should().Equal(sidecarBlid);
        mainBlid!.Length.Should().Be(BuildId.Size);
    }

    // ── Strip = false: DBUG inline, no sidecar, no BLID ─────────────────────
    [Fact]
    public void StripFalse_DbugInline_NoSidecar()
    {
        var m = MakeModule(withDebug: true);
        var (main, sidecar) = ZbcWriter.WriteWithSidecar(m, stripSymbols: false);

        sidecar.Should().BeNull();
        ZbcReader.ReadFlags(main).HasFlag(ZbcFlags.HasDebug).Should().BeTrue();
        ZbcReader.ReadBuildId(main).Should().BeNull("strip=false must not write BLID");

        var loaded = ZbcReader.Read(main);
        loaded.Functions[0].LineTable.Should().NotBeNull();
        loaded.Functions[0].LineTable!.Should().HaveCount(2);
    }

    // ── Module with no LineTable still produces sidecar (consistency) ───────
    [Fact]
    public void StripTrue_NoLineTable_StillProducesSidecar()
    {
        var m = MakeModule(withDebug: false);
        var (main, sidecar) = ZbcWriter.WriteWithSidecar(m, stripSymbols: true);

        sidecar.Should().NotBeNull();
        ZbcReader.ReadFlags(sidecar!).HasFlag(ZbcFlags.SymOnly).Should().BeTrue();
        ZbcReader.ReadBuildId(main).Should().Equal(ZbcReader.ReadBuildId(sidecar!));

        // Sidecar's DBUG has count=N entries (matches func count) but each is empty
        var sd = ZbcReader.ReadSidecar(sidecar!);
        sd.Functions.Should().HaveCount(2);
        sd.Functions[0].Lines.Should().BeNull();
        sd.Functions[0].Vars.Should().BeNull();
    }

    // ── BuildId stability: same input → same hash ───────────────────────────
    [Fact]
    public void BuildId_IsStable_AcrossRepeatedWrites()
    {
        var m = MakeModule();
        var (a, _) = ZbcWriter.WriteWithSidecar(m, stripSymbols: true);
        var (b, _) = ZbcWriter.WriteWithSidecar(m, stripSymbols: true);
        ZbcReader.ReadBuildId(a).Should().Equal(ZbcReader.ReadBuildId(b));
    }

    // ── BuildId sensitivity: tiny content change → different hash ───────────
    [Fact]
    public void BuildId_ChangesWhenAnyByteChanges()
    {
        var m1 = MakeModule();
        var m2 = m1 with { Name = "DemoX" };
        var (a, _) = ZbcWriter.WriteWithSidecar(m1, stripSymbols: true);
        var (b, _) = ZbcWriter.WriteWithSidecar(m2, stripSymbols: true);
        ZbcReader.ReadBuildId(a).Should().NotEqual(ZbcReader.ReadBuildId(b));
    }

    // ── Apply sidecar to main: re-attach LineTable ──────────────────────────
    [Fact]
    public void ApplyDebugInfo_RestoresLineTableOnMatchedBuildId()
    {
        var m = MakeModule(withDebug: true);
        var (main, sidecar) = ZbcWriter.WriteWithSidecar(m, stripSymbols: true);

        // Read main: line tables should be empty (DBUG stripped)
        var loaded = ZbcReader.Read(main);
        loaded.Functions[0].LineTable.Should().BeNull();
        loaded.Functions[1].LineTable.Should().BeNull();

        // Apply sidecar
        var sd = ZbcReader.ReadSidecar(sidecar!);
        var merged = ZbcReader.ApplyDebugInfo(loaded, sd);

        merged.Functions[0].LineTable.Should().NotBeNull();
        merged.Functions[0].LineTable!.Should().HaveCount(2);
        merged.Functions[0].LineTable![0].Line.Should().Be(5);
        merged.Functions[0].LineTable![0].File.Should().Be("Demo.z42");
        merged.Functions[0].LineTable![0].Column.Should().Be(3);
        merged.Functions[0].LocalVarTable.Should().NotBeNull();
        merged.Functions[0].LocalVarTable!.Should().HaveCount(1);
        merged.Functions[1].LineTable.Should().NotBeNull();
        merged.Functions[1].LineTable!.Should().HaveCount(1);
    }

    // ── ApplyDebugInfo rejects mismatched build_id ──────────────────────────
    [Fact]
    public void ApplyDebugInfo_RejectsMismatchedBuildId()
    {
        var m1 = MakeModule();
        var m2 = m1 with { Name = "Different" };
        var (main1, _)       = ZbcWriter.WriteWithSidecar(m1, stripSymbols: true);
        var (_,    sidecar2) = ZbcWriter.WriteWithSidecar(m2, stripSymbols: true);

        var loaded = ZbcReader.Read(main1);
        var sd     = ZbcReader.ReadSidecar(sidecar2!);

        Action act = () => ZbcReader.ApplyDebugInfo(loaded, sd);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*build_id mismatch*");
    }

    // ── Reading SymOnly file as main module is rejected ─────────────────────
    [Fact]
    public void Read_RejectsSymOnlyFileAsMainModule()
    {
        var m = MakeModule();
        var (_, sidecar) = ZbcWriter.WriteWithSidecar(m, stripSymbols: true);

        Action act = () => ZbcReader.Read(sidecar!);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*SymOnly*sidecar*");
    }

    // ── ReadSidecar rejects file without SymOnly flag ───────────────────────
    [Fact]
    public void ReadSidecar_RejectsRegularZbcFile()
    {
        var m = MakeModule();
        byte[] regular = ZbcWriter.Write(m);

        Action act = () => ZbcReader.ReadSidecar(regular);
        act.Should().Throw<InvalidDataException>()
            .WithMessage("*SymOnly*");
    }

    // ── BuildId.ShortHex matches expected runtime fallback format ───────────
    [Fact]
    public void BuildId_ShortHex_FormatsFirst4BytesAsLowercaseHex()
    {
        byte[] bid = [0xAB, 0xCD, 0x12, 0x34, 0xFF, 0xFF];
        BuildId.ShortHex(bid).Should().Be("abcd1234");
    }
}
