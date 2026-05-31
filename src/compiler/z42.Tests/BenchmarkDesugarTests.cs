using System.Linq;
using FluentAssertions;
using Z42.Core.Features;
using Z42.Semantics.Codegen;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// add-benchmark-bencher-arg-trampoline (2026-05-31) — verifies the
/// pre-TypeCheck AST desugar that rewrites `[Benchmark] void f(Bencher b)`
/// into a zero-arg `[Benchmark] void f()` wrapper + a demoted
/// `void f$impl(Bencher b)` helper.
public sealed class BenchmarkDesugarTests
{
    private static CompilationUnit ParseCu(string src)
    {
        var tokens = new Lexer(src, "<fixture>").Tokenize();
        return new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
    }

    private const string BencherStub = """
        namespace Std.Test;
        class Bencher { public Bencher() {} }
        """;

    private static FunctionDecl Fn(CompilationUnit cu, string name)
        => cu.Functions.Single(f => f.Name == name);

    // ── Bencher-arg desugars into wrapper + impl ──────────────────────────

    [Fact]
    public void BencherArg_ProducesWrapperPlusImpl()
    {
        var cu = ParseCu(BencherStub + """
            [Benchmark] void bench_add(Bencher b) {}
            """);
        var after = BenchmarkDesugar.Run(cu);

        after.Functions.Should().Contain(f => f.Name == "bench_add");
        after.Functions.Should().Contain(f => f.Name == "bench_add$impl");
    }

    [Fact]
    public void Wrapper_CarriesBenchmarkAttribute_ImplHasNone()
    {
        var cu = ParseCu(BencherStub + """
            [Benchmark] void bench_add(Bencher b) {}
            """);
        var after = BenchmarkDesugar.Run(cu);

        var wrapper = Fn(after, "bench_add");
        var impl    = Fn(after, "bench_add$impl");

        wrapper.TestAttributes.Should().NotBeNull();
        wrapper.TestAttributes!.Should().Contain(a => a.Name == "Benchmark");
        wrapper.Params.Should().BeEmpty("wrapper is zero-arg");

        (impl.TestAttributes is null || impl.TestAttributes.Count == 0)
            .Should().BeTrue("impl has no test attributes");
        impl.Params.Should().ContainSingle(p => p.Name == "b");
    }

    [Fact]
    public void Wrapper_BodyShape_NewBencher_CallImpl_PrintSummary()
    {
        var cu = ParseCu(BencherStub + """
            [Benchmark] void bench_add(Bencher b) {}
            """);
        var after = BenchmarkDesugar.Run(cu);
        var wrapper = Fn(after, "bench_add");

        wrapper.Body.Stmts.Should().HaveCount(3);

        // 1. var b = new Bencher();
        var decl = wrapper.Body.Stmts[0].Should().BeOfType<VarDeclStmt>().Subject;
        decl.Name.Should().Be("b");
        decl.Init.Should().BeOfType<NewExpr>()
            .Which.Type.Should().BeOfType<NamedType>()
            .Which.Name.Should().Be("Bencher");

        // 2. bench_add$impl(b);
        var call = wrapper.Body.Stmts[1].Should().BeOfType<ExprStmt>()
            .Which.Expr.Should().BeOfType<CallExpr>().Subject;
        call.Callee.Should().BeOfType<IdentExpr>().Which.Name.Should().Be("bench_add$impl");
        call.Args.Should().ContainSingle()
            .Which.Value.Should().BeOfType<IdentExpr>().Which.Name.Should().Be("b");

        // 3. b.printSummary("bench_add");
        var summary = wrapper.Body.Stmts[2].Should().BeOfType<ExprStmt>()
            .Which.Expr.Should().BeOfType<CallExpr>().Subject;
        var member = summary.Callee.Should().BeOfType<MemberExpr>().Subject;
        member.Member.Should().Be("printSummary");
        member.Target.Should().BeOfType<IdentExpr>().Which.Name.Should().Be("b");
        summary.Args.Should().ContainSingle()
            .Which.Value.Should().BeOfType<LitStrExpr>().Which.Value.Should().Be("bench_add");
    }

    [Fact]
    public void Impl_PreservesOriginalBody()
    {
        var cu = ParseCu(BencherStub + """
            [Benchmark] void bench_add(Bencher b) {
                b.iter(() => 1 + 2);
            }
            """);
        var origBody = Fn(cu, "bench_add").Body;
        var after = BenchmarkDesugar.Run(cu);
        var impl = Fn(after, "bench_add$impl");

        // Same body instance carried over (desugar only renames + strips attrs).
        impl.Body.Should().BeSameAs(origBody);
    }

    // ── Non-triggering cases pass through unchanged ───────────────────────

    [Fact]
    public void ZeroArgBenchmark_Untouched()
    {
        var cu = ParseCu(BencherStub + """
            [Benchmark] void bench_z() {}
            """);
        var after = BenchmarkDesugar.Run(cu);

        after.Functions.Where(f => f.Name.StartsWith("bench_z")).Should().ContainSingle();
        after.Functions.Should().NotContain(f => f.Name.Contains("$impl"));
    }

    [Fact]
    public void NonBencherParamBenchmark_Untouched()
    {
        // [Benchmark] void h(int x) — desugar must NOT fire; the validator
        // (run later) is responsible for the E0912 here.
        var cu = ParseCu(BencherStub + """
            [Benchmark] void bench_h(int x) {}
            """);
        var after = BenchmarkDesugar.Run(cu);

        after.Functions.Should().NotContain(f => f.Name.Contains("$impl"));
        Fn(after, "bench_h").Params.Should().ContainSingle(p => p.Name == "x");
    }

    [Fact]
    public void TestAttribute_Untouched()
    {
        var cu = ParseCu(BencherStub + """
            [Test] void t() {}
            """);
        var after = BenchmarkDesugar.Run(cu);
        after.Functions.Should().NotContain(f => f.Name.Contains("$impl"));
        Fn(after, "t").TestAttributes!.Should().Contain(a => a.Name == "Test");
    }

    [Fact]
    public void NoBenchmarks_ReturnsSameInstance()
    {
        var cu = ParseCu(BencherStub + """
            void plain() {}
            [Test] void t() {}
            """);
        var after = BenchmarkDesugar.Run(cu);
        // Fast-path no-op returns the same CompilationUnit reference.
        after.Should().BeSameAs(cu);
    }

    [Fact]
    public void TwoBencherArgBenchmarks_BothDesugar()
    {
        var cu = ParseCu(BencherStub + """
            [Benchmark] void bench_a(Bencher b) {}
            [Benchmark] void bench_b(Bencher b) {}
            """);
        var after = BenchmarkDesugar.Run(cu);
        after.Functions.Should().Contain(f => f.Name == "bench_a");
        after.Functions.Should().Contain(f => f.Name == "bench_a$impl");
        after.Functions.Should().Contain(f => f.Name == "bench_b");
        after.Functions.Should().Contain(f => f.Name == "bench_b$impl");
    }
}
