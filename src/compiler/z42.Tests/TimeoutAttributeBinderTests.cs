using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.Semantics;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// add-test-timeout-attribute (2026-05-30) — covers `[Timeout(milliseconds:
/// <int>)]` validator + IrGen integration: every spec scenario from
/// `docs/spec/changes/add-test-timeout-attribute/specs/test-attributes/spec.md`.
public sealed class TimeoutAttributeBinderTests
{
    private static CompilationUnit ParseCu(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        return new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
    }

    private static (DiagnosticBag Diags, SemanticModel? Sem) Validate(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var cu = parser.ParseCompilationUnit();
        var diags = parser.Diagnostics;
        SemanticModel? sem = null;
        try { sem = new TypeChecker(diags).Check(cu, imported: null); }
        catch { }
        if (sem is not null) TestAttributeValidator.Validate(cu, sem, diags);
        return (diags, sem);
    }

    private const string ExceptionStub = """
        namespace Std;
        class Exception { public Exception(string msg) {} }
        class TestFailure : Exception { public TestFailure(string msg) : base(msg) {} }
        """;

    private const string BencherStub = """
        namespace Std.Test;
        class Bencher { public Bencher() {} }
        """;

    // ── Validator: happy path ─────────────────────────────────────────────

    [Fact]
    public void Validate_TestPlusTimeout_PassesValidation()
    {
        var (diags, _) = Validate(ExceptionStub + """
            [Test] [Timeout(milliseconds: 5000)] void f() {}
            """);
        diags.HasErrors.Should().BeFalse(because: string.Join("\n", diags.All));
    }

    [Fact]
    public void Validate_BenchmarkPlusTimeout_PassesValidation()
    {
        // add-benchmark-runner-dispatch (2026-05-31): [Benchmark] signature
        // is now `void f()` (was `void f(Bencher b)`).
        var (diags, _) = Validate(ExceptionStub + BencherStub + """
            using Std.Test;
            [Benchmark] [Timeout(milliseconds: 10000)] void f() {}
            """);
        diags.HasErrors.Should().BeFalse(because: string.Join("\n", diags.All));
    }

    // ── Validator: E0917 negative cases ────────────────────────────────────

    [Fact]
    public void Validate_TimeoutWithoutTestOrBenchmark_ReportsE0917()
    {
        var (diags, _) = Validate(ExceptionStub + """
            [Timeout(milliseconds: 1000)] void lonely() {}
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TimeoutValueInvalid &&
            d.Message.Contains("requires `[Test]` or `[Benchmark]`"));
    }

    [Fact]
    public void Validate_TimeoutMissingMillisecondsArg_ReportsE0917()
    {
        // Bare [Timeout] — no named-arg list at all.
        var (diags, _) = Validate(ExceptionStub + """
            [Test] [Timeout] void f() {}
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TimeoutValueInvalid &&
            d.Message.Contains("requires a single named arg"));
    }

    [Fact]
    public void Validate_TimeoutValueZero_ReportsE0917()
    {
        var (diags, _) = Validate(ExceptionStub + """
            [Test] [Timeout(milliseconds: 0)] void f() {}
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TimeoutValueInvalid &&
            d.Message.Contains("must be > 0"));
    }

    [Fact]
    public void Validate_TimeoutValueNegative_ReportsE0917()
    {
        var (diags, _) = Validate(ExceptionStub + """
            [Test] [Timeout(milliseconds: -1)] void f() {}
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TimeoutValueInvalid &&
            d.Message.Contains("must be > 0"));
    }

    [Fact]
    public void Validate_TimeoutValueAsString_ReportsE0917()
    {
        var (diags, _) = Validate(ExceptionStub + """
            [Test] [Timeout(milliseconds: "5000")] void f() {}
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TimeoutValueInvalid &&
            d.Message.Contains("must be an integer literal"));
    }

    [Fact]
    public void Validate_TimeoutAppliedTwice_ReportsE0917()
    {
        var (diags, _) = Validate(ExceptionStub + """
            [Test] [Timeout(milliseconds: 1000)] [Timeout(milliseconds: 2000)] void f() {}
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TimeoutValueInvalid &&
            d.Message.Contains("applied more than once"));
    }

    // ── IrGen: TestEntry.TimeoutMs population ──────────────────────────────

    [Fact]
    public void IrGen_TestPlusTimeout_PopulatesTimeoutMs()
    {
        var src = ExceptionStub + """
            [Test] [Timeout(milliseconds: 7500)] void f() {}
            """;
        var cu = ParseCu(src);
        var diags = new DiagnosticBag();
        var model = new TypeChecker(diags).Check(cu, imported: null);
        TestAttributeValidator.Validate(cu, model, diags);
        diags.HasErrors.Should().BeFalse(because: string.Join("\n", diags.All));

        var module = new IrGen(semanticModel: model).Generate(cu);
        var entry = module.TestIndex!.Single(e => e.Kind == TestEntryKind.Test);
        entry.TimeoutMs.Should().Be(7500);
    }

    [Fact]
    public void IrGen_TestWithoutTimeout_LeavesTimeoutMsAtZero()
    {
        var src = ExceptionStub + """
            [Test] void f() {}
            """;
        var cu = ParseCu(src);
        var diags = new DiagnosticBag();
        var model = new TypeChecker(diags).Check(cu, imported: null);
        TestAttributeValidator.Validate(cu, model, diags);
        diags.HasErrors.Should().BeFalse(because: string.Join("\n", diags.All));

        var module = new IrGen(semanticModel: model).Generate(cu);
        var entry = module.TestIndex!.Single(e => e.Kind == TestEntryKind.Test);
        entry.TimeoutMs.Should().Be(0, because: "no [Timeout] means runner uses default");
    }
}
