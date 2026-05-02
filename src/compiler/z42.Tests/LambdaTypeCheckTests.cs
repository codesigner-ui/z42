using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// TypeChecker unit tests for L2 lambda binding + L2 no-capture detection.
/// Pairs with archived `add-closures` Requirements R1, R2, R14 and impl spec
/// `IR-L4`, `IR-L5`. See docs/design/closure.md §4 + §10.
public sealed class LambdaTypeCheckTests
{
    private static (SemanticModel model, DiagnosticBag diags) Check(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        var model  = new TypeChecker(diags, LanguageFeatures.Phase1).Check(cu);
        return (model, diags);
    }

    // ── IR-L4: Type inference (positive cases) ────────────────────────────────

    [Fact]
    public void Lambda_ContextDriven_InfersParamTypes()
    {
        var (_, diags) = Check(
            "void Main() { (int) -> int f = x => x + 1; }");
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Lambda_ExplicitParamTypes_NoContextNeeded()
    {
        // Explicit `int x` allows binding even with `var`.
        var (_, diags) = Check(
            "void Main() { var f = (int x) => x + 1; }");
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Lambda_BlockBody()
    {
        var (_, diags) = Check(
            "void Main() { (int) -> int f = (int x) => { return x * 2; }; }");
        diags.HasErrors.Should().BeFalse();
    }

    // ── IR-L4: Type inference (failures) ─────────────────────────────────────

    [Fact]
    public void Lambda_VarAndUntypedParam_FailsToInfer()
    {
        // `var f = x => x + 1;` has neither expected type nor explicit param type.
        var (_, diags) = Check(
            "void Main() { var f = x => x + 1; }");
        diags.HasErrors.Should().BeTrue();
    }

    // ── IR-L5: capture (impl-closure-l3-core 解锁) ────────────────────────────

    [Fact]
    public void Lambda_CaptureOfOuterLocal_NowAllowed()
    {
        // L2 阶段曾报 Z0301 拒绝；L3 (impl-closure-l3-core) 解锁，捕获分析
        // 在 BindLambda 内自动记录到 BoundLambda.Captures（值类型走快照）。
        var (_, diags) = Check(
            "void Main() { var k = 10; (int) -> bool f = (int x) => x > k; }");
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Lambda_AllowsAccessToParamOnly()
    {
        var (_, diags) = Check(
            "void Main() { (int) -> int f = (int x) => x * x; }");
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Lambda_NestedLambdaInnerCapturesOuterParam_NowAllowed()
    {
        // `(int x) => { var g = (int y) => x + y; ...; }` — inner lambda
        // captures outer lambda's `x`. L3 accepts and lifts it through the
        // outer lambda's env (impl-closure-l3-core Decision 6).
        var (_, diags) = Check("""
            void Main() {
                (int) -> int f = (int x) => {
                    (int) -> int g = (int y) => x + y;
                    return g(x);
                };
            }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    // ── IR-L4: Func<T,R> / Action<T> equivalence ─────────────────────────────

    [Fact]
    public void FuncType_AndArrowType_AreEquivalent()
    {
        // 2026-05-02 D1c: hardcoded `Func`/`Action` desugar removed; this test
        // now uses a locally-declared delegate to verify named-delegate ↔
        // arrow-type structural equivalence (same as before, just w/o stdlib).
        var src = """
            public delegate int IntFn(int x);
            void Main() {
                IntFn a = (int x) => x + 1;
                (int) -> int b = a;
                IntFn c = b;
            }
            """;
        var (_, diags) = Check(src);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Action_DesugarsToVoidReturn()
    {
        // 2026-05-02 D1c: 本测试改名后续 follow-up；当前用本地 delegate 验证。
        var (_, diags) = Check("""
            public delegate void Log(int x);
            void Main() { Log log = (int x) => { }; }
            """);
        diags.HasErrors.Should().BeFalse();
    }
}
