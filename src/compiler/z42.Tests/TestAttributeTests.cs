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

/// Spec R4.A + R4.B — z42.test attribute parsing, validation, and TIDX emission.
///
/// Parser cases verify the syntactic surface (including R4.B `[Name<TypeArg>]`
/// form). Validator cases cover E0911 / E0913 / E0914 / E0915. Round-trip case
/// confirms `[ShouldThrow<E>]` reaches the IrGen TIDX emission path.
public sealed class TestAttributeTests
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static CompilationUnit ParseCu(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        return new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
    }

    private static (CompilationUnit Cu, DiagnosticBag Diags) ParseWithDiags(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var cu = parser.ParseCompilationUnit();
        return (cu, parser.Diagnostics);
    }

    private static (DiagnosticBag Diags, SemanticModel? Sem) Validate(string src)
    {
        var cu = ParseCu(src);
        var diags = new DiagnosticBag();
        SemanticModel? sem = null;
        try
        {
            sem = new TypeChecker(diags).Check(cu, imported: null);
        }
        catch
        {
            // TypeChecker may throw on malformed input; let validator run
            // against whatever survived (which mostly happens for shape-only
            // tests where parser succeeded).
        }
        if (sem is not null)
            TestAttributeValidator.Validate(cu, sem, diags);
        return (diags, sem);
    }

    private const string ExceptionStub = """
        namespace Std;
        class Exception {
            public Exception(string msg) {}
        }
        class TestFailure : Exception {
            public TestFailure(string msg) : base(msg) {}
        }
        class SkipSignal : Exception {
            public SkipSignal(string msg) : base(msg) {}
        }
        """;

    // ── Parser: basic accept ───────────────────────────────────────────────────

    [Fact]
    public void Parse_ShouldThrowWithTypeArg_PopulatesTypeArgField()
    {
        const string src = """
            [Test]
            [ShouldThrow<TestFailure>]
            void f() {}
            """;
        var fn = ParseCu(src).Functions.Single();
        fn.TestAttributes.Should().HaveCount(2);
        var st = fn.TestAttributes!.Single(a => a.Name == "ShouldThrow");
        st.TypeArg.Should().Be("TestFailure");
        st.NamedArgs.Should().BeNull();
    }

    [Fact]
    public void Parse_BareAttribute_TypeArgIsNull()
    {
        const string src = "[Test] void f() {}";
        var fn = ParseCu(src).Functions.Single();
        var t = fn.TestAttributes!.Single(a => a.Name == "Test");
        t.TypeArg.Should().BeNull();
    }

    [Fact]
    public void Parse_ShouldThrowWithoutTypeArg_TypeArgIsNull()
    {
        // Bare `[ShouldThrow]` parses; validator (E0913) catches the missing arg.
        const string src = "[Test] [ShouldThrow] void f() {}";
        var fn = ParseCu(src).Functions.Single();
        var st = fn.TestAttributes!.Single(a => a.Name == "ShouldThrow");
        st.TypeArg.Should().BeNull();
    }

    // ── Parser: reject malformed type-arg ──────────────────────────────────────

    [Fact]
    public void Parse_EmptyTypeArg_RaisesDiagnostic()
    {
        // Parser catches ParseException and routes to DiagnosticBag; the test
        // confirms the error was reported, not the throw mechanism.
        var (_, diags) = ParseWithDiags("[Test] [ShouldThrow<>] void f() {}");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Message.Contains("requires a type identifier"));
    }

    [Fact]
    public void Parse_MultiTypeArg_RaisesDiagnostic()
    {
        var (_, diags) = ParseWithDiags("[Test] [ShouldThrow<A, B>] void f() {}");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Message.Contains("single type parameter"));
    }

    [Fact]
    public void Parse_NestedGeneric_RaisesDiagnostic()
    {
        var (_, diags) = ParseWithDiags("[Test] [ShouldThrow<List<int>>] void f() {}");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Message.Contains("nested generic"));
    }

    // ── Validator: E0913 ShouldThrow type checks ───────────────────────────────

    [Fact]
    public void Validate_ShouldThrowWithoutTypeArg_ReportsE0913()
    {
        var (diags, _) = Validate(ExceptionStub + """
            [Test] [ShouldThrow] void f() {}
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.ShouldThrowTypeInvalid &&
            d.Message.Contains("requires a single type argument"));
    }

    [Fact]
    public void Validate_ShouldThrowUnknownType_ReportsE0913()
    {
        var (diags, _) = Validate(ExceptionStub + """
            [Test] [ShouldThrow<NotAType>] void f() {}
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.ShouldThrowTypeInvalid &&
            d.Message.Contains("unknown type `NotAType`"));
    }

    [Fact]
    public void Validate_ShouldThrowNonExceptionType_ReportsE0913()
    {
        // `Foo` exists but does not derive from Exception.
        var (diags, _) = Validate(ExceptionStub + """
            class Foo {}
            [Test] [ShouldThrow<Foo>] void f() {}
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.ShouldThrowTypeInvalid &&
            d.Message.Contains("must derive from `Exception`"));
    }

    [Fact]
    public void Validate_ShouldThrowWithoutPrimary_ReportsE0914()
    {
        var (diags, _) = Validate(ExceptionStub + """
            [ShouldThrow<TestFailure>] void f() {}
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.SkipReasonMissing &&
            d.Message.Contains("[ShouldThrow]"));
    }

    [Fact]
    public void Validate_TypeArgOnNonShouldThrowAttribute_ReportsE0913()
    {
        var (diags, _) = Validate(ExceptionStub + """
            [Test<TestFailure>] void f() {}
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.ShouldThrowTypeInvalid &&
            d.Message.Contains("does not accept a type argument"));
    }

    [Fact]
    public void Validate_TestPlusShouldThrowFailure_PassesValidation()
    {
        var (diags, _) = Validate(ExceptionStub + """
            [Test] [ShouldThrow<TestFailure>] void f() {}
            """);
        diags.HasErrors.Should().BeFalse(because: string.Join("\n", diags.All));
    }

    [Fact]
    public void Validate_TransitiveExceptionDescendant_PassesValidation()
    {
        // SkipSignal extends Exception (one hop); ChildSkip extends SkipSignal.
        var (diags, _) = Validate(ExceptionStub + """
            class ChildSkip : SkipSignal {
                public ChildSkip(string msg) : base(msg) {}
            }
            [Test] [ShouldThrow<ChildSkip>] void f() {}
            """);
        diags.HasErrors.Should().BeFalse(because: string.Join("\n", diags.All));
    }

    // ── IrGen → TIDX round-trip ────────────────────────────────────────────────

    [Fact]
    public void IrGen_ShouldThrow_PopulatesExpectedThrowTypeIdxAndFlag()
    {
        var src = ExceptionStub + """
            [Test] [ShouldThrow<TestFailure>] void f() {}
            """;
        var cu = ParseCu(src);
        var diags = new DiagnosticBag();
        var model = new TypeChecker(diags).Check(cu, imported: null);
        TestAttributeValidator.Validate(cu, model, diags);
        diags.HasErrors.Should().BeFalse(because: string.Join("\n", diags.All));

        var module = new IrGen(semanticModel: model).Generate(cu);
        var entry = module.TestIndex!.Single(e => e.Kind == TestEntryKind.Test);
        entry.ExpectedThrowTypeIdx.Should().BeGreaterThan(0,
            because: "[ShouldThrow<TestFailure>] should set 1-based string-pool index");
        entry.Flags.HasFlag(TestFlags.ShouldThrow).Should().BeTrue();

        // A3 — IrGen emits the user-written type plus its DESCENDANTS as a
        // `;`-delimited chain. TestFailure has no subclasses in this CU, so
        // the chain is just ["TestFailure"].
        var actual = module.StringPool[entry.ExpectedThrowTypeIdx - 1];
        actual.Split(';').Should().Equal(new[] { "TestFailure" });
    }

    [Fact]
    public void IrGen_ShouldThrow_DescendantsIncluded()
    {
        // [ShouldThrow<Exception>] + this CU declares two Exception subclasses
        // (TestFailure, SkipSignal). Chain must contain Exception + both subs.
        var src = ExceptionStub + """
            [Test] [ShouldThrow<Exception>] void f() {}
            """;
        var cu = ParseCu(src);
        var diags = new DiagnosticBag();
        var model = new TypeChecker(diags).Check(cu, imported: null);
        TestAttributeValidator.Validate(cu, model, diags);
        diags.HasErrors.Should().BeFalse(because: string.Join("\n", diags.All));

        var module = new IrGen(semanticModel: model).Generate(cu);
        var entry = module.TestIndex!.Single(e => e.Kind == TestEntryKind.Test);
        var actual = module.StringPool[entry.ExpectedThrowTypeIdx - 1];
        var entries = actual.Split(';');
        entries[0].Should().Be("Exception", because: "head is always the user-written type");
        entries.Should().Contain("TestFailure");
        entries.Should().Contain("SkipSignal");
    }

    [Fact]
    public void IrGen_ShouldThrow_TransitiveDescendantsIncluded()
    {
        // GrandChild : Child : TestFailure : Exception.
        // [ShouldThrow<TestFailure>] should match Child and GrandChild too.
        var src = ExceptionStub + """
            class Child : TestFailure {
                public Child(string m) : base(m) {}
            }
            class GrandChild : Child {
                public GrandChild(string m) : base(m) {}
            }
            [Test] [ShouldThrow<TestFailure>] void f() {}
            """;
        var cu = ParseCu(src);
        var diags = new DiagnosticBag();
        var model = new TypeChecker(diags).Check(cu, imported: null);
        TestAttributeValidator.Validate(cu, model, diags);
        diags.HasErrors.Should().BeFalse(because: string.Join("\n", diags.All));

        var module = new IrGen(semanticModel: model).Generate(cu);
        var entry = module.TestIndex!.Single(e => e.Kind == TestEntryKind.Test);
        var entries = module.StringPool[entry.ExpectedThrowTypeIdx - 1].Split(';');
        entries.Should().Contain(new[] { "TestFailure", "Child", "GrandChild" });
        entries.Should().NotContain("SkipSignal", because: "siblings of TestFailure must not appear");
    }

    [Fact]
    public void IrGen_NoShouldThrow_LeavesExpectedThrowTypeIdxZero()
    {
        var cu = ParseCu("[Test] void f() {}");
        var diags = new DiagnosticBag();
        var model = new TypeChecker(diags).Check(cu, imported: null);
        TestAttributeValidator.Validate(cu, model, diags);
        var module = new IrGen(semanticModel: model).Generate(cu);
        var entry = module.TestIndex!.Single();
        entry.ExpectedThrowTypeIdx.Should().Be(0);
        entry.Flags.HasFlag(TestFlags.ShouldThrow).Should().BeFalse();
    }
}
