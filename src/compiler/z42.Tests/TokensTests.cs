using FluentAssertions;
using Z42.IR;

namespace Z42.Tests;

/// <summary>
/// Phase 3 (tokenize-ir-and-zbc-bump, 2026-05-09): mirrors Rust
/// <c>runtime/src/metadata/tokens_tests.rs</c>. Verifies token space
/// layout and IsImport / ImportIdx semantics across all 4 importable kinds.
/// </summary>
public class TokensTests
{
    [Fact]
    public void TokenConstants_HaveExpectedValues()
    {
        TokenConsts.Unresolved.Should().Be(0xFFFF_FFFFu);
        TokenConsts.ImportBase.Should().Be(0x8000_0000u);
    }

    [Fact]
    public void UnresolvedToken_IsNotResolved()
    {
        MethodId.Unresolved.IsResolved.Should().BeFalse();
        TypeId.Unresolved.IsResolved.Should().BeFalse();
        StaticFieldId.Unresolved.IsResolved.Should().BeFalse();
        BuiltinId.Unresolved.IsResolved.Should().BeFalse();
    }

    [Fact]
    public void IntraModuleToken_IsResolvedAndNotImport()
    {
        var m = new MethodId(0);
        m.IsResolved.Should().BeTrue();
        m.IsImport.Should().BeFalse();

        var t = new TypeId(42);
        t.IsResolved.Should().BeTrue();
        t.IsImport.Should().BeFalse();

        var s = new StaticFieldId(0x7FFF_FFFFu);
        s.IsResolved.Should().BeTrue();
        s.IsImport.Should().BeFalse();
    }

    [Fact]
    public void ImportToken_IsResolvedAndImport()
    {
        var m = new MethodId(TokenConsts.ImportBase);
        m.IsResolved.Should().BeTrue();
        m.IsImport.Should().BeTrue();
        m.ImportIdx.Should().Be(0u);

        var t = new TypeId(TokenConsts.ImportBase + 5u);
        t.IsImport.Should().BeTrue();
        t.ImportIdx.Should().Be(5u);

        var s = new StaticFieldId(TokenConsts.ImportBase + 0x1234u);
        s.IsImport.Should().BeTrue();
        s.ImportIdx.Should().Be(0x1234u);
    }

    [Fact]
    public void UnresolvedToken_IsNotImport()
    {
        // UNRESOLVED is in the >= ImportBase range numerically but specifically
        // excluded from import semantics so callers can tell the two apart.
        MethodId.Unresolved.IsImport.Should().BeFalse();
        TypeId.Unresolved.IsImport.Should().BeFalse();
    }

    [Fact]
    public void TokenTypes_AreDistinct()
    {
        // Compile-time check — these would not compile if MethodId == TypeId
        // (record struct types are distinct nominal types).
        var m = new MethodId(1);
        var t = new TypeId(1);
        m.Value.Should().Be(t.Value); // same underlying u32 …
        // … but you cannot assign one to the other:
        // MethodId bad = t;  // does not compile.
    }

    [Fact]
    public void ImportKind_HasFourKinds()
    {
        ((byte)ImportKind.Method).Should().Be(0x01);
        ((byte)ImportKind.Type).Should().Be(0x02);
        ((byte)ImportKind.StaticField).Should().Be(0x03);
        ((byte)ImportKind.Builtin).Should().Be(0x04);
    }

    [Fact]
    public void ToString_ReturnsHumanReadable()
    {
        MethodId.Unresolved.ToString().Should().Be("MethodId(unresolved)");
        new MethodId(7).ToString().Should().Be("MethodId(7)");
        new MethodId(TokenConsts.ImportBase + 3).ToString().Should().Be("MethodId(import 3)");
        new TypeId(0).ToString().Should().Be("TypeId(0)");
        new StaticFieldId(99).ToString().Should().Be("StaticFieldId(99)");
        new BuiltinId(2).ToString().Should().Be("BuiltinId(2)");
    }
}
