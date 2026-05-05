using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;
using Z42.Semantics.TypeCheck;

namespace Z42.Tests;

/// Spec define-ref-out-in-parameters — TypeChecker scenario coverage.
/// Each test maps to one Scenario in spec/changes/define-ref-out-in-parameters/
/// specs/parameter-modifiers/spec.md.
public sealed class ParameterModifierTypeCheckTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DiagnosticBag Check(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        new TypeChecker(diags).Check(cu);
        return diags;
    }

    // ── ref ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Ref_BasicMutation_Compiles()
    {
        var src = @"
void Increment(ref int x) { x = x + 1; }
void Main() { var c = 0; Increment(ref c); }";
        Check(src).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Ref_CallsiteWithoutModifier_Errors()
    {
        var src = @"
void Increment(ref int x) { x = x + 1; }
void Main() { var c = 0; Increment(c); }";
        Check(src).HasErrors.Should().BeTrue(
            because: "callsite must explicitly write `ref` (修饰符一致性)");
    }

    [Fact]
    public void Ref_NonLvalueArg_Errors()
    {
        var src = @"
void Increment(ref int x) { x = x + 1; }
int Make() { return 42; }
void Main() { Increment(ref Make()); }";
        Check(src).HasErrors.Should().BeTrue(
            because: "ref arg must be lvalue, not function call result");
    }

    [Fact]
    public void Ref_LiteralArg_Errors()
    {
        var src = @"
void Increment(ref int x) { x = x + 1; }
void Main() { Increment(ref 42); }";
        Check(src).HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Ref_StrictTypeMatch_RejectsImplicitConversion()
    {
        var src = @"
void Foo(ref int x) { x = x + 1; }
void Main() { long n = 0; Foo(ref n); }";
        Check(src).HasErrors.Should().BeTrue(
            because: "ref boundary disallows long → int conversion");
    }

    // ── out ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Out_BasicTryParse_Compiles()
    {
        var src = @"
bool TryParse(string s, out int v) { v = 42; return true; }
void Main() { int n; TryParse(""x"", out n); }";
        Check(src).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Out_VarInlineDecl_Compiles()
    {
        var src = @"
bool TryParse(string s, out int v) { v = 42; return true; }
void Main() { TryParse(""x"", out var n); }";
        Check(src).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Out_CalleeMustAssign_NormalReturn()
    {
        var src = @"
void Foo(out int x) { return; }
void Main() { }";
        Check(src).HasErrors.Should().BeTrue(
            because: "callee normal-return path must assign out param");
    }

    [Fact]
    public void Out_CallerPostCall_TreatsTargetAsAssigned()
    {
        var src = @"
bool TryParse(string s, out int v) { v = 1; return true; }
void Main() { int n; TryParse(""x"", out n); var dup = n; }";
        // After call, `n` is initialized — `var dup = n;` should not error
        // on uninitialized read.
        Check(src).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Out_ThrowPath_DoesNotRequireAssignment()
    {
        var src = @"
class MyErr { MyErr() { } }
void Foo(out int x) { if (true) { x = 1; } else { throw new MyErr(); } }
void Main() { }";
        // Throw path replaces the assignment requirement on its branch.
        Check(src).HasErrors.Should().BeFalse();
    }

    // ── in ────────────────────────────────────────────────────────────────────

    [Fact]
    public void In_ReadOnly_Compiles()
    {
        var src = @"
double Norm(in double x) { return x * x; }
void Main() { var v = 3.0; var r = Norm(in v); }";
        Check(src).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void In_WriteForbidden_Errors()
    {
        var src = @"
void Foo(in int x) { x = 5; }
void Main() { }";
        Check(src).HasErrors.Should().BeTrue(
            because: "in parameter is read-only, write must error");
    }

    [Fact]
    public void In_CallsiteRequiresExplicitModifier()
    {
        var src = @"
double Norm(in double x) { return x * x; }
void Main() { var v = 3.0; var r = Norm(v); }";
        Check(src).HasErrors.Should().BeTrue(
            because: "z42 修正 C# `in` 可省，要求 callsite 强制写");
    }

    // ── Modifier mismatch ─────────────────────────────────────────────────────

    [Fact]
    public void Modifier_RefVsOut_Mismatch_Errors()
    {
        var src = @"
void Foo(ref int x) { x = 1; }
void Main() { var c = 0; Foo(out c); }";
        Check(src).HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Modifier_InOnPlainParam_Errors()
    {
        var src = @"
void Foo(int x) { }
void Main() { var c = 0; Foo(in c); }";
        Check(src).HasErrors.Should().BeTrue();
    }

    // ── Modifier overload (Decision 7) ────────────────────────────────────────

    [Fact]
    public void Overload_RefVsValue_DistinctSelections()
    {
        var src = @"
void Foo(int x) { }
void Foo(ref int x) { x = x + 1; }
void Main() {
    var a = 0;
    Foo(a);            // by-value variant
    Foo(ref a);        // by-ref variant
}";
        Check(src).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Overload_OnlyRefDeclared_PlainCallErrors()
    {
        var src = @"
void Foo(ref int x) { x = x + 1; }
void Main() { var c = 0; Foo(c); }";
        Check(src).HasErrors.Should().BeTrue(
            because: "only ref overload exists; plain call missing modifier");
    }

    // ── Lambda capture restriction ────────────────────────────────────────────

    [Fact]
    public void Lambda_CannotCapture_RefParam()
    {
        var src = @"
void Foo(ref int x) {
    var f = () => x + 1;
}
void Main() { }";
        Check(src).HasErrors.Should().BeTrue(
            because: "lambda may not capture ref parameter");
    }

    [Fact]
    public void Lambda_CannotCapture_OutParam()
    {
        var src = @"
void Foo(out int x) {
    x = 0;
    var f = () => x;
}
void Main() { }";
        Check(src).HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Lambda_CanCapture_PlainParam()
    {
        var src = @"
void Foo(int x) {
    var f = () => x + 1;
}
void Main() { }";
        Check(src).HasErrors.Should().BeFalse();
    }
}
