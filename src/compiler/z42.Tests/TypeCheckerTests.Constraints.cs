using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;
using Z42.Semantics.TypeCheck;

namespace Z42.Tests;

public partial class TypeCheckerTests
{
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

    // ── L3-G2.5 chain constraint validation ──────────────────────────────────

    // `where T: IEquatable<T>` with T=int — self-referential primitive case, must pass.
    [Fact]
    public void ChainConstraint_SelfReferential_Primitive_Passes()
    {
        var src = @"
interface IEquatable<T> { bool Equals(T other); }
struct Int32 : IEquatable<int> { [Native(""__int32_equals"")] public extern bool Equals(int other); }
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
struct Int32 : IEquatable<int> { [Native(""__int32_equals"")] public extern bool Equals(int other); }
class Foo<T, U> where T: IEquatable<U>, U: IEquatable<U> {
    T t; U u;
    Foo(T t, U u) { this.t = t; this.u = u; }
}
void Main() { var f = new Foo<int, int>(0, 0); }";
        Check(src).HasErrors.Should().BeFalse();
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

}
