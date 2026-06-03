using FluentAssertions;
using Z42.Core.Text;
using Z42.Semantics.Symbols;
using Z42.Semantics.TypeCheck;
using Z42.Semantics.TypeCheck.Binders;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// Unit tests for the Binder scaffold (review.md F2.4 Phase 1,
/// add-binder-hierarchy-phase1 2026-06-03). Phase 1 binders are NOT yet
/// wired into TypeChecker — these tests prove the chain dispatch + lookup
/// + shadowing semantics work on stub symbols, so when Phase 2 wires them
/// into TypeChecker the contract is already proven.
public sealed class BinderTests
{
    /// Tiny ISymbol used to seed the binders without dragging in
    /// TypeChecker / SymbolCollector. Mirrors `MethodSymbol`'s public
    /// surface but holds no real semantic state.
    private sealed class StubSymbol : ISymbol
    {
        public string Name { get; }
        public SymbolKind Kind { get; }
        public Span DeclarationSpan { get; }
        public Visibility Visibility { get; }

        public StubSymbol(string name, SymbolKind kind = SymbolKind.Local)
        {
            Name = name;
            Kind = kind;
            DeclarationSpan = default;
            Visibility = Visibility.Public;
        }
    }

    // ── Chain dispatch ───────────────────────────────────────────────────────

    [Fact]
    public void GlobalScopeBinder_FindsOwnSymbol()
    {
        var g = new GlobalScopeBinder();
        var foo = new StubSymbol("Foo", SymbolKind.Class);
        g.Define(foo);

        var result = g.LookupSymbol("Foo");
        result.Should().BeSameAs(foo);
        result!.Kind.Should().Be(SymbolKind.Class);
    }

    [Fact]
    public void GlobalScopeBinder_UnknownReturnsNull()
    {
        var g = new GlobalScopeBinder();
        g.LookupSymbol("Missing").Should().BeNull();
    }

    [Fact]
    public void InBlockBinder_FallsThroughToParentForGlobals()
    {
        var g = new GlobalScopeBinder();
        var globalFn = new StubSymbol("Print", SymbolKind.Method);
        g.Define(globalFn);

        var block = new InBlockBinder(g);
        // Block defines no locals — `Print` lookup falls through to global.
        block.LookupSymbol("Print").Should().BeSameAs(globalFn);
    }

    [Fact]
    public void InBlockBinder_LocalShadowsGlobal()
    {
        var g = new GlobalScopeBinder();
        var globalX = new StubSymbol("x", SymbolKind.Class);
        g.Define(globalX);

        var block = new InBlockBinder(g);
        var localX = new StubSymbol("x", SymbolKind.Local);
        block.DefineLocal(localX).Should().BeTrue();

        // Innermost scope wins — shadowing.
        var result = block.LookupSymbol("x");
        result.Should().BeSameAs(localX,
            "InBlockBinder must consult its own slot before forwarding");
        result!.Kind.Should().Be(SymbolKind.Local);

        // The global is still reachable from the global binder directly.
        g.LookupSymbol("x").Should().BeSameAs(globalX);
    }

    [Fact]
    public void InBlockBinder_DuplicateDefineReturnsFalse()
    {
        var block = new InBlockBinder(new GlobalScopeBinder());
        block.DefineLocal(new StubSymbol("x")).Should().BeTrue();
        block.DefineLocal(new StubSymbol("x"))
            .Should().BeFalse("re-defining the same name in one block must signal an error");
        block.DefinedInCurrentScope("x").Should().BeTrue();
    }

    // ── Three-level chain ────────────────────────────────────────────────────

    [Fact]
    public void DeepChain_LookupForwardsThroughAllLevels()
    {
        var g = new GlobalScopeBinder();
        g.Define(new StubSymbol("g", SymbolKind.Class));

        var method = new InMethodBinder(g);
        method.DefineParameter(new StubSymbol("p", SymbolKind.Parameter));

        var block1 = new InBlockBinder(method);
        block1.DefineLocal(new StubSymbol("b1", SymbolKind.Local));

        var block2 = new InBlockBinder(block1);
        block2.DefineLocal(new StubSymbol("b2", SymbolKind.Local));

        // From the innermost block, every name from every level resolves.
        block2.LookupSymbol("b2")!.Kind.Should().Be(SymbolKind.Local);
        block2.LookupSymbol("b1")!.Kind.Should().Be(SymbolKind.Local);
        block2.LookupSymbol("p") !.Kind.Should().Be(SymbolKind.Parameter);
        block2.LookupSymbol("g") !.Kind.Should().Be(SymbolKind.Class);
        block2.LookupSymbol("absent").Should().BeNull();
    }

    [Fact]
    public void InMethodBinder_ParametersDoNotLeakUp()
    {
        var g = new GlobalScopeBinder();
        var method = new InMethodBinder(g);
        method.DefineParameter(new StubSymbol("arg", SymbolKind.Parameter));

        // Looking up from the method binder finds the parameter.
        method.LookupSymbol("arg").Should().NotBeNull();

        // But the parent binder doesn't see the method's params (chain is
        // one-directional — child → parent, never the other way).
        g.LookupSymbol("arg").Should().BeNull(
            "InMethodBinder.parameters must not pollute the global scope");
    }
}
