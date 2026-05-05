using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Text;
using Z42.Semantics.Synthesis;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// Spec C11b/C11e — `ManifestSignatureParser` unit tests.
public sealed class ManifestSignatureParserTests
{
    private static readonly Span Anywhere = new(0, 0, 1, 1);

    /// Default: only the receiver type is in scope (mirrors a single-import CU).
    private static IReadOnlySet<string> Known(params string[] names)
        => new HashSet<string>(names, StringComparer.Ordinal);

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
        var t = (object)ManifestSignatureParser.ParseReturn(
            sig, "Counter", Known("Counter"), Anywhere);
        if (sig == "void")
            t.Should().BeOfType<VoidType>();
        else
            t.Should().BeOfType<NamedType>().Which.Name.Should().Be(sig);
    }

    [Fact]
    public void ParseReturn_Self_BindsToEnclosingType()
    {
        var t = ManifestSignatureParser.ParseReturn(
            "Self", "Counter", Known("Counter"), Anywhere);
        t.Should().BeOfType<NamedType>().Which.Name.Should().Be("Counter");
    }

    [Theory]
    [InlineData("String")]
    [InlineData("Box<Counter>")]
    [InlineData("[u8; 32]")]
    public void ParseReturn_UnsupportedShape_Throws_E0916(string sig)
    {
        var act = () => ManifestSignatureParser.ParseReturn(
            sig, "Counter", Known("Counter"), Anywhere);
        var ex  = act.Should().Throw<NativeImportException>().Which;
        ex.Code.Should().Be(DiagnosticCodes.NativeImportSynthesisFailure);
        ex.Message.Should().Contain(sig);
        ex.Message.Should().Contain("not supported");
    }

    // ── Param types ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("*mut Self")]
    [InlineData("*const Self")]
    public void ParseParam_FirstParam_PointerSelf_IsReceiver(string sig)
    {
        var (isReceiver, type) = ManifestSignatureParser.ParseParam(
            sig, "Counter", Known("Counter"), firstParam: true, Anywhere);
        isReceiver.Should().BeTrue();
        type.Should().BeNull();
    }

    [Fact]
    public void ParseParam_NonFirstParam_PointerSelf_RejectedAsUnsupported()
    {
        // Only accepts the receiver shape on the FIRST parameter; any later
        // occurrence must fail (we don't have a notion of "raw pointer params").
        var act = () => ManifestSignatureParser.ParseParam(
            "*mut Self", "Counter", Known("Counter"), firstParam: false, Anywhere);
        act.Should().Throw<NativeImportException>()
           .Which.Code.Should().Be(DiagnosticCodes.NativeImportSynthesisFailure);
    }

    [Fact]
    public void ParseParam_PrimitiveParam_NotReceiver()
    {
        var (isReceiver, type) = ManifestSignatureParser.ParseParam(
            "i64", "Counter", Known("Counter"), firstParam: false, Anywhere);
        isReceiver.Should().BeFalse();
        type.Should().BeOfType<NamedType>().Which.Name.Should().Be("i64");
    }

    [Fact]
    public void ParseParam_UnsupportedShape_Throws_E0916()
    {
        var act = () => ManifestSignatureParser.ParseParam(
            "Box<Counter>", "Counter", Known("Counter"), firstParam: false, Anywhere);
        var ex = act.Should().Throw<NativeImportException>().Which;
        ex.Code.Should().Be(DiagnosticCodes.NativeImportSynthesisFailure);
        ex.Message.Should().Contain("Box<Counter>");
        ex.Message.Should().Contain("not supported");
    }

    // ── C11e: c_char param ────────────────────────────────────────────────────

    [Theory]
    [InlineData("*const c_char")]
    [InlineData("*mut c_char")]
    public void ParseParam_CChar_AsString_Param(string sig)
    {
        var (isReceiver, type) = ManifestSignatureParser.ParseParam(
            sig, "Counter", Known("Counter"), firstParam: false, Anywhere);
        isReceiver.Should().BeFalse();
        type.Should().BeOfType<NamedType>().Which.Name.Should().Be("string");
    }

    [Theory]
    [InlineData("*const c_char")]
    [InlineData("*mut c_char")]
    public void ParseReturn_CChar_Throws_E0916_WithC11fHint(string sig)
    {
        var act = () => ManifestSignatureParser.ParseReturn(
            sig, "Counter", Known("Counter"), Anywhere);
        var ex  = act.Should().Throw<NativeImportException>().Which;
        ex.Code.Should().Be(DiagnosticCodes.NativeImportSynthesisFailure);
        ex.Message.Should().Contain("c_char return");
        ex.Message.Should().Contain("C11f");
    }

    // ── C11e: pointer to other imported type ──────────────────────────────────

    [Theory]
    [InlineData("*const Regex")]
    [InlineData("*mut Regex")]
    public void ParseParam_PointerToOtherImported_AsNamedType(string sig)
    {
        var (isReceiver, type) = ManifestSignatureParser.ParseParam(
            sig, "Match", Known("Match", "Regex"), firstParam: false, Anywhere);
        isReceiver.Should().BeFalse();
        type.Should().BeOfType<NamedType>().Which.Name.Should().Be("Regex");
    }

    [Theory]
    [InlineData("*const Regex")]
    [InlineData("*mut Regex")]
    public void ParseReturn_PointerToOtherImported_AsNamedType(string sig)
    {
        var t = ManifestSignatureParser.ParseReturn(
            sig, "Match", Known("Match", "Regex"), Anywhere);
        t.Should().BeOfType<NamedType>().Which.Name.Should().Be("Regex");
    }

    [Fact]
    public void ParseParam_PointerToUnknownType_Throws_UnknownType_E0916()
    {
        var act = () => ManifestSignatureParser.ParseParam(
            "*mut Foo", "Counter", Known("Counter"), firstParam: false, Anywhere);
        var ex  = act.Should().Throw<NativeImportException>().Which;
        ex.Code.Should().Be(DiagnosticCodes.NativeImportSynthesisFailure);
        ex.Message.Should().Contain("`Foo`");
        ex.Message.Should().Contain("import Foo from");
    }

    [Fact]
    public void ParseParam_ConstAndMut_Equivalent_ForOtherType()
    {
        var known = Known("Match", "Regex");
        var (_, t1) = ManifestSignatureParser.ParseParam(
            "*mut Regex", "Match", known, firstParam: false, Anywhere);
        var (_, t2) = ManifestSignatureParser.ParseParam(
            "*const Regex", "Match", known, firstParam: false, Anywhere);

        t1.Should().BeOfType<NamedType>().Which.Name.Should().Be("Regex");
        t2.Should().BeOfType<NamedType>().Which.Name.Should().Be("Regex");
    }

    [Fact]
    public void ParseReturn_UnsupportedShape_ListsImportedTypes()
    {
        var act = () => ManifestSignatureParser.ParseReturn(
            "Box<u8>", "Counter", Known("Counter", "Other"), Anywhere);
        var ex  = act.Should().Throw<NativeImportException>().Which;
        ex.Message.Should().Contain("Counter");
        ex.Message.Should().Contain("Other");
    }
}
