using FluentAssertions;
using Xunit;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// fix-array-indexed-method-e0402 (2026-06-07): a member type that references a
/// class collected in a LATER compilation unit degrades to Z42PrimType during
/// per-CU collection. SymbolCollector.FinalizeTypeReferences upgrades it back to
/// the real Z42ClassType once every CU is collected — otherwise `T x = arr[i].M()`
/// (M's return type degraded) raised a spurious "E0402: cannot assign T to T" in
/// workspace builds (where CU order happened to collect the referer first).
public sealed class SymbolCollectorTypeFixupTests
{
    private static CompilationUnit Parse(string source, DiagnosticBag diags)
    {
        var tokens = new Lexer(source, "t.z42").Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var cu = parser.ParseCompilationUnit();
        foreach (var d in parser.Diagnostics.All) diags.Add(d);
        return cu;
    }

    [Fact]
    public void ForwardClassRef_InMethodReturn_UpgradedByFinalize()
    {
        var diags = new DiagnosticBag();
        // Collect B (whose Make() returns A) BEFORE A is defined → A degrades.
        var cuB = Parse("class B { public A Make() { return new A(); } }", diags);
        var cuA = Parse("class A { public int V; }", diags);

        var collector = new SymbolCollector(diags);
        collector.Collect(cuB);
        var table = collector.Collect(cuA);

        var make = table.Classes["B"].Methods["Make"];
        // Pre-finalize: the forward reference is the degraded sentinel.
        make.Signature.Ret.Should().BeOfType<Z42PrimType>()
            .Which.Name.Should().Be("A");

        collector.FinalizeInheritance();   // runs FinalizeTypeReferences

        // Post-finalize: upgraded in place to the real class type.
        make.Signature.Ret.Should().BeOfType<Z42ClassType>()
            .Which.Name.Should().Be("A");
    }

    [Fact]
    public void ForwardClassRef_InFieldAndArrayParam_Upgraded()
    {
        var diags = new DiagnosticBag();
        // C references A as a field type, an array element, and a param type —
        // all collected before A.
        var cuC = Parse(
            "class C { public A One; public A[] Many; public void Take(A a) {} }", diags);
        var cuA = Parse("class A { public int V; }", diags);

        var collector = new SymbolCollector(diags);
        collector.Collect(cuC);
        var table = collector.Collect(cuA);
        collector.FinalizeInheritance();

        var c = table.Classes["C"];
        c.Fields["One"].Type.Should().BeOfType<Z42ClassType>().Which.Name.Should().Be("A");
        c.Fields["Many"].Type.Should().BeOfType<Z42ArrayType>()
            .Which.Element.Should().BeOfType<Z42ClassType>().Which.Name.Should().Be("A");
        c.Methods["Take"].Signature.Params[0].Should().BeOfType<Z42ClassType>()
            .Which.Name.Should().Be("A");
    }

    /// fix-stale-classtype-in-signature (2026-06-18): a self / forward reference
    /// can degrade not only to a `Z42PrimType` sentinel but to a *stale*
    /// `Z42ClassType` skeleton — the class is registered (Phase 1) so ResolveType
    /// returns it, but its Methods dict is still empty because classes are
    /// immutable records replaced (not mutated) as members are merged. Left stale,
    /// `E.Root().With(5)` resolves `.With` against the empty skeleton →
    /// `E0402 type E has no method With` (a valid program wrongly rejected).
    [Fact]
    public void SelfRef_StaleClassTypeSkeleton_UpgradedToFullByFinalize()
    {
        var diags = new DiagnosticBag();
        // E.Root() returns E (self-reference); the return type captured during
        // collection is E's skeleton (no methods yet).
        var cuE = Parse(
            "class E { public static E Root() { return new E(); } "
            + "public E With(int x) { return this; } }", diags);

        var collector = new SymbolCollector(diags);
        var table = collector.Collect(cuE);
        collector.FinalizeInheritance();   // runs FinalizeTypeReferences

        // Root's return type must be the FULL E — its Methods dict carries `With`,
        // so chained `E.Root().With(...)` resolves.
        var rootRet = table.Classes["E"].StaticMethods["Root"].Signature.Ret;
        rootRet.Should().BeOfType<Z42ClassType>().Which.Name.Should().Be("E");
        ((Z42ClassType)rootRet).Methods.Should().ContainKey("With");
    }

    /// End-to-end: the chained factory call type-checks with no diagnostics.
    /// Regressing the fixup re-raises `E0402 type E has no method With`.
    [Fact]
    public void ChainedFactoryCall_TypeChecks_NoErrors()
    {
        var diags = new DiagnosticBag();
        var cu = Parse(
            "class E { public static E Root() { return new E(); } "
            + "public E With(int x) { return this; } "
            + "public int Use() { var a = E.Root().With(5); return 1; } }", diags);

        // Check() runs the full pipeline (collect + FinalizeTypeReferences + bind).
        new TypeChecker(diags).Check(cu);

        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void UnknownTypeName_StaysPrimType_NotUpgraded()
    {
        // A genuine unknown (typo) must remain Z42PrimType — fixup only touches
        // names that resolve to a real collected class.
        var diags = new DiagnosticBag();
        var cu = Parse("class B { public NoSuchType Make() { return null; } }", diags);
        var collector = new SymbolCollector(diags);
        var table = collector.Collect(cu);
        collector.FinalizeInheritance();
        table.Classes["B"].Methods["Make"].Signature.Ret
            .Should().BeOfType<Z42PrimType>().Which.Name.Should().Be("NoSuchType");
    }
}
