using FluentAssertions;
using Z42.IR;

namespace Z42.Tests;

/// <summary>
/// Phase 3 (<c>tokenize-ir-and-zbc-bump</c>, 2026-05-09): verifies determinism
/// and import_table ordering invariants of <see cref="TokenAllocator"/>.
/// </summary>
public class TokenAllocatorTests
{
    // ── Determinism: same registration set → same IDs regardless of order ──

    [Fact]
    public void TypeId_AssignedInOrdinalOrder()
    {
        var a = new TokenAllocator();
        a.RegisterClass("Demo.Bbb");
        a.RegisterClass("Demo.Aaa");
        a.RegisterClass("Demo.Ccc");
        a.Build();

        a.ResolveType("Demo.Aaa").Value.Should().Be(0u);
        a.ResolveType("Demo.Bbb").Value.Should().Be(1u);
        a.ResolveType("Demo.Ccc").Value.Should().Be(2u);
    }

    [Fact]
    public void MethodId_AssignedInOrdinalOrder()
    {
        var a = new TokenAllocator();
        // Simulate IrGen registering in arbitrary visit order.
        a.RegisterMethod("Foo.zzz");
        a.RegisterMethod("Aaa.bar");
        a.RegisterMethod("Foo.bar$2");
        a.RegisterMethod("Foo.bar");
        a.Build();

        // String.CompareOrdinal: '$' (0x24) < 'a' (0x61). Hence "Foo.bar$2" < "Foo.bar".
        // Wait — actually "Foo.bar" < "Foo.bar$2" because the shorter is prefix.
        // CompareOrdinal: shorter prefix sorts first.
        a.ResolveMethod("Aaa.bar").Value.Should().Be(0u);
        a.ResolveMethod("Foo.bar").Value.Should().Be(1u);
        a.ResolveMethod("Foo.bar$2").Value.Should().Be(2u);
        a.ResolveMethod("Foo.zzz").Value.Should().Be(3u);
    }

    [Fact]
    public void StaticFieldId_AssignedInOrdinalOrder()
    {
        var a = new TokenAllocator();
        a.RegisterStaticField("Foo.x");
        a.RegisterStaticField("Aaa.y");
        a.RegisterStaticField("Foo.a");
        a.Build();

        a.ResolveStaticField("Aaa.y").Value.Should().Be(0u);
        a.ResolveStaticField("Foo.a").Value.Should().Be(1u);
        a.ResolveStaticField("Foo.x").Value.Should().Be(2u);
    }

    [Fact]
    public void TwoAllocators_SameInputs_ProduceSameTokens()
    {
        var inputs = new[] { "Demo.Foo", "Demo.Bar", "Demo.Aaa" };

        var a = new TokenAllocator();
        foreach (var n in inputs) a.RegisterClass(n);
        a.Build();

        var b = new TokenAllocator();
        // Different insertion order should not affect output.
        foreach (var n in inputs.Reverse()) b.RegisterClass(n);
        b.Build();

        foreach (var n in inputs)
            a.ResolveType(n).Should().Be(b.ResolveType(n));
    }

    // ── Idempotency ────────────────────────────────────────────────────────

    [Fact]
    public void Register_Idempotent()
    {
        var a = new TokenAllocator();
        a.RegisterClass("Demo.X");
        a.RegisterClass("Demo.X");  // duplicate
        a.RegisterClass("Demo.X");  // duplicate
        a.Build();

        a.ResolveType("Demo.X").Value.Should().Be(0u);
        a.ImportTable.Count.Should().Be(0);
    }

    [Fact]
    public void DiscoverImport_Idempotent()
    {
        var a = new TokenAllocator();
        a.DiscoverImport(ImportKind.Method, "Std.IO.Print");
        a.DiscoverImport(ImportKind.Method, "Std.IO.Print");  // duplicate
        a.DiscoverImport(ImportKind.Method, "Std.IO.Print");  // duplicate
        a.Build();

        a.ImportTable.Count.Should().Be(1);
    }

    // ── Import table ordering ──────────────────────────────────────────────

    [Fact]
    public void ImportTable_SortedByKindThenName()
    {
        var a = new TokenAllocator();
        a.DiscoverImport(ImportKind.Type,        "Std.Object");
        a.DiscoverImport(ImportKind.Method,      "Std.IO.Print");
        a.DiscoverImport(ImportKind.StaticField, "Std.Math.PI");
        a.DiscoverImport(ImportKind.Method,      "Std.IO.ReadLine");
        a.DiscoverImport(ImportKind.Type,        "Std.Array");
        a.Build();

        var t = a.ImportTable;
        t.Count.Should().Be(5);
        // Method (0x01) entries first, sorted by name
        t[0].Should().Be(new ImportEntry(ImportKind.Method, "Std.IO.Print"));
        t[1].Should().Be(new ImportEntry(ImportKind.Method, "Std.IO.ReadLine"));
        // Type (0x02) entries next
        t[2].Should().Be(new ImportEntry(ImportKind.Type, "Std.Array"));
        t[3].Should().Be(new ImportEntry(ImportKind.Type, "Std.Object"));
        // StaticField (0x03) entries last
        t[4].Should().Be(new ImportEntry(ImportKind.StaticField, "Std.Math.PI"));
    }

    [Fact]
    public void ImportToken_IsImportBasePlusIdx()
    {
        var a = new TokenAllocator();
        a.DiscoverImport(ImportKind.Method, "Std.IO.Print");
        a.DiscoverImport(ImportKind.Method, "Std.IO.ReadLine");
        a.Build();

        var p = a.ResolveMethod("Std.IO.Print");
        p.Value.Should().Be(TokenConsts.ImportBase + 0u);
        p.IsImport.Should().BeTrue();
        p.ImportIdx.Should().Be(0u);

        var r = a.ResolveMethod("Std.IO.ReadLine");
        r.Value.Should().Be(TokenConsts.ImportBase + 1u);
        r.ImportIdx.Should().Be(1u);
    }

    [Fact]
    public void IntraModule_AndImport_DistinctSpaces()
    {
        var a = new TokenAllocator();
        a.RegisterMethod("Demo.Local");
        a.DiscoverImport(ImportKind.Method, "Std.Foreign");
        a.Build();

        var local = a.ResolveMethod("Demo.Local");
        local.IsImport.Should().BeFalse();
        local.Value.Should().BeLessThan(TokenConsts.ImportBase);

        var foreign = a.ResolveMethod("Std.Foreign");
        foreign.IsImport.Should().BeTrue();
    }

    // ── Lifecycle errors ───────────────────────────────────────────────────

    [Fact]
    public void Resolve_BeforeBuild_Throws()
    {
        var a = new TokenAllocator();
        a.RegisterClass("X");

        var act = () => a.ResolveType("X");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must call Build()*");
    }

    [Fact]
    public void Register_AfterBuild_Throws()
    {
        var a = new TokenAllocator();
        a.Build();

        var act = () => a.RegisterClass("X");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot register after Build()*");
    }

    [Fact]
    public void Resolve_UnknownName_Throws()
    {
        var a = new TokenAllocator();
        a.Build();

        var act = () => a.ResolveMethod("Demo.Nonexistent");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not registered*");
    }

    // ── Build is idempotent ────────────────────────────────────────────────

    [Fact]
    public void Build_Idempotent()
    {
        var a = new TokenAllocator();
        a.RegisterClass("X");
        a.Build();
        a.Build();  // second call is a no-op

        a.ResolveType("X").Value.Should().Be(0u);
    }
}
