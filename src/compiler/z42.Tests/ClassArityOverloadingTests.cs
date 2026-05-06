using FluentAssertions;
using Xunit;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// add-class-arity-overloading (2026-05-07): same source name with different
/// arities can coexist via shadow-only mangling. Non-generic owns bare key;
/// generic sibling moves to `Name$N` with HasArityMangle=true.
public sealed class ClassArityOverloadingTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SymbolTable Collect(string source, out DiagnosticBag diags)
    {
        diags = new DiagnosticBag();
        var tokens = new Lexer(source, "test.z42").Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var cu     = parser.ParseCompilationUnit();
        foreach (var d in parser.Diagnostics.All) diags.Add(d);
        var collector = new SymbolCollector(diags);
        return collector.Collect(cu);
    }

    // ── IrName derivation ────────────────────────────────────────────────────

    [Fact]
    public void IrName_NonGeneric_IsBare()
    {
        // No collision: bare name, no mangle, IrName == Name
        var symbols = Collect("class Foo { public int X; public Foo(int x) { this.X = x; } }", out var diags);
        diags.All.Where(d => d.IsError).Should().BeEmpty();
        symbols.Classes.Should().ContainKey("Foo");
        var foo = symbols.Classes["Foo"];
        foo.IrName.Should().Be("Foo");
        foo.HasArityMangle.Should().BeFalse();
    }

    [Fact]
    public void IrName_GenericAlone_IsBare()
    {
        // Generic alone (no non-generic sibling): keep bare key, no mangle.
        // This preserves backward compat for stdlib generic classes like List<T>.
        var symbols = Collect("class List<T> { public T Item; public List(T x) { this.Item = x; } }", out var diags);
        diags.All.Where(d => d.IsError).Should().BeEmpty();
        symbols.Classes.Should().ContainKey("List");
        var list = symbols.Classes["List"];
        list.IrName.Should().Be("List");
        list.HasArityMangle.Should().BeFalse();
    }

    [Fact]
    public void IrName_GenericWithCollision_IsMangled()
    {
        // `class Foo` + `class Foo<R>` coexist: generic gets `$1` shadow key + mangle flag.
        var src = """
            class Foo { public int Value; public Foo(int v) { this.Value = v; } }
            class Foo<R> { public R Item; public Foo(R x) { this.Item = x; } }
        """;
        var symbols = Collect(src, out var diags);
        diags.All.Where(d => d.IsError).Should().BeEmpty();

        symbols.Classes.Should().ContainKey("Foo");
        symbols.Classes.Should().ContainKey("Foo$1");

        var nonGeneric = symbols.Classes["Foo"];
        nonGeneric.IrName.Should().Be("Foo");
        nonGeneric.HasArityMangle.Should().BeFalse();
        nonGeneric.TypeParams.Should().BeNull();

        var generic = symbols.Classes["Foo$1"];
        generic.IrName.Should().Be("Foo$1");
        generic.HasArityMangle.Should().BeTrue();
        generic.TypeParams.Should().NotBeNull().And.HaveCount(1);
    }

    [Fact]
    public void IrName_MultiArityGeneric_IsMangledByCount()
    {
        // `class Pair` + `class Pair<A,B>` coexist: arity-2 generic keyed by `Pair$2`.
        var src = """
            class Pair { public string T; public Pair(string t) { this.T = t; } }
            class Pair<A, B> { public A First; public B Second; public Pair(A a, B b) { this.First = a; this.Second = b; } }
        """;
        var symbols = Collect(src, out var diags);
        diags.All.Where(d => d.IsError).Should().BeEmpty();

        symbols.Classes.Should().ContainKey("Pair");
        symbols.Classes.Should().ContainKey("Pair$2");

        symbols.Classes["Pair"].HasArityMangle.Should().BeFalse();
        symbols.Classes["Pair$2"].HasArityMangle.Should().BeTrue();
        symbols.Classes["Pair$2"].IrName.Should().Be("Pair$2");
    }

    // ── ResolveType routing ──────────────────────────────────────────────────

    [Fact]
    public void ResolveType_NamedType_PicksNonGeneric()
    {
        // NamedType("Foo") — bare lookup hits non-generic Foo regardless of generic sibling.
        var src = """
            class Foo { public int X; public Foo(int x) { this.X = x; } }
            class Foo<R> { public R Item; public Foo(R x) { this.Item = x; } }
        """;
        var symbols = Collect(src, out _);
        var t = symbols.ResolveType(new NamedType("Foo", default));
        t.Should().BeOfType<Z42ClassType>().Which.TypeParams.Should().BeNull(
            because: "bare-name lookup resolves to non-generic Foo, never the mangled generic Foo$1");
    }

    [Fact]
    public void ResolveType_GenericType_MatchesByArity()
    {
        // GenericType("Foo", [int]) — arity=1 lookup tries `Foo$1` first → finds generic.
        var src = """
            class Foo { public int X; public Foo(int x) { this.X = x; } }
            class Foo<R> { public R Item; public Foo(R x) { this.Item = x; } }
        """;
        var symbols = Collect(src, out _);
        var t = symbols.ResolveType(new GenericType("Foo",
            new List<TypeExpr> { new NamedType("int", default) }, default));
        t.Should().BeOfType<Z42InstantiatedType>()
            .Which.Definition.Name.Should().Be("Foo");
        ((Z42InstantiatedType)t).Definition.HasArityMangle.Should().BeTrue();
        ((Z42InstantiatedType)t).Definition.IrName.Should().Be("Foo$1");
    }

    [Fact]
    public void ResolveType_GenericAlone_FallsBackToBare()
    {
        // Pure generic (no non-generic sibling) — registered at bare `List` key.
        // GenericType("List", [int]) — `List$1` not found → fallback to bare `List` succeeds.
        var symbols = Collect(
            "class List<T> { public T Item; public List(T x) { this.Item = x; } }",
            out _);
        var t = symbols.ResolveType(new GenericType("List",
            new List<TypeExpr> { new NamedType("int", default) }, default));
        t.Should().BeOfType<Z42InstantiatedType>()
            .Which.Definition.IrName.Should().Be("List",
                because: "no collision means generic stays at bare key, IrName unchanged");
    }

    // ── Duplicate detection still works ──────────────────────────────────────

    [Fact]
    public void DuplicateSameArity_StillReportsError()
    {
        // Same name + same arity → real duplicate, E0408 fires.
        var src = """
            class Foo<R> { public R X; public Foo(R x) { this.X = x; } }
            class Foo<S> { public S Y; public Foo(S y) { this.Y = y; } }
        """;
        Collect(src, out var diags);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.DuplicateDeclaration
            && d.Message.Contains("Foo"));
    }
}
