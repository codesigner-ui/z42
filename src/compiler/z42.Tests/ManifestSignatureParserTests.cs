using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Text;
using Z42.Semantics.Synthesis;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// Spec C11b — `ManifestSignatureParser` unit tests.
public sealed class ManifestSignatureParserTests
{
    private static readonly Span Anywhere = new(0, 0, 1, 1);

    // ── Return types ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("void")]
    [InlineData("i8")]
    [InlineData("i16")]
    [InlineData("i32")]
    [InlineData("i64")]
    [InlineData("u8")]
    [InlineData("u16")]
    [InlineData("u32")]
    [InlineData("u64")]
    [InlineData("f32")]
    [InlineData("f64")]
    [InlineData("bool")]
    public void ParseReturn_Primitive_Roundtrips(string sig)
    {
        var t = (object)ManifestSignatureParser.ParseReturn(sig, "Counter", Anywhere);
        if (sig == "void")
            t.Should().BeOfType<VoidType>();
        else
            t.Should().BeOfType<NamedType>().Which.Name.Should().Be(sig);
    }

    [Fact]
    public void ParseReturn_Self_BindsToEnclosingType()
    {
        var t = ManifestSignatureParser.ParseReturn("Self", "Counter", Anywhere);
        t.Should().BeOfType<NamedType>().Which.Name.Should().Be("Counter");
    }

    [Theory]
    [InlineData("*const c_char")]
    [InlineData("String")]
    [InlineData("Box<Counter>")]
    [InlineData("Foo")]
    public void ParseReturn_Unsupported_Throws_E0916(string sig)
    {
        var act = () => ManifestSignatureParser.ParseReturn(sig, "Counter", Anywhere);
        var ex  = act.Should().Throw<NativeImportException>().Which;
        ex.Code.Should().Be(DiagnosticCodes.NativeImportSynthesisFailure);
        ex.Message.Should().Contain(sig);
    }

    // ── Param types ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("*mut Self")]
    [InlineData("*const Self")]
    public void ParseParam_FirstParam_PointerSelf_IsReceiver(string sig)
    {
        var (isReceiver, type) = ManifestSignatureParser.ParseParam(
            sig, "Counter", firstParam: true, Anywhere);
        isReceiver.Should().BeTrue();
        type.Should().BeNull();
    }

    [Fact]
    public void ParseParam_NonFirstParam_PointerSelf_RejectedAsUnsupported()
    {
        // C11b only accepts the receiver shape on the FIRST parameter; any later
        // occurrence must fail (we don't have a notion of "raw pointer params").
        var act = () => ManifestSignatureParser.ParseParam(
            "*mut Self", "Counter", firstParam: false, Anywhere);
        act.Should().Throw<NativeImportException>()
           .Which.Code.Should().Be(DiagnosticCodes.NativeImportSynthesisFailure);
    }

    [Fact]
    public void ParseParam_PrimitiveParam_NotReceiver()
    {
        var (isReceiver, type) = ManifestSignatureParser.ParseParam(
            "i64", "Counter", firstParam: false, Anywhere);
        isReceiver.Should().BeFalse();
        type.Should().BeOfType<NamedType>().Which.Name.Should().Be("i64");
    }

    [Fact]
    public void ParseParam_Unsupported_Throws_E0916()
    {
        var act = () => ManifestSignatureParser.ParseParam(
            "*const c_char", "Counter", firstParam: false, Anywhere);
        var ex = act.Should().Throw<NativeImportException>().Which;
        ex.Code.Should().Be(DiagnosticCodes.NativeImportSynthesisFailure);
        ex.Message.Should().Contain("c_char");
    }
}
