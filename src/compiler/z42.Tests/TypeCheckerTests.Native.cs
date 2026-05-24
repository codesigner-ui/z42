using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;
using Z42.Semantics.TypeCheck;

namespace Z42.Tests;

public partial class TypeCheckerTests
{
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

    // ── Primitive interface implementation (L3-G4b) ─────────────────────────

    // L3-G4b primitive-as-struct: tests inline-declare the primitive struct so
    // `PrimitiveImplementsInterface` (now data-driven) has the interface list to consult.
    // In production compilation stdlib provides these automatically.
    // rename-primitives-to-pascal-case (2026-05-24): synthetic primitive structs declared
    // with BCL PascalCase names (`Int32` / `Boolean`) matching the production stdlib.
    // The keyword forms (`int` / `bool`) remain valid in user code as source-level aliases.
    private const string InlinePrimitiveStructs = @"
interface IComparable<T> { int CompareTo(T other); }
interface IEquatable<T> { bool Equals(T other); }
struct Int32 : IComparable<int>, IEquatable<int> {
    [Native(""__int32_compare_to"")] public extern int CompareTo(int other);
    [Native(""__int32_equals"")]     public extern bool Equals(int other);
}
struct String : IComparable<string>, IEquatable<string> {
    [Native(""__str_compare_to"")] public extern int CompareTo(string other);
    [Native(""__str_equals"")]     public extern bool Equals(string other);
}
struct Boolean : IEquatable<bool> {
    [Native(""__boolean_equals"")] public extern bool Equals(bool other);
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

    // ── L3 Static abstract interface members (C# 11 alignment) ───────────────

    // Interface declares `static abstract T M(T)`; implementer provides
    // `static override T M(T)` — accepted.
    [Fact]
    public void StaticAbstract_Implementer_WithOverride_Passes()
    {
        Check(@"
interface INumber<T> {
    static abstract T op_Add(T a, T b);
}
class MyInt : INumber<MyInt> {
    int v;
    MyInt() { this.v = 0; }
    public static override MyInt op_Add(MyInt a, MyInt b) { return a; }
}
void Main() { }").HasErrors.Should().BeFalse();
    }

    // Interface declares `static abstract`; implementer omits it → missing override.
    [Fact]
    public void StaticAbstract_Implementer_Missing_Rejected()
    {
        var diags = Check(@"
interface INumber<T> {
    static abstract T op_Add(T a, T b);
}
class MyInt : INumber<MyInt> {
    int v;
    MyInt() { this.v = 0; }
}
void Main() { }");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.InterfaceMismatch
            && d.Message.Contains("static override"));
    }

    // `override` targeting a name that doesn't exist on any implemented
    // interface → spelling防护 error.
    [Fact]
    public void StaticOverride_NoInterfaceTarget_Rejected()
    {
        var diags = Check(@"
interface INumber<T> {
    static abstract T op_Add(T a, T b);
}
class MyInt : INumber<MyInt> {
    int v;
    MyInt() { this.v = 0; }
    public static override MyInt op_Add(MyInt a, MyInt b) { return a; }
    public static override MyInt op_Addd(MyInt a, MyInt b) { return a; }
}
void Main() { }");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.InterfaceMismatch
            && d.Message.Contains("op_Addd"));
    }

    // `static virtual` default body — implementer may skip override (inherits default).
    [Fact]
    public void StaticVirtual_Implementer_CanSkip_Passes()
    {
        Check(@"
interface INumber<T> {
    static abstract T op_Add(T a, T b);
    static virtual T op_Double(T x) { return x; }
}
class MyInt : INumber<MyInt> {
    int v;
    MyInt() { this.v = 0; }
    public static override MyInt op_Add(MyInt a, MyInt b) { return a; }
}
void Main() { }").HasErrors.Should().BeFalse();
    }

    // `static` (no virtual/abstract) is sealed — override rejected.
    [Fact]
    public void StaticConcrete_Implementer_Override_Rejected()
    {
        var diags = Check(@"
interface INumber<T> {
    static T Tag(T x) { return x; }
}
class MyInt : INumber<MyInt> {
    int v;
    MyInt() { this.v = 0; }
    public static override MyInt Tag(MyInt x) { return x; }
}
void Main() { }");
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.InterfaceMismatch
            && d.Message.Contains("sealed"));
    }
}
