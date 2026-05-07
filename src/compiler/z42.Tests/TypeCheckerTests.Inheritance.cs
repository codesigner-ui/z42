using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;
using Z42.Semantics.TypeCheck;

namespace Z42.Tests;

public partial class TypeCheckerTests
{
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

}
