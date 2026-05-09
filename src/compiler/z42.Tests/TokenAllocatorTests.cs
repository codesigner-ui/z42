using FluentAssertions;
using Z42.IR;
using Z42.IR.BinaryFormat;

namespace Z42.Tests;

/// <summary>
/// Phase 3 (<c>tokenize-ir-and-zbc-bump</c>, 2026-05-09 redesigned): verifies
/// <see cref="TokenAllocator"/> returns local indices for intra-module
/// references and <c>IMPORT_BASE + STRS idx</c> for cross-zpkg references.
/// </summary>
public class TokenAllocatorTests
{
    private static IrModule BuildModule(string[] funcNames, string[] classNames)
    {
        var fns = funcNames
            .Select(n => new IrFunction(n, 0, "void", "Interp",
                Blocks: new List<IrBlock> { new("entry", new List<IrInstr>(), new RetTerm(null)) }))
            .ToList();
        var classes = classNames
            .Select(n => new IrClassDesc(n, BaseClass: null, Fields: new List<IrFieldDesc>()))
            .ToList();
        return new IrModule("Test", new List<string>(), classes, fns);
    }

    [Fact]
    public void LocalFunction_ResolvesToInsertionIndex()
    {
        var m = BuildModule(new[] { "Demo.First", "Demo.Second", "Demo.Third" }, Array.Empty<string>());
        var a = TokenAllocator.FromModule(m);
        var pool = new StringPool();

        a.ResolveMethod("Demo.First",  pool).Should().Be(0u);
        a.ResolveMethod("Demo.Second", pool).Should().Be(1u);
        a.ResolveMethod("Demo.Third",  pool).Should().Be(2u);
    }

    [Fact]
    public void LocalClass_ResolvesToInsertionIndex()
    {
        var m = BuildModule(Array.Empty<string>(), new[] { "Demo.Foo", "Demo.Bar" });
        var a = TokenAllocator.FromModule(m);
        var pool = new StringPool();

        a.ResolveType("Demo.Foo", pool).Should().Be(0u);
        a.ResolveType("Demo.Bar", pool).Should().Be(1u);
    }

    [Fact]
    public void NonLocalMethod_ResolvesToImportBasePlusPoolIdx()
    {
        var m = BuildModule(new[] { "Demo.Local" }, Array.Empty<string>());
        var a = TokenAllocator.FromModule(m);
        var pool = new StringPool();

        var token = a.ResolveMethod("Std.IO.Print", pool);
        token.Should().BeGreaterThanOrEqualTo(TokenConsts.ImportBase);
        token.Should().NotBe(TokenConsts.Unresolved);
        var poolIdx = (int)(token - TokenConsts.ImportBase);
        pool.Idx("Std.IO.Print").Should().Be(poolIdx, "import idx == STRS pool position");
    }

    [Fact]
    public void NonLocalClass_ResolvesToImportBasePlusPoolIdx()
    {
        var m = BuildModule(Array.Empty<string>(), new[] { "Demo.Local" });
        var a = TokenAllocator.FromModule(m);
        var pool = new StringPool();

        var token = a.ResolveType("Std.Object", pool);
        token.Should().BeGreaterThanOrEqualTo(TokenConsts.ImportBase);
    }

    [Fact]
    public void LocalAndImport_DistinctSpaces()
    {
        var m = BuildModule(new[] { "Demo.X" }, Array.Empty<string>());
        var a = TokenAllocator.FromModule(m);
        var pool = new StringPool();

        var local  = a.ResolveMethod("Demo.X",        pool);
        var import = a.ResolveMethod("Std.Foreign", pool);

        local.Should().BeLessThan(TokenConsts.ImportBase, "local index in low range");
        import.Should().BeGreaterThanOrEqualTo(TokenConsts.ImportBase, "import token has IMPORT_BASE bit");
    }

    [Fact]
    public void DuplicateNonLocalNameInternsOnceInPool()
    {
        var m = BuildModule(new[] { "Demo.Local" }, Array.Empty<string>());
        var a = TokenAllocator.FromModule(m);
        var pool = new StringPool();

        var t1 = a.ResolveMethod("Std.IO.Print", pool);
        var t2 = a.ResolveMethod("Std.IO.Print", pool);

        t1.Should().Be(t2, "same name → same token via pool intern dedup");
    }

    [Fact]
    public void TwoBuilds_SameSource_ProduceSameTokens()
    {
        // Determinism: same module → same allocator output.
        var m1 = BuildModule(new[] { "Foo", "Bar", "Baz" }, new[] { "C1", "C2" });
        var m2 = BuildModule(new[] { "Foo", "Bar", "Baz" }, new[] { "C1", "C2" });
        var a = TokenAllocator.FromModule(m1);
        var b = TokenAllocator.FromModule(m2);
        var pool1 = new StringPool();
        var pool2 = new StringPool();

        a.ResolveMethod("Foo", pool1).Should().Be(b.ResolveMethod("Foo", pool2));
        a.ResolveMethod("Bar", pool1).Should().Be(b.ResolveMethod("Bar", pool2));
        a.ResolveType("C1",   pool1).Should().Be(b.ResolveType("C1",   pool2));
    }

    [Fact]
    public void SourceOrderMatters_ChangingSourceChangesTokens()
    {
        // Documented tradeoff: re-arranging source declarations changes tokens.
        // This is acceptable per spec (reproducible build = same source → same output).
        var m1 = BuildModule(new[] { "A", "B" }, Array.Empty<string>());
        var m2 = BuildModule(new[] { "B", "A" }, Array.Empty<string>());
        var a = TokenAllocator.FromModule(m1);
        var b = TokenAllocator.FromModule(m2);
        var pool = new StringPool();

        a.ResolveMethod("A", pool).Should().Be(0u);
        a.ResolveMethod("B", pool).Should().Be(1u);
        b.ResolveMethod("A", pool).Should().Be(1u, "swapped insertion order swaps tokens");
        b.ResolveMethod("B", pool).Should().Be(0u);
    }
}
