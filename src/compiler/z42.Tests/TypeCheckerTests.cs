using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;
using Z42.Semantics.TypeCheck;

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

    // ── L3-G2.5 constructor constraint ───────────────────────────────────────

    // `where T: new()` accepts a class with an explicit no-arg constructor.
    [Fact]
    public void CtorConstraint_ClassWithNoArgCtor_Passes()
    {
        var src = @"
class Factory<T> where T: new() { T t; Factory() { this.t = null; } }
class Widget { int x; Widget() { this.x = 0; } }
void Main() { var f = new Factory<Widget>(); }";
        Check(src).HasErrors.Should().BeFalse();
    }

    // `where T: new()` rejects a class whose only constructor takes args.
    [Fact]
    public void CtorConstraint_ClassWithoutNoArgCtor_Rejected()
    {
        var src = @"
class Factory<T> where T: new() { T t; Factory() { this.t = null; } }
class NeedsArg { int x; NeedsArg(int v) { this.x = v; } }
void Main() { var f = new Factory<NeedsArg>(); }";
        var diags = Check(src);
        diags.HasErrors.Should().BeTrue(because: "NeedsArg has no parameterless ctor");
    }

    // Primitives are always default-constructible → accepted.
    [Fact]
    public void CtorConstraint_Primitive_Passes()
    {
        var src = @"
class Factory<T> where T: new() { T t; Factory() { this.t = null; } }
void Main() { var f = new Factory<int>(); }";
        Check(src).HasErrors.Should().BeFalse();
    }

    // Interfaces are not default-constructible → rejected.
    [Fact]
    public void CtorConstraint_Interface_Rejected()
    {
        var src = @"
interface IShape { int Area(); }
class Factory<T> where T: new() { T t; Factory() { this.t = null; } }
void Main() { var f = new Factory<IShape>(); }";
        var diags = Check(src);
        diags.HasErrors.Should().BeTrue(because: "IShape interface is not instantiable");
    }

    // Combined with class constraint: `where T: class + new()` — both must hold.
    [Fact]
    public void CtorConstraint_Combined_ClassAndCtor_Passes()
    {
        var src = @"
class Factory<T> where T: class + new() { T t; Factory() { this.t = null; } }
class Widget { int x; Widget() { this.x = 0; } }
void Main() { var f = new Factory<Widget>(); }";
        Check(src).HasErrors.Should().BeFalse();
    }

    // ── L3-G2.5 enum constraint ───────────────────────────────────────────────

    // `where T: enum` accepts user-defined enum types.
    [Fact]
    public void EnumConstraint_EnumArg_Passes()
    {
        var src = @"
enum Color { Red, Green, Blue }
class Parser<T> where T: enum { Parser() { } }
void Main() { var p = new Parser<Color>(); }";
        Check(src).HasErrors.Should().BeFalse();
    }

    // `where T: enum` rejects class types.
    [Fact]
    public void EnumConstraint_ClassArg_Rejected()
    {
        var src = @"
class Widget { int x; Widget() { this.x = 0; } }
class Parser<T> where T: enum { Parser() { } }
void Main() { var p = new Parser<Widget>(); }";
        Check(src).HasErrors.Should().BeTrue(because: "Widget is a class, not an enum");
    }

    // `where T: enum` rejects struct types.
    [Fact]
    public void EnumConstraint_StructArg_Rejected()
    {
        var src = @"
struct Point { int x; int y; Point() { this.x = 0; this.y = 0; } }
class Parser<T> where T: enum { Parser() { } }
void Main() { var p = new Parser<Point>(); }";
        Check(src).HasErrors.Should().BeTrue(because: "Point is a struct, not an enum");
    }

    // `where T: enum` rejects primitive types.
    [Fact]
    public void EnumConstraint_Primitive_Rejected()
    {
        var src = @"
class Parser<T> where T: enum { Parser() { } }
void Main() { var p = new Parser<int>(); }";
        Check(src).HasErrors.Should().BeTrue(because: "int is primitive, not an enum");
    }

    // `where T: enum` rejects interface types.
    [Fact]
    public void EnumConstraint_Interface_Rejected()
    {
        var src = @"
interface IShape { int Area(); }
class Parser<T> where T: enum { Parser() { } }
void Main() { var p = new Parser<IShape>(); }";
        Check(src).HasErrors.Should().BeTrue(because: "IShape is an interface, not an enum");
    }

    // Combined with interface constraint: `where T: enum + IComparable<T>` — both must hold.
    // Enum currently cannot implement interfaces (no-op: interface part is trivially consumed
    // once enums gain IComparable via L3-R); for now the enum arm alone fires.
    [Fact]
    public void EnumConstraint_ClassAndEnum_Mutex_Rejected()
    {
        var src = @"
enum Color { Red, Green, Blue }
class Parser<T> where T: class + enum { Parser() { } }
void Main() { var p = new Parser<Color>(); }";
        Check(src).HasErrors.Should().BeTrue(because: "class and enum are mutually exclusive");
    }

    // ── L3 extern impl (Change 1) ─────────────────────────────────────────────

    [Fact]
    public void ImplBlock_Basic_Passes()
    {
        var src = @"
interface IGreet { string Hello(); }
class Foo { int x; Foo() { this.x = 1; } }
impl IGreet for Foo {
    public string Hello() { return ""hi""; }
}
void Main() { var f = new Foo(); }";
        Check(src).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void ImplBlock_SatisfiesInterfaceConstraint()
    {
        var src = @"
interface IGreet { string Hello(); }
class Foo { int x; Foo() { this.x = 1; } }
impl IGreet for Foo {
    public string Hello() { return ""hi""; }
}
string Greet<T>(T t) where T: IGreet { return t.Hello(); }
void Main() { var f = new Foo(); var s = Greet(f); }";
        Check(src).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void ImplBlock_GenericTrait_Passes()
    {
        var src = @"
interface IMatches<T> { bool MatchesValue(T other); }
class Box { int v; Box() { this.v = 0; } }
impl IMatches<int> for Box {
    public bool MatchesValue(int other) { return this.v == other; }
}
void Use<T>(T t) where T: IMatches<int> { }
void Main() { var b = new Box(); Use(b); }";
        Check(src).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void ImplBlock_NonUserTarget_Rejected()
    {
        var src = @"
interface IGreet { string Hello(); }
impl IGreet for NotAClass {
    public string Hello() { return ""x""; }
}
void Main() { }";
        Check(src).HasErrors.Should().BeTrue(because: "NotAClass is not a known class");
    }

    [Fact]
    public void ImplBlock_MissingMethod_Rejected()
    {
        var src = @"
interface IPair { int First(); int Second(); }
class P { int a; P() { this.a = 1; } }
impl IPair for P {
    public int First() { return this.a; }
}
void Main() { }";
        Check(src).HasErrors.Should().BeTrue(because: "IPair.Second missing from impl");
    }

    [Fact]
    public void ImplBlock_SignatureMismatch_Rejected()
    {
        var src = @"
interface IEq<T> { bool Equals(T other); }
class Box { int v; Box() { this.v = 0; } }
impl IEq<int> for Box {
    public bool Equals(int a, int b) { return a == b; }
}
void Main() { }";
        Check(src).HasErrors.Should().BeTrue(because: "Equals has arity mismatch");
    }

    [Fact]
    public void ImplBlock_DuplicateWithClassMethod_Rejected()
    {
        var src = @"
interface IGreet { string Hello(); }
class Foo { int x; Foo() { this.x = 1; } public string Hello() { return ""x""; } }
impl IGreet for Foo {
    public string Hello() { return ""y""; }
}
void Main() { }";
        Check(src).HasErrors.Should().BeTrue(because: "Foo already declared Hello directly");
    }

    // ── L3 operator overload (C# `operator` keyword) ──────────────────────────

    [Fact]
    public void OpOverload_BinaryPlus_OnUserStruct()
    {
        var src = @"
public struct Vec2 {
    int x; int y;
    Vec2(int a, int b) { this.x = a; this.y = b; }
    public static Vec2 operator +(Vec2 a, Vec2 b) { return new Vec2(a.x + b.x, a.y + b.y); }
}
void Main() {
    var a = new Vec2(1, 2);
    var b = new Vec2(3, 4);
    var c = a + b;
}";
        Check(src).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void OpOverload_Heterogeneous_StructTimesInt()
    {
        var src = @"
public struct Vec2 {
    int x; int y;
    Vec2(int a, int b) { this.x = a; this.y = b; }
    public static Vec2 operator *(Vec2 v, int s) { return new Vec2(v.x * s, v.y * s); }
}
void Main() {
    var v = new Vec2(1, 2);
    var scaled = v * 10;
}";
        Check(src).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void OpOverload_TypeMismatch_Rejected()
    {
        // Vec2 + int with no matching static operator → falls back to BinaryTypeTable,
        // which rejects non-numeric left operand.
        var src = @"
public struct Vec2 {
    int x; int y;
    Vec2(int a, int b) { this.x = a; this.y = b; }
    public static Vec2 operator +(Vec2 a, Vec2 b) { return new Vec2(a.x + b.x, a.y + b.y); }
}
void Main() {
    var v = new Vec2(1, 2);
    var bad = v + 42;
}";
        Check(src).HasErrors.Should().BeTrue(because: "Vec2 has no operator +(Vec2, int)");
    }

    // Note: generic instance operator path (`a + b` on T: INumber<T>) works when
    // the interface + primitive struct are inline-declared; separate end-to-end
    // demo via stdlib INumber is deferred to a follow-up iteration.

    // ── L3-G2.5 chain (class-side TypeArgs) ───────────────────────────────────

    // Class that implements `IEquatable<int>` satisfies `where T: IEquatable<int>`.
    [Fact]
    public void ChainConstraint_ClassIfaceArgs_Match_Passes()
    {
        var src = @"
interface IEquatable<T> { bool Equals(T other); }
class IntEq : IEquatable<int> { bool Equals(int other) { return false; } }
class Foo<T> where T: IEquatable<int> { T t; Foo() { this.t = null; } }
void Main() { var f = new Foo<IntEq>(); }";
        Check(src).HasErrors.Should().BeFalse();
    }

    // Class that implements `IEquatable<int>` does NOT satisfy `where T: IEquatable<string>`.
    [Fact]
    public void ChainConstraint_ClassIfaceArgs_Mismatch_Rejected()
    {
        var src = @"
interface IEquatable<T> { bool Equals(T other); }
class IntEq : IEquatable<int> { bool Equals(int other) { return false; } }
class Foo<T> where T: IEquatable<string> { T t; Foo() { this.t = null; } }
void Main() { var f = new Foo<IntEq>(); }";
        var diags = Check(src);
        diags.HasErrors.Should().BeTrue(
            because: "IntEq implements IEquatable<int>, not IEquatable<string>");
    }

    // Instantiated-class substitution: `Pair<int>` implements `IEquatable<int>` →
    // satisfies `where T: IEquatable<int>`.
    [Fact]
    public void ChainConstraint_InstantiatedClass_Substitution()
    {
        var src = @"
interface IEquatable<T> { bool Equals(T other); }
class Pair<U> : IEquatable<U> {
    U u;
    Pair() { this.u = null; }
    bool Equals(U other) { return false; }
}
class Foo<T> where T: IEquatable<int> { T t; Foo() { this.t = null; } }
void Main() { var f = new Foo<Pair<int>>(); }";
        Check(src).HasErrors.Should().BeFalse();
    }

    // ── L3-G2.5 chain constraint validation ──────────────────────────────────

    // `where T: IEquatable<T>` with T=int — self-referential primitive case, must pass.
    [Fact]
    public void ChainConstraint_SelfReferential_Primitive_Passes()
    {
        var src = @"
interface IEquatable<T> { bool Equals(T other); }
struct int : IEquatable<int> { [Native(""__int_equals"")] public extern bool Equals(int other); }
class Box<T> where T: IEquatable<T> {
    T value;
    Box(T v) { this.value = v; }
}
void Main() { var b = new Box<int>(0); }";
        Check(src).HasErrors.Should().BeFalse();
    }

    // `where T: IEquatable<U>, U: IEquatable<U>` instantiated with <int, string>
    // must reject: int does not implement IEquatable<string>.
    [Fact]
    public void ChainConstraint_CrossParam_Primitive_Mismatch_Rejected()
    {
        var src = @"
class Foo<T, U> where T: IEquatable<U>, U: IEquatable<U> {
    T t; U u;
    Foo(T t, U u) { this.t = t; this.u = u; }
}
interface IEquatable<T> { bool Equals(T other); }
void Main() { var f = new Foo<int, string>(0, ""x""); }";
        var diags = Check(src);
        diags.HasErrors.Should().BeTrue(because: "int only satisfies IEquatable<int>, not IEquatable<string>");
    }

    // Same decl, matched args `<int, int>` — both params self-referential, must pass.
    [Fact]
    public void ChainConstraint_CrossParam_Primitive_Match_Passes()
    {
        var src = @"
interface IEquatable<T> { bool Equals(T other); }
struct int : IEquatable<int> { [Native(""__int_equals"")] public extern bool Equals(int other); }
class Foo<T, U> where T: IEquatable<U>, U: IEquatable<U> {
    T t; U u;
    Foo(T t, U u) { this.t = t; this.u = u; }
}
void Main() { var f = new Foo<int, int>(0, 0); }";
        Check(src).HasErrors.Should().BeFalse();
    }

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

    // ── [Native] / extern validation ─────────────────────────────────────────

    [Fact]
    public void ExternNativeMethod_KnownIntrinsic_NoErrors()
    {
        Check("""
            class Console {
                [Native("__println")]
                public static extern void WriteLine(string value);
            }
            """).HasErrors.Should().BeFalse();
    }


    [Fact]
    public void ExternMethod_MissingNativeAttribute_ReportsZ0903()
    {
        var diags = Check("""
            class Foo {
                public static extern void Bar();
            }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.ExternRequiresNative);
    }

    [Fact]
    public void NativeAttribute_MissingExtern_ReportsZ0904()
    {
        // [Native] on a regular method with a body
        var diags = Check("""
            class Foo {
                [Native("__println")]
                public static void Bar(string s) { }
            }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.NativeRequiresExtern);
    }

    [Fact]
    public void ExternNativeMethod_VariadicIntrinsic_AnyParamCount_NoErrors()
    {
        // __str_format is variadic (ParamCount = -1), any count is valid
        Check("""
            class Str {
                [Native("__str_format")]
                public static extern string Format(string template, string a, string b);
            }
            """).HasErrors.Should().BeFalse();
    }

    // ── Generics ─────────────────────────────────────────────────────────────

    [Fact]
    public void GenericClass_InferredTypeArgs_NoErrors()
    {
        Check("""
            class Box<T> {
                T value;
                Box(T v) { this.value = v; }
                T Get() { return this.value; }
            }
            void Main() { var b = new Box(42); }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void GenericClass_ExplicitTypeArgs_NoErrors()
    {
        Check("""
            class Box<T> {
                T value;
                Box(T v) { this.value = v; }
            }
            void Main() { var b = new Box<int>(42); }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void GenericClass_WrongTypeArgCount_Error()
    {
        var diags = Check("""
            class Box<T> {
                T value;
                Box(T v) { this.value = v; }
            }
            void Main() { var b = new Box<int, string>(42); }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TypeMismatch
            && d.Message.Contains("expects 1 type argument(s)"));
    }

    [Fact]
    public void GenericFunction_NoErrors()
    {
        Check("""
            T Identity<T>(T x) { return x; }
            void Main() { var r = Identity(42); }
            """).HasErrors.Should().BeFalse();
    }

    // ── Generic constraints (L3-G2) ─────────────────────────────────────────

    [Fact]
    public void Generic_SingleConstraint_MethodCallOk()
    {
        // a.CompareTo(b) must resolve via constraint interface.
        Check("""
            interface IComparable<T> { int CompareTo(T other); }

            T Max<T>(T a, T b) where T: IComparable<T> {
                return a.CompareTo(b) > 0 ? a : b;
            }

            class Num : IComparable<Num> {
                int v;
                Num(int x) { this.v = x; }
                public int CompareTo(Num other) { return this.v - other.v; }
            }

            void Main() {
                var a = new Num(1);
                var b = new Num(2);
                var m = Max(a, b);
            }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_MultiConstraint_BothMethodsOk()
    {
        Check("""
            interface IDisplay { string Show(); }
            interface IEquatable<T> { bool Equals(T other); }

            void Describe<T>(T a, T b) where T: IDisplay + IEquatable<T> {
                a.Show();
                a.Equals(b);
            }

            class Item : IDisplay, IEquatable<Item> {
                public string Show() { return "item"; }
                public bool Equals(Item other) { return true; }
            }

            void Main() {
                Describe(new Item(), new Item());
            }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_CrossParamConstraint_Ok()
    {
        Check("""
            interface IHashable { int Hash(); }
            interface ICloneable { int Clone(); }

            void Copy<K, V>(K k, V v) where K: IHashable, V: ICloneable {
                k.Hash();
                v.Clone();
            }

            class A : IHashable { public int Hash() { return 1; } }
            class B : ICloneable { public int Clone() { return 2; } }

            void Main() { Copy(new A(), new B()); }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_CallSite_TypeArgImplements_Ok()
    {
        Check("""
            interface IComparable<T> { int CompareTo(T other); }

            class Box<T> where T: IComparable<T> {
                T item;
                Box(T x) { this.item = x; }
            }

            class Num : IComparable<Num> {
                int v;
                Num(int x) { this.v = x; }
                public int CompareTo(Num other) { return this.v - other.v; }
            }

            void Main() { var b = new Box<Num>(new Num(1)); }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_CallSite_TypeArgMissing_Error()
    {
        var diags = Check("""
            interface IComparable<T> { int CompareTo(T other); }

            class Box<T> where T: IComparable<T> {
                T item;
                Box(T x) { this.item = x; }
            }

            class Plain { int v; Plain(int x) { this.v = x; } }

            void Main() { var b = new Box<Plain>(new Plain(1)); }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TypeMismatch
            && d.Message.Contains("does not satisfy constraint")
            && d.Message.Contains("IComparable"));
    }

    [Fact]
    public void Generic_MethodOnUnconstrainedT_Error()
    {
        var diags = Check("""
            void F<T>(T a) { a.CompareTo(a); }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Message.Contains("CompareTo"));
    }

    [Fact]
    public void Generic_ClassField_ConstraintMethodCall_Ok()
    {
        Check("""
            interface IComparable<T> { int CompareTo(T other); }

            class Sorted<T> where T: IComparable<T> {
                T first;
                Sorted(T x) { this.first = x; }
                int Rank(T other) { return this.first.CompareTo(other); }
            }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_Inferred_ConstraintSatisfied_Ok()
    {
        // Max(a, b) with inferred T=Num — constraint validated from arg types.
        Check("""
            interface IComparable<T> { int CompareTo(T other); }

            T Max<T>(T a, T b) where T: IComparable<T> {
                return a.CompareTo(b) > 0 ? a : b;
            }

            class Num : IComparable<Num> {
                int v;
                Num(int x) { this.v = x; }
                public int CompareTo(Num other) { return this.v - other.v; }
            }

            void Main() { var m = Max(new Num(3), new Num(7)); }
            """).HasErrors.Should().BeFalse();
    }

    // ── Base class constraints (L3-G2.5) ────────────────────────────────────

    [Fact]
    public void Generic_BaseClass_FieldAccess_Ok()
    {
        Check("""
            class Animal {
                public int legs;
                Animal(int n) { this.legs = n; }
            }
            void Describe<T>(T x) where T: Animal {
                Console.WriteLine(x.legs);
            }
            class Dog : Animal { Dog() : base(4) { } }
            void Main() { Describe(new Dog()); }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_BaseClass_MethodCall_Ok()
    {
        Check("""
            class Base {
                public int Compute() { return 42; }
            }
            int Run<T>(T x) where T: Base {
                return x.Compute();
            }
            class Sub : Base { }
            void Main() { var r = Run(new Sub()); }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_BaseClassAndInterface_Combo_Ok()
    {
        Check("""
            interface IDisplay { string Show(); }
            class Base { public int id; Base(int i) { this.id = i; } }
            void F<T>(T x) where T: Base + IDisplay {
                Console.WriteLine(x.id);
                x.Show();
            }
            class Item : Base, IDisplay {
                Item(int i) : base(i) { }
                public string Show() { return "item"; }
            }
            void Main() { F(new Item(1)); }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_CallSite_SubclassSatisfies_Ok()
    {
        Check("""
            class Animal { }
            class Dog : Animal { }
            class Box<T> where T: Animal {
                T item;
                Box(T x) { this.item = x; }
            }
            void Main() { var b = new Box<Dog>(new Dog()); }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_CallSite_SiblingClass_Error()
    {
        var diags = Check("""
            class Animal { }
            class Vehicle { }
            class Box<T> where T: Animal {
                T item;
                Box(T x) { this.item = x; }
            }
            void Main() { var b = new Box<Vehicle>(new Vehicle()); }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TypeMismatch
            && d.Message.Contains("does not satisfy constraint")
            && d.Message.Contains("Animal"));
    }

    [Fact]
    public void Generic_MultipleBaseClasses_Error()
    {
        var diags = Check("""
            class A { }
            class B { }
            void F<T>(T x) where T: A + B { }
            void Main() { }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TypeMismatch
            && d.Message.Contains("multiple class constraints"));
    }

    [Fact]
    public void Generic_BaseClassNotFirst_Error()
    {
        var diags = Check("""
            interface IFoo { void M(); }
            class Animal { }
            void F<T>(T x) where T: IFoo + Animal { }
            void Main() { }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TypeMismatch
            && d.Message.Contains("must appear first"));
    }

    // ── Reference / Value type flag constraints (L3-G2.5 refvalue) ──────────

    [Fact]
    public void Generic_ClassConstraint_Reference_Ok()
    {
        Check("""
            class Foo { }
            void F<T>(T x) where T: class { }
            void Main() { F(new Foo()); }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_ClassConstraint_Primitive_Error()
    {
        var diags = Check("""
            void F<T>(T x) where T: class { }
            void Main() { F(42); }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TypeMismatch
            && d.Message.Contains("does not satisfy constraint `class`"));
    }

    [Fact]
    public void Generic_StructConstraint_Primitive_Ok()
    {
        Check("""
            void F<T>(T x) where T: struct { }
            void Main() { F(42); }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_StructConstraint_RefType_Error()
    {
        var diags = Check("""
            class Foo { }
            void F<T>(T x) where T: struct { }
            void Main() { F(new Foo()); }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TypeMismatch
            && d.Message.Contains("does not satisfy constraint `struct`"));
    }

    [Fact]
    public void Generic_ClassAndStruct_Exclusive_Error()
    {
        var diags = Check("""
            void F<T>(T x) where T: class + struct { }
            void Main() { }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TypeMismatch
            && d.Message.Contains("cannot be both `class` and `struct`"));
    }

    [Fact]
    public void Generic_ClassAndInterface_Combo_Ok()
    {
        Check("""
            interface IDisplay { string Show(); }
            void F<T>(T x) where T: class + IDisplay { x.Show(); }
            class Item : IDisplay { public string Show() { return "item"; } }
            void Main() { F(new Item()); }
            """).HasErrors.Should().BeFalse();
    }

    // ── Bare type parameter constraint (L3-G2.5) ────────────────────────────
    // NOTE: z42 supports explicit type args only on `new GenericClass<T>(...)`, not on
    // function calls. Tests below use generic classes to verify call-site subtype checks.

    [Fact]
    public void Generic_BareTypeParam_SubclassArg_Ok()
    {
        Check("""
            class Animal { }
            class Dog : Animal { }
            class Container<T, U> where U: T {
                Container() { }
            }
            void Main() { var c = new Container<Animal, Dog>(); }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_BareTypeParam_SiblingArg_Error()
    {
        var diags = Check("""
            class Animal { }
            class Vehicle { }
            class Container<T, U> where U: T {
                Container() { }
            }
            void Main() { var c = new Container<Animal, Vehicle>(); }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TypeMismatch
            && d.Message.Contains("not a subtype"));
    }

    [Fact]
    public void Generic_BareTypeParam_SameArg_Ok()
    {
        Check("""
            class Animal { }
            class Container<T, U> where U: T {
                Container() { }
            }
            void Main() { var c = new Container<Animal, Animal>(); }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_BareTypeParam_InClass_ReturnAssign_Ok()
    {
        Check("""
            class Animal { }
            class Dog : Animal { }
            class Container<T, U> where U: T {
                T Get(U child) { return child; }
            }
            void Main() {
                var c = new Container<Animal, Dog>();
                var a = c.Get(new Dog());
            }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_BareTypeParam_MultipleBare_Error()
    {
        var diags = Check("""
            class A { }
            class B { }
            void F<T, U, V>(T t, U u, V v) where V: T + U { }
            void Main() { }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TypeMismatch
            && d.Message.Contains("multiple type-param constraints"));
    }

    // ── Instantiated generic type substitution (L3-G4a) ─────────────────────

    [Fact]
    public void Generic_Instantiated_MethodReturnSubstituted_Ok()
    {
        // b.Get() returns T; with Box<int>, T=int, so result can be added to int.
        Check("""
            class Box<T> {
                T value;
                Box(T v) { this.value = v; }
                T Get() { return this.value; }
            }
            void Main() {
                var b = new Box<int>(42);
                int x = b.Get();
                int y = x + 1;
            }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_Instantiated_FieldAccessSubstituted_Ok()
    {
        Check("""
            class Box<T> {
                public T value;
                Box(T v) { this.value = v; }
            }
            void Main() {
                var b = new Box<int>(42);
                int x = b.value;
            }
            """).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_Instantiated_WrongTypeAssignment_Error()
    {
        var diags = Check("""
            class Box<T> {
                T value;
                Box(T v) { this.value = v; }
                T Get() { return this.value; }
            }
            void Main() {
                var b = new Box<int>(42);
                string s = b.Get();
            }
            """);
        diags.HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Generic_Instantiated_MultipleTypeArgs_Ok()
    {
        Check("""
            class Pair<A, B> {
                public A first;
                public B second;
                Pair(A a, B b) { this.first = a; this.second = b; }
            }
            void Main() {
                var p = new Pair<int, string>(1, "hi");
                int i = p.first;
                string s = p.second;
            }
            """).HasErrors.Should().BeFalse();
    }

    // ── Primitive interface implementation (L3-G4b) ─────────────────────────

    // L3-G4b primitive-as-struct: tests inline-declare the primitive struct so
    // `PrimitiveImplementsInterface` (now data-driven) has the interface list to consult.
    // In production compilation stdlib provides these automatically.
    private const string InlinePrimitiveStructs = @"
interface IComparable<T> { int CompareTo(T other); }
interface IEquatable<T> { bool Equals(T other); }
struct int : IComparable<int>, IEquatable<int> {
    [Native(""__int_compare_to"")] public extern int CompareTo(int other);
    [Native(""__int_equals"")]     public extern bool Equals(int other);
}
struct String : IComparable<string>, IEquatable<string> {
    [Native(""__str_compare_to"")] public extern int CompareTo(string other);
    [Native(""__str_equals"")]     public extern bool Equals(string other);
}
struct bool : IEquatable<bool> {
    [Native(""__bool_equals"")] public extern bool Equals(bool other);
}
";

    [Fact]
    public void Generic_PrimitiveInt_SatisfiesIComparable_Ok()
    {
        // int implements IComparable<int> — Max<int> should type-check.
        Check(InlinePrimitiveStructs + @"
T Max<T>(T a, T b) where T: IComparable<T> {
    return a.CompareTo(b) > 0 ? a : b;
}

void Main() { var m = Max(3, 5); }").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_PrimitiveString_SatisfiesIComparable_Ok()
    {
        Check(InlinePrimitiveStructs + @"
T Max<T>(T a, T b) where T: IComparable<T> {
    return a.CompareTo(b) > 0 ? a : b;
}

void Main() { var m = Max(""a"", ""b""); }").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_PrimitiveBool_NoIComparable_Error()
    {
        // bool only satisfies IEquatable, not IComparable.
        var diags = Check("""
            interface IComparable<T> { int CompareTo(T other); }
            void F<T>(T a, T b) where T: IComparable<T> { }
            void Main() { F(true, false); }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.TypeMismatch
            && d.Message.Contains("does not satisfy constraint"));
    }

    [Fact]
    public void Generic_PrimitiveBool_SatisfiesIEquatable_Ok()
    {
        Check(InlinePrimitiveStructs + @"
bool AreEqual<T>(T a, T b) where T: IEquatable<T> { return a.Equals(b); }
void Main() { var r = AreEqual(true, false); }").HasErrors.Should().BeFalse();
    }
}
