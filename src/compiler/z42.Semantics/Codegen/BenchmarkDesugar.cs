using System.Collections.Generic;
using Z42.Core.Text;
using Z42.Syntax.Parser;

namespace Z42.Semantics.Codegen;

/// <summary>
/// add-benchmark-bencher-arg-trampoline (2026-05-31) — AST-level desugar that
/// restores the ergonomic <c>[Benchmark] void f(Bencher b)</c> signature.
///
/// add-benchmark-runner-dispatch (#6) constrained <c>[Benchmark]</c> to a
/// zero-arg signature because the runner had no way to construct a Bencher
/// and inject it. Rather than build that runtime machinery, this pass
/// rewrites a Bencher-arg benchmark into a zero-arg wrapper that constructs
/// the Bencher itself, plus a demoted helper holding the original body:
///
/// <code>
/// [Benchmark] void f(Bencher b) { BODY }
/// </code>
///
/// becomes
///
/// <code>
/// void f$impl(Bencher b) { BODY }                 // attribute stripped
/// [Benchmark] void f() {                          // synthesized wrapper
///     var b = new Bencher();
///     f$impl(b);
///     b.printSummary("f");
/// }
/// </code>
///
/// The synthesized wrapper is indistinguishable from a hand-written zero-arg
/// benchmark, so it flows through the unchanged TypeChecker / validator /
/// IrGen / runner. The pass runs once, before TypeCheck, in
/// <c>PipelineCore.CheckAndGenerate</c> / <c>CheckOnly</c>.
///
/// <para>Trigger: a top-level <see cref="FunctionDecl"/> with a
/// <c>[Benchmark]</c> attribute whose sole parameter's type short-name is
/// <c>"Bencher"</c>. Zero-arg benchmarks pass through untouched (validator
/// accepts them); other signatures pass through untouched (validator still
/// emits E0912). Class-method benchmarks are out of scope for v1.</para>
///
/// <para><c>$</c> is illegal in z42 identifiers, so the <c>$impl</c> suffix
/// cannot collide with user code.</para>
/// </summary>
public static class BenchmarkDesugar
{
    private const string BenchmarkAttr = "Benchmark";
    private const string BencherType   = "Bencher";
    private const string ImplSuffix    = "$impl";

    /// <summary>Rewrite Bencher-arg benchmarks in <paramref name="cu"/>.
    /// Returns <paramref name="cu"/> unchanged when no such benchmark exists
    /// (common case — avoids rebuilding the function list).</summary>
    public static CompilationUnit Run(CompilationUnit cu)
    {
        // Fast path: scan for any match before allocating.
        bool any = false;
        foreach (var fn in cu.Functions)
        {
            if (IsBencherArgBenchmark(fn)) { any = true; break; }
        }
        if (!any) return cu;

        var rebuilt = new List<FunctionDecl>(cu.Functions.Count + 2);
        foreach (var fn in cu.Functions)
        {
            if (IsBencherArgBenchmark(fn))
            {
                rebuilt.Add(Demote(fn));
                rebuilt.Add(SynthesizeWrapper(fn));
            }
            else
            {
                rebuilt.Add(fn);
            }
        }
        return cu with { Functions = rebuilt };
    }

    /// <summary>True iff <paramref name="fn"/> carries <c>[Benchmark]</c> and
    /// has exactly one parameter of type <c>Bencher</c> (short-name match,
    /// shape-only — mirrors the validator's pre-typecheck name comparison).</summary>
    public static bool IsBencherArgBenchmark(FunctionDecl fn)
    {
        if (fn.TestAttributes is null) return false;
        bool hasBenchmark = false;
        foreach (var a in fn.TestAttributes)
        {
            if (a.Name == BenchmarkAttr) { hasBenchmark = true; break; }
        }
        if (!hasBenchmark) return false;
        if (fn.Params.Count != 1) return false;
        return fn.Params[0].Type is NamedType nt && nt.Name == BencherType;
    }

    /// <summary>Original body + param, attribute stripped, renamed to
    /// <c>&lt;name&gt;$impl</c>.</summary>
    private static FunctionDecl Demote(FunctionDecl fn)
        => fn with { Name = fn.Name + ImplSuffix, TestAttributes = null };

    /// <summary>Zero-arg <c>[Benchmark]</c> wrapper that constructs a Bencher,
    /// invokes the demoted impl, and reports via <c>printSummary</c>.</summary>
    private static FunctionDecl SynthesizeWrapper(FunctionDecl fn)
    {
        var s = default(Span); // synthetic glue — original spans live in $impl
        string name = fn.Name;
        string implName = name + ImplSuffix;

        // var b = new Bencher();
        var declB = new VarDeclStmt(
            "b",
            null,
            new NewExpr(new NamedType(BencherType, s), new List<Argument>(), s),
            s);

        // f$impl(b);
        var callImpl = new ExprStmt(
            new CallExpr(
                new IdentExpr(implName, s),
                new List<Argument> { new Argument(null, new IdentExpr("b", s), s) },
                s),
            s);

        // b.printSummary("f");
        var callSummary = new ExprStmt(
            new CallExpr(
                new MemberExpr(new IdentExpr("b", s), "printSummary", s),
                new List<Argument> { new Argument(null, new LitStrExpr(name, s), s) },
                s),
            s);

        var body = new BlockStmt(new List<Stmt> { declB, callImpl, callSummary }, s);

        return new FunctionDecl(
            Name:            name,
            Params:          new List<Param>(),
            ReturnType:      new VoidType(s),
            Body:            body,
            Visibility:      fn.Visibility,
            Modifiers:       fn.Modifiers,
            NativeIntrinsic: null,
            Span:            s,
            TestAttributes:  fn.TestAttributes); // carries [Benchmark]
    }
}
