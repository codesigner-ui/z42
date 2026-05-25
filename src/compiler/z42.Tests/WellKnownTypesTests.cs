using FluentAssertions;
using Z42.Semantics.TypeCheck;

namespace Z42.Tests;

/// <summary>
/// docs/review.md Part 6 F5 #1 — WellKnownTypes centralization (2026-05-25).
/// </summary>
public class WellKnownTypesTests
{
    [Theory]
    [InlineData("int")]
    [InlineData("i32")]
    [InlineData("long")]
    [InlineData("i64")]
    [InlineData("byte")]
    [InlineData("u8")]
    [InlineData("sbyte")]
    [InlineData("i8")]
    [InlineData("ushort")]
    [InlineData("u16")]
    [InlineData("short")]
    [InlineData("i16")]
    [InlineData("uint")]
    [InlineData("u32")]
    [InlineData("ulong")]
    [InlineData("u64")]
    [InlineData("float")]
    [InlineData("f32")]
    [InlineData("double")]
    [InlineData("f64")]
    [InlineData("bool")]
    [InlineData("string")]
    [InlineData("char")]
    [InlineData("object")]
    [InlineData("void")]
    [InlineData("var")]
    public void TryResolve_KnownPrimitive_ReturnsTrue(string name)
    {
        WellKnownTypes.TryResolve(name, out var t).Should().BeTrue();
        t.Should().NotBeNull();
    }

    [Theory]
    [InlineData("int", "i32")]
    [InlineData("long", "i64")]
    [InlineData("byte", "u8")]
    [InlineData("sbyte", "i8")]
    [InlineData("ushort", "u16")]
    [InlineData("short", "i16")]
    [InlineData("uint", "u32")]
    [InlineData("ulong", "u64")]
    [InlineData("float", "f32")]
    [InlineData("double", "f64")]
    public void TryResolve_AliasPair_ReturnsSameSingleton(string legacy, string modern)
    {
        WellKnownTypes.TryResolve(legacy, out var a).Should().BeTrue();
        WellKnownTypes.TryResolve(modern, out var b).Should().BeTrue();

        a.Should().BeSameAs(b, because: $"{legacy} and {modern} must alias to the same Z42Type singleton");
    }

    [Theory]
    [InlineData("Foo")]
    [InlineData("MyClass")]
    [InlineData("")]
    [InlineData("INT")]   // case-sensitive — canonical names only
    [InlineData("Int")]
    public void TryResolve_UnknownOrCaseMismatch_ReturnsFalse(string name)
    {
        WellKnownTypes.TryResolve(name, out var t).Should().BeFalse();
        t.Should().BeSameAs(Z42Type.Unknown);
    }

    [Fact]
    public void IsPrimitiveName_MatchesTryResolve()
    {
        foreach (var name in WellKnownTypes.ByName.Keys)
            WellKnownTypes.IsPrimitiveName(name).Should().BeTrue();

        WellKnownTypes.IsPrimitiveName("MyClass").Should().BeFalse();
    }

    [Fact]
    public void AllPrimitives_NoDuplicates_AllInByName()
    {
        var distinct = WellKnownTypes.AllPrimitives.Distinct().ToList();
        distinct.Should().HaveCount(WellKnownTypes.AllPrimitives.Count,
            because: "AllPrimitives is the canonical singleton list — must not duplicate");

        // Every entry in AllPrimitives must appear at least once in ByName (as canonical or alias).
        foreach (var prim in WellKnownTypes.AllPrimitives)
            WellKnownTypes.ByName.Values.Should().Contain(prim);
    }

    [Fact]
    public void ByName_AllZ42TypeSingletonsResolveBack()
    {
        // Self-consistency: every Z42Type.X singleton appears in ByName under at
        // least one name (catches "added a singleton, forgot to register").
        var singletons = new Z42Type[]
        {
            Z42Type.Int, Z42Type.Long, Z42Type.I8, Z42Type.I16,
            Z42Type.U8, Z42Type.U16, Z42Type.U32, Z42Type.U64,
            Z42Type.Float, Z42Type.Double,
            Z42Type.Bool, Z42Type.String, Z42Type.Char, Z42Type.Object,
            Z42Type.Void, Z42Type.Unknown,
        };

        foreach (var s in singletons)
            WellKnownTypes.ByName.Values.Should().Contain(s);
    }
}
