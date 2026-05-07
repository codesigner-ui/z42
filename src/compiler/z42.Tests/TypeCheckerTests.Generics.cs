using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;
using Z42.Semantics.TypeCheck;

namespace Z42.Tests;

public partial class TypeCheckerTests
{
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

}
