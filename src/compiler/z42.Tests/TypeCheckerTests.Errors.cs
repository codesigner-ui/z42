using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;
using Z42.Semantics.TypeCheck;

namespace Z42.Tests;

public partial class TypeCheckerTests
{
    // ── Undefined symbol ──────────────────────────────────────────────────────

    [Fact]
    public void UndefinedVariable_ReportsError()
    {
        var diags = CheckStmts("var x = y + 1;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.UndefinedSymbol);
    }

    [Fact]
    public void UndefinedFunction_ReportsError()
    {
        var diags = CheckStmts("var x = Missing(1);");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.UndefinedSymbol);
    }

    // ── Type mismatch ─────────────────────────────────────────────────────────

    [Fact]
    public void TypeMismatch_AssignStringToInt_ReportsError()
    {
        var diags = CheckStmts("int x = \"hello\";");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void TypeMismatch_AssignBoolToInt_ReportsError()
    {
        var diags = CheckStmts("int x = true;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void TypeMismatch_NonBoolIfCondition_ReportsError()
    {
        var diags = CheckStmts("if (42) {}");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void TypeMismatch_NonBoolWhileCondition_ReportsError()
    {
        var diags = CheckStmts("while (1) {}");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void TypeMismatch_ArithmeticOnBool_ReportsError()
    {
        var diags = CheckExpr("true + 1");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void TypeMismatch_LogicalOnInt_ReportsError()
    {
        var diags = CheckExpr("1 && 2");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void TypeMismatch_PostfixOnString_ReportsError()
    {
        var diags = CheckStmts("string s = \"x\"; s++;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void TypeMismatch_TernaryWithNonBoolCond_ReportsError()
    {
        var diags = CheckExpr("42 ? 1 : 2");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    // ── Return type mismatch ──────────────────────────────────────────────────

    [Fact]
    public void ReturnType_WrongType_ReportsError()
    {
        var diags = Check("int Foo() { return \"hello\"; }");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void ReturnType_MissingValue_ReportsError()
    {
        var diags = Check("int Foo() { return; }");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void ReturnVoid_FromVoidFunction_NoError()
    {
        var diags = Check("void Foo() { return; }");
        diags.HasErrors.Should().BeFalse();
    }

    // ── Function call arity ───────────────────────────────────────────────────

    [Fact]
    public void FunctionCall_TooFewArgs_ReportsError()
    {
        var diags = Check("int Add(int a, int b) { return a + b; } void Main() { var r = Add(1); }");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void FunctionCall_TooManyArgs_ReportsError()
    {
        var diags = Check("int Noop() { return 0; } void Main() { var r = Noop(1, 2); }");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    // ── Array type mismatch ───────────────────────────────────────────────────

    [Fact]
    public void ArrayLiteral_WrongElementType_ReportsError()
    {
        var diags = CheckStmts("int[] arr = new int[] { 1, \"hello\", 3 };");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    // ── VoidAssignment ────────────────────────────────────────────────────────

    [Fact]
    public void AssignVoidToVar_ReportsError()
    {
        var diags = Check("void Noop() {} void Main() { var x = Noop(); }");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.VoidAssignment);
    }

    // ── Duplicate declarations ────────────────────────────────────────────────

    [Fact]
    public void DuplicateVarDecl_SameScope_ReportsError()
    {
        var diags = CheckStmts("var x = 1; var x = 2;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.DuplicateDeclaration);
    }

    [Fact]
    public void DuplicateVarDecl_DifferentScopes_NoError()
    {
        // Re-declaring in an inner scope is allowed (shadowing)
        var diags = CheckStmts("var x = 1; if (true) { var x = 2; }");
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void DuplicateClass_ReportsError()
    {
        var diags = Check("class Foo {} class Foo {} void Main() {}");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.DuplicateDeclaration);
    }

    [Fact]
    public void DuplicateFunction_ReportsError()
    {
        var diags = Check("void Foo() {} void Foo() {}");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.DuplicateDeclaration);
    }

    [Fact]
    public void DuplicateParam_ReportsError()
    {
        var diags = Check("int Add(int x, int x) { return x; }");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.DuplicateDeclaration);
    }

    // ── Interface implementation ──────────────────────────────────────────────

    [Fact]
    public void InterfaceNotImplemented_ReportsError()
    {
        var diags = Check(
            "interface IFoo { int Value(); }" +
            "class Bar : IFoo {}" +
            "void Main() {}");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.InterfaceMismatch);
    }

    [Fact]
    public void InterfaceFullyImplemented_NoError()
    {
        var diags = Check(
            "interface IFoo { int Value(); }" +
            "class Bar : IFoo { int Value() { return 42; } }" +
            "void Main() {}");
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void InterfaceWrongReturnType_ReportsError()
    {
        var diags = Check(
            "interface IFoo { int Value(); }" +
            "class Bar : IFoo { string Value() { return \"x\"; } }" +
            "void Main() {}");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.InterfaceMismatch);
    }

    // ── Function call argument types ──────────────────────────────────────────

    [Fact]
    public void FunctionCall_WrongArgType_ReportsError()
    {
        var diags = Check("void Greet(int n) {} void Main() { Greet(\"hello\"); }");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void FunctionCall_CorrectArgType_NoError()
    {
        var diags = Check("void Greet(int n) {} void Main() { Greet(42); }");
        diags.HasErrors.Should().BeFalse();
    }

}
