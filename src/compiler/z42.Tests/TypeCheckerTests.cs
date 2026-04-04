using FluentAssertions;
using Z42.Compiler.Diagnostics;
using Z42.Compiler.Features;
using Z42.Compiler.Lexer;
using Z42.Compiler.Parser;
using Z42.Compiler.TypeCheck;

namespace Z42.Tests;

/// Unit tests for the TypeChecker: verify that valid programs produce no diagnostics
/// and that ill-typed programs produce the expected error codes.
public sealed class TypeCheckerTests
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
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    // ── Duplicate declarations ────────────────────────────────────────────────

    [Fact]
    public void DuplicateVarDecl_SameScope_ReportsError()
    {
        var diags = CheckStmts("var x = 1; var x = 2;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
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
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void DuplicateFunction_ReportsError()
    {
        var diags = Check("void Foo() {} void Foo() {}");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void DuplicateParam_ReportsError()
    {
        var diags = Check("int Add(int x, int x) { return x; }");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
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
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
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
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.TypeMismatch);
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

    // ── Integer literal range checking ────────────────────────────────────────

    [Fact]
    public void I8_LiteralInRange_NoError()
    {
        CheckStmts("i8 x = 127;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void I8_LiteralMinBound_NoError()
    {
        CheckStmts("i8 x = -128;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void I8_LiteralOverflow_ReportsError()
    {
        var diags = CheckStmts("i8 x = 128;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IntLiteralOutOfRange);
    }

    [Fact]
    public void I8_LiteralUnderflow_ReportsError()
    {
        var diags = CheckStmts("i8 x = -129;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IntLiteralOutOfRange);
    }

    [Fact]
    public void U8_LiteralInRange_NoError()
    {
        CheckStmts("u8 x = 255;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void U8_LiteralOverflow_ReportsError()
    {
        var diags = CheckStmts("u8 x = 256;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IntLiteralOutOfRange);
    }

    [Fact]
    public void U8_NegativeLiteral_ReportsError()
    {
        var diags = CheckStmts("u8 x = -1;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IntLiteralOutOfRange);
    }

    [Fact]
    public void I16_LiteralInRange_NoError()
    {
        CheckStmts("i16 x = 32767;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void I16_LiteralOverflow_ReportsError()
    {
        var diags = CheckStmts("i16 x = 32768;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IntLiteralOutOfRange);
    }

    [Fact]
    public void U32_LiteralInRange_NoError()
    {
        CheckStmts("u32 x = 4294967295;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void U32_LiteralOverflow_ReportsError()
    {
        // 4294967296 = uint.MaxValue + 1; stored as long in AST, fits in long but not u32
        var diags = CheckStmts("u32 x = 4294967296;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IntLiteralOutOfRange);
    }

    [Fact]
    public void Int_LargePositiveLiteral_TreatedAsLong_RequiresLong()
    {
        // 2147483648 > int.MaxValue → literal typed as Long → int target → TypeMismatch via range check
        var diags = CheckStmts("int x = 2147483648;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.IntLiteralOutOfRange || d.Code == DiagnosticCodes.TypeMismatch);
    }

    [Fact]
    public void I8_AssignLiteralInRange_NoError()
    {
        CheckStmts("i8 x = 0; x = 50;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void I8_AssignLiteralOverflow_ReportsError()
    {
        var diags = CheckStmts("i8 x = 0; x = 200;");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IntLiteralOutOfRange);
    }

    // ── Default parameter values ──────────────────────────────────────────────

    [Fact]
    public void DefaultParam_DeclarationAndCall_NoErrors()
    {
        Check("void Greet(string name, string greeting = \"Hello\") {} void Main() { Greet(\"Alice\"); }")
            .HasErrors.Should().BeFalse();
    }

    [Fact]
    public void DefaultParam_ExplicitOverride_NoErrors()
    {
        Check("void Greet(string name, string greeting = \"Hello\") {} void Main() { Greet(\"Alice\", \"Hi\"); }")
            .HasErrors.Should().BeFalse();
    }

    [Fact]
    public void DefaultParam_AllDefaults_NoErrors()
    {
        Check("void Reset(int x = 0, int y = 0) {} void Main() { Reset(); }")
            .HasErrors.Should().BeFalse();
    }

    [Fact]
    public void DefaultParam_MissingRequired_ReportsError()
    {
        Check("void Greet(string name, string greeting = \"Hello\") {} void Main() { Greet(); }")
            .HasErrors.Should().BeTrue();
    }

    [Fact]
    public void DefaultParam_WrongDefaultType_ReportsError()
    {
        Check("void Foo(int n = \"bad\") {}")
            .HasErrors.Should().BeTrue();
    }

    [Fact]
    public void DefaultParam_NullableParam_NoErrors()
    {
        Check("void Foo(int x, string? label = null) {} void Main() { Foo(1); }")
            .HasErrors.Should().BeFalse();
    }

    // ── C# type aliases ───────────────────────────────────────────────────────

    [Fact]
    public void SbyteAlias_LiteralAssignment_NoErrors()
    {
        CheckStmts("sbyte sb = -128;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void ShortAlias_LiteralAssignment_NoErrors()
    {
        CheckStmts("short sh = 32000;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void ByteAlias_LiteralAssignment_NoErrors()
    {
        CheckStmts("byte b = 255;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void FloatLiteral_TypeIsFloat_NoErrors()
    {
        CheckStmts("float f = 1.5f;").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void NullableString_StringAssignment_NoErrors()
    {
        CheckStmts("string? s = \"hello\";").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void NullCoalesce_UnwrapsOptional()
    {
        // string? ?? string → string; assigning to string must succeed
        CheckStmts("string? opt = null; string s = opt ?? \"default\";")
            .HasErrors.Should().BeFalse();
    }
}
