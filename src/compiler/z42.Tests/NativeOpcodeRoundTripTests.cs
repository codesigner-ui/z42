using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Xunit;
using Z42.IR;
using Z42.IR.BinaryFormat;

namespace Z42.Tests;

/// <summary>
/// Round-trip coverage for the four C1-scaffold native interop opcodes:
/// CallNative, CallNativeVtable, PinPtr, UnpinPtr.
///
/// These opcodes are declared with no runtime behaviour yet (the VM traps),
/// but their binary encoding and IR record shapes must already round-trip
/// cleanly so subsequent specs (C2/C4/C5) can rely on the format being frozen.
/// </summary>
public class NativeOpcodeRoundTripTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static string ToJson(IrModule m) => JsonSerializer.Serialize(m, JsonOpts);

    private static IrModule Wrap(IrInstr instr, string retType = "void", IrTerminator? term = null)
    {
        var entry = new IrBlock("entry", [instr], term ?? new RetTerm(null));
        return new IrModule(
            Name       : "native_optest",
            StringPool : [],
            Classes    : [],
            Functions  : [
                new IrFunction(
                    Name       : "native_optest.Main",
                    ParamCount : 0,
                    RetType    : retType,
                    ExecMode   : "Interp",
                    Blocks     : [entry])
            ]
        );
    }

    private static IrModule RoundTrip(IrModule original)
    {
        byte[] bytes = ZbcWriter.Write(original);
        return ZbcReader.Read(bytes);
    }

    /// Idempotency check: a single round-trip is always equal to a second one.
    /// This sidesteps the fact that the binary format intentionally drops type
    /// info on source registers (only `Dst` carries a type tag), so the post-
    /// round-trip IR is the canonical form to compare against.
    private static void AssertRoundTripStable(IrModule original)
    {
        var first  = RoundTrip(original);
        var second = RoundTrip(first);
        ToJson(second).Should().Be(ToJson(first));
    }

    // ── CallNative ────────────────────────────────────────────────────────────

    [Fact]
    public void CallNative_RoundTrip_PreservesAllFields()
    {
        var dst   = new TypedReg(0, IrType.I64);
        var arg0  = new TypedReg(1, IrType.Unknown);
        var arg1  = new TypedReg(2, IrType.Unknown);
        var instr = new CallNativeInstr(
            Dst      : dst,
            Module   : "numz42",
            TypeName : "Tensor",
            Symbol   : "__shim_Tensor_dot",
            Args     : [arg0, arg1]);

        AssertRoundTripStable(Wrap(instr, retType: "i64"));

        // Spot-check key fields survive the round-trip (without depending on
        // operand-register type tags, which are reset to Unknown by design).
        var rt = RoundTrip(Wrap(instr, retType: "i64"));
        var read = (CallNativeInstr)rt.Functions[0].Blocks[0].Instructions[0];
        read.Module.Should().Be("numz42");
        read.TypeName.Should().Be("Tensor");
        read.Symbol.Should().Be("__shim_Tensor_dot");
        read.Args.Should().HaveCount(2);
        read.Dst.Type.Should().Be(IrType.I64);
    }

    // ── CallNativeVtable ──────────────────────────────────────────────────────

    [Fact]
    public void CallNativeVtable_RoundTrip_PreservesSlot()
    {
        var dst  = new TypedReg(0, IrType.Ref);
        var recv = new TypedReg(1, IrType.Ref);
        var arg  = new TypedReg(2, IrType.Unknown);
        var instr = new CallNativeVtableInstr(
            Dst        : dst,
            Recv       : recv,
            VtableSlot : 7,
            Args       : [arg]);

        AssertRoundTripStable(Wrap(instr, retType: "Tensor"));

        var rt = RoundTrip(Wrap(instr, retType: "Tensor"));
        var read = (CallNativeVtableInstr)rt.Functions[0].Blocks[0].Instructions[0];
        read.VtableSlot.Should().Be((ushort)7);
        read.Args.Should().HaveCount(1);
        read.Dst.Type.Should().Be(IrType.Ref);
    }

    // ── PinPtr ────────────────────────────────────────────────────────────────

    [Fact]
    public void PinPtr_RoundTrip_PreservesSrcDst()
    {
        var dst = new TypedReg(0, IrType.Ref);
        var src = new TypedReg(1, IrType.Str);
        var instr = new PinPtrInstr(dst, src);

        AssertRoundTripStable(Wrap(instr));

        var rt = RoundTrip(Wrap(instr));
        var read = (PinPtrInstr)rt.Functions[0].Blocks[0].Instructions[0];
        read.Dst.Id.Should().Be(0);
        read.Dst.Type.Should().Be(IrType.Ref);
        read.Src.Id.Should().Be(1);
    }

    // ── UnpinPtr ──────────────────────────────────────────────────────────────

    [Fact]
    public void UnpinPtr_RoundTrip_PreservesPinnedReg()
    {
        var pinned = new TypedReg(3, IrType.Ref);
        var instr  = new UnpinPtrInstr(pinned);

        AssertRoundTripStable(Wrap(instr));

        var rt = RoundTrip(Wrap(instr));
        var read = (UnpinPtrInstr)rt.Functions[0].Blocks[0].Instructions[0];
        read.Pinned.Id.Should().Be(3);
    }

    // ── Opcode bytes are pinned ───────────────────────────────────────────────

    [Fact]
    public void OpcodeBytes_ArePinnedToSpecValues()
    {
        Opcodes.CallNative.Should().Be(0x53);
        Opcodes.CallNativeVtable.Should().Be(0x54);
        Opcodes.PinPtr.Should().Be(0x90);
        Opcodes.UnpinPtr.Should().Be(0x91);
    }
}
