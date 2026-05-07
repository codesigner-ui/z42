using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;
using Z42.Semantics.TypeCheck;

namespace Z42.Tests;

/// Unit tests for the TypeChecker: verify that valid programs produce no diagnostics
/// and that ill-typed programs produce the expected error codes.
public sealed partial class TypeCheckerTests
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

    /// Wraps statements inside a void Main().
    private static DiagnosticBag CheckStmts(string stmts)
        => Check($"void Main() {{ {stmts} }}");

    /// Wraps an expression as `var _x = <expr>;` inside void Main().
    private static DiagnosticBag CheckExpr(string expr)
        => CheckStmts($"var _x = {expr};");

    // ── Valid programs ────────────────────────────────────────────────────────

    [Fact]
    public void EmptyFunction_NoErrors()
    {
        Check("void Main() {}").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void VarInference_Int_NoErrors()
    {
        CheckStmts("var x = 42;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void VarInference_String_NoErrors()
    {
        CheckStmts("var s = \"hello\";").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void ExplicitTypeAnnotation_NoErrors()
    {
        CheckStmts("int x = 42;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void NumericWidening_IntToLong_NoErrors()
    {
        CheckStmts("long x = 42;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void NumericWidening_IntToDouble_NoErrors()
    {
        CheckStmts("double x = 1;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void ReturnInt_FromIntFunction_NoErrors()
    {
        Check("int Add(int a, int b) { return a + b; }").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void VoidReturn_FromVoidFunction_NoErrors()
    {
        Check("void Noop() { return; }").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void StringConcat_WithPlus_NoErrors()
    {
        CheckStmts("var s = \"hello\" + \" world\";").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void StringConcat_IntAndString_NoErrors()
    {
        CheckStmts("var s = 42 + \" suffix\";").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void BoolLiteral_Assignment_NoErrors()
    {
        CheckStmts("bool b = true;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void IfCondition_Bool_NoErrors()
    {
        CheckStmts("if (true) {}").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void WhileCondition_Bool_NoErrors()
    {
        CheckStmts("while (false) {}").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void ArrayCreate_NoErrors()
    {
        CheckStmts("int[] arr = new int[5];").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void ArrayLiteral_NoErrors()
    {
        CheckStmts("int[] arr = new int[] { 1, 2, 3 };").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void ArrayLength_IsInt_NoErrors()
    {
        CheckStmts("int[] arr = new int[3]; int n = arr.Length;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void FunctionCall_CorrectArity_NoErrors()
    {
        Check("int Add(int a, int b) { return a + b; } void Main() { var r = Add(1, 2); }")
            .HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Foreach_OnArray_NoErrors()
    {
        Check("void Main() { int[] arr = new int[3]; foreach (var x in arr) {} }")
            .HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Ternary_WithBoolCondition_NoErrors()
    {
        CheckStmts("var x = true ? 1 : 2;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void LogicalAnd_Bools_NoErrors()
    {
        CheckStmts("bool r = true && false;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void NullAssignToString_NoErrors()
    {
        CheckStmts("string s = null;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void StringSubstring_NoErrors()
    {
        Check("void Main() { string s = \"hello\"; var r = s.Substring(1, 3); }")
            .HasErrors.Should().BeFalse();
    }

    [Fact]
    public void StringContains_NoErrors()
    {
        Check("void Main() { string s = \"hello\"; bool b = s.Contains(\"el\"); }")
            .HasErrors.Should().BeFalse();
    }

}
