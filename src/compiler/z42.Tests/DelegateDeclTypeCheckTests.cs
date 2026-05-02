using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// 2026-05-02 add-delegate-type TypeCheck regression tests.
/// 验证 SymbolTable.Delegates 注册、ResolveType 命名/泛型解析、lambda 赋值兼容性。
public sealed class DelegateDeclTypeCheckTests
{
    private static (SemanticModel model, DiagnosticBag diags) Check(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        var model  = new TypeChecker(diags).Check(cu);
        return (model, diags);
    }

    [Fact]
    public void Delegate_Name_Resolves_To_FuncType()
    {
        var (_, diags) = Check("""
            namespace Demo;
            public delegate int Sq(int x);
            void Main() { Sq f = (int x) => x * x; var r = f(5); }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Lambda_Type_Mismatch_Rejected()
    {
        var (_, diags) = Check("""
            namespace Demo;
            public delegate void NoRet(int x);
            void Main() { NoRet f = (int x) => x; }
            """);
        diags.HasErrors.Should().BeTrue("lambda returning int can't bind to void delegate");
    }

    [Fact]
    public void Named_Delegate_And_Literal_Equivalent()
    {
        var (_, diags) = Check("""
            namespace Demo;
            public delegate int IntFn(int x);
            void Main() {
                IntFn a = (int x) => x * x;
                (int) -> int b = a;
                IntFn c = b;
                var r = c(7);
            }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_Delegate_Instantiation_Resolves()
    {
        var (_, diags) = Check("""
            namespace Demo;
            public delegate R Mapper<T, R>(T arg);
            void Main() {
                Mapper<int, int> sq = (int x) => x * x;
                var r = sq(5);
            }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Generic_Delegate_Wrong_Arity_Reports_Error()
    {
        var (_, diags) = Check("""
            namespace Demo;
            public delegate R Mapper<T, R>(T arg);
            void Main() {
                Mapper<int> bad = (int x) => x;
            }
            """);
        // Mapper<T, R> 需要 2 个 type args，给了 1 个 → 报错（具体码可能是
        // TypeMismatch / ArityMismatch；这里只验有错）
        diags.HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Multiple_Arity_Same_Name_Coexist()
    {
        var (_, diags) = Check("""
            namespace Demo;
            public delegate void Act<T>(T arg);
            public delegate void Act<T1, T2>(T1 a, T2 b);
            void Main() {
                Act<int> a1 = (int x) => {};
                Act<int, int> a2 = (int a, int b) => {};
            }
            """);
        diags.HasErrors.Should().BeFalse(
            "Act$1 and Act$2 should coexist via arity-suffixed key");
    }

    [Fact]
    public void Nested_Delegate_Visible_Within_Class()
    {
        // 嵌套 delegate 在外部用 `Btn.OnClick` 引用属于 follow-up
        // (D1a Open Question)；当前 v1 验证类内部使用即可。
        var (_, diags) = Check("""
            namespace Demo;
            public class Btn {
                public delegate void OnClick(int x);
                void Test() {
                    OnClick h = (int x) => {};
                    h(1);
                }
            }
            void Main() {}
            """);
        diags.HasErrors.Should().BeFalse();
    }
}
