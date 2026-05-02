using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// TypeChecker unit tests for fix-class-field-default-init (P1).
/// Verifies that instance field initializers are bound and recorded in
/// SemanticModel.BoundInstanceInits, and that type mismatches are reported.
public sealed class ClassFieldInitTypeCheckTests
{
    private static (SemanticModel model, DiagnosticBag diags) Check(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        var model  = new TypeChecker(diags, LanguageFeatures.Phase1).Check(cu);
        return (model, diags);
    }

    [Fact]
    public void InstanceFieldInit_Binds_Correct_Type()
    {
        var (model, diags) = Check("""
            namespace Demo;
            class Box { int n = 5; }
            void Main() {}
            """);
        diags.HasErrors.Should().BeFalse();
        model.BoundInstanceInits.Should().ContainSingle()
            .Which.Key.Name.Should().Be("n");
    }

    [Fact]
    public void InstanceFieldInit_TypeMismatch_Reports_Error()
    {
        var (_, diags) = Check("""
            namespace Demo;
            class Bad { int n = "string"; }
            void Main() {}
            """);
        diags.HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Static_And_Instance_Init_Coexist_In_Different_Dicts()
    {
        var (model, diags) = Check("""
            namespace Demo;
            class C {
                static int s = 1;
                int n = 2;
            }
            void Main() {}
            """);
        diags.HasErrors.Should().BeFalse();
        model.BoundStaticInits.Should().ContainSingle()
            .Which.Key.Name.Should().Be("s");
        model.BoundInstanceInits.Should().ContainSingle()
            .Which.Key.Name.Should().Be("n");
    }

    [Fact]
    public void Field_With_No_Initializer_Is_Absent()
    {
        var (model, diags) = Check("""
            namespace Demo;
            class C { int n; bool b; }
            void Main() {}
            """);
        diags.HasErrors.Should().BeFalse();
        model.BoundInstanceInits.Should().BeEmpty();
        model.BoundStaticInits.Should().BeEmpty();
    }

    [Fact]
    public void Multiple_Instance_Fields_All_Bound()
    {
        var (model, diags) = Check("""
            namespace Demo;
            class A {
                int x = 1;
                string s = "hello";
                bool b = true;
            }
            void Main() {}
            """);
        diags.HasErrors.Should().BeFalse();
        var names = model.BoundInstanceInits.Keys.Select(f => f.Name).ToHashSet();
        names.Should().BeEquivalentTo(new[] { "x", "s", "b" });
    }
}
