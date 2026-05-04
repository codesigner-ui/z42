using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// 2026-05-04 D-6 add-nested-delegate-dotted-path tests.
/// 验证嵌套 delegate 在外部以 `Owner.Inner` dotted-path 形式被引用为字段类型 /
/// 参数 / 返回类型；以及不存在的 nested 名 / 左侧非 class 等错误路径。
public sealed class NestedDelegateAccessTests
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
    public void DottedPath_Field_Type_Resolves()
    {
        var (_, diags) = Check("""
            namespace Demo;
            public class Btn { public delegate void OnClick(int x); }
            public class Listener { public Btn.OnClick handler; }
            """);
        diags.HasErrors.Should().BeFalse(
            "Listener.handler 类型为嵌套 delegate `Btn.OnClick`，dotted-path resolve 通过");
    }

    [Fact]
    public void DottedPath_Param_And_Return_Resolves()
    {
        var (_, diags) = Check("""
            namespace Demo;
            public class Btn { public delegate int Compute(int a, int b); }
            Btn.Compute MakeAdder() { return (int a, int b) => a + b; }
            int Use(Btn.Compute f) { return f(1, 2); }
            void Main() { var x = Use(MakeAdder()); }
            """);
        diags.HasErrors.Should().BeFalse(
            "嵌套 delegate 作为返回类型 + 参数类型，dotted-path resolve 通过");
    }

    [Fact]
    public void DottedPath_NonExistent_Member_Yields_Unknown_Type()
    {
        // 与 NamedType 对未知名一致策略：返回 Unknown / Prim（不直接报错），
        // 后续真使用该 type 时才报错。本测试验证"使用时报错"的路径。
        var (_, diags) = Check("""
            namespace Demo;
            public class Btn { public delegate void OnClick(int x); }
            public class Bad { public Btn.NotExist field; }
            void Main() {
                var b = new Bad();
                b.field(42);
            }
            """);
        diags.HasErrors.Should().BeTrue(
            "Btn.NotExist 解析失败 → field 类型 Unknown → 调用 b.field(42) 报错");
    }

    [Fact]
    public void DottedPath_Class_Internal_SimpleName_Still_Works()
    {
        var (_, diags) = Check("""
            namespace Demo;
            public class Btn {
                public delegate void OnClick(int x);
                public OnClick handler;
            }
            """);
        diags.HasErrors.Should().BeFalse(
            "类内部继续支持 simple name `OnClick` 引用嵌套 delegate（D1a 行为不回归）");
    }

    [Fact]
    public void DottedPath_NonClass_Left_Yields_Unknown_Type()
    {
        // 与 NamedType 对未知名一致：左侧 `int` 不是 class，整个 dotted-path
        // resolve 为 Unknown；使用时报错。
        var (_, diags) = Check("""
            namespace Demo;
            public class Bad { public int.NotAType field; }
            void Main() {
                var b = new Bad();
                b.field(42);
            }
            """);
        diags.HasErrors.Should().BeTrue(
            "左侧 `int` 不是 class，dotted-path resolve 为 Unknown → 后续使用报错");
    }
}
