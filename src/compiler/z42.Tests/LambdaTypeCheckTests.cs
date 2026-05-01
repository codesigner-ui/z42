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

    // ── IR-L5: L2 no-capture check ────────────────────────────────────────────

    [Fact]
    public void Lambda_RejectsCaptureOfOuterLocal()
    {
        var (_, diags) = Check(
            "void Main() { var k = 10; (int) -> bool f = (int x) => x > k; }");
        diags.HasErrors.Should().BeTrue();
        // Error should call out the captured name.
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.FeatureDisabled
            && d.Message.Contains("capture")
            && d.Message.Contains("k"));
    }

    [Fact]
    public void Lambda_AllowsAccessToParamOnly()
    {
        var (_, diags) = Check(
            "void Main() { (int) -> int f = (int x) => x * x; }");
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Lambda_NestedLambdaInner_RejectsOuterParamCapture()
    {
        // `(int x) => (int y) => x + y` — inner lambda captures outer's `x`.
        // L2 rejects this; L3 will accept.
        var (_, diags) = Check(
            "void Main() { ((int) -> int) f = (int x) => { (int) -> int g = (int y) => x + y; return g(x); }; }");
        diags.HasErrors.Should().BeTrue();
    }

    // ── IR-L4: Func<T,R> / Action<T> equivalence ─────────────────────────────

    [Fact]
    public void FuncType_AndArrowType_AreEquivalent()
    {
        // `Func<int, int>` and `(int) -> int` desugar to the same Z42FuncType,
        // so cross-assigning between them is allowed (Decision 9).
        var src = """
            void Main() {
                Func<int, int> a = (int x) => x + 1;
                (int) -> int   b = a;
                Func<int, int> c = b;
            }
            """;
        var (_, diags) = Check(src);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Action_DesugarsToVoidReturn()
    {
        // `Action<int>` is `(int) -> void`. Assigning a void-returning block
        // lambda must be accepted; a value-returning lambda would mismatch.
        var (_, diags) = Check(
            "void Main() { Action<int> log = (int x) => { }; }");
        diags.HasErrors.Should().BeFalse();
    }
}
