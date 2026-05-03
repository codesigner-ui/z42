using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// 2026-05-03 fix-generic-member-substitution：验证 generic 实例链式 member
/// 访问的 substitution propagation —— 数组元素 unwrap 后访问字段 / 字段
/// 链式 method call / 嵌套实例化等场景。
public sealed class GenericMemberAccessTests
{
    private static DiagnosticBag Check(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        new TypeChecker(diags).Check(cu);
        return diags;
    }

    [Fact]
    public void Array_InstantiatedElement_FieldAccess()
    {
        // `Foo<T>[] arr; arr[i].value` —— 数组元素是 instantiated generic，
        // 字段类型 T 应该 substitute 为正确的实例化 type-arg。
        var diags = Check("""
            namespace Demo;
            public class Foo<T> { public T value; }
            public class Bar<T> {
                Foo<T>[] arr;
                public Bar() { this.arr = new Foo<T>[4]; }
                public T get(int i) { return this.arr[i].value; }
            }
            """);
        diags.HasErrors.Should().BeFalse(diags.ToString());
    }

    [Fact]
    public void Array_InstantiatedElement_ChainedFieldMethod()
    {
        // 链式：arr[i].subscription.IsAlive() —— 字段 subscription 的 type
        // 是 generic interface ISubscription<T>，IsAlive() 应可解析。
        var diags = Check("""
            namespace Demo;
            public interface ISub<TD> {
                bool Live();
            }
            public class Slot<T> {
                public ISub<T> subscription;
            }
            public class Bus<T> {
                Slot<T>[] slots;
                public bool any() {
                    return this.slots[0].subscription.Live();
                }
            }
            """);
        diags.HasErrors.Should().BeFalse(diags.ToString());
    }

    [Fact]
    public void Array_NestedInstantiatedElement_FieldAccess()
    {
        // 嵌套：`Foo<Bar<T>>[]` 元素的字段访问
        var diags = Check("""
            namespace Demo;
            public class Inner<U> { public U val; }
            public class Outer<W> { public Inner<W> nested; }
            public class Wrapper<T> {
                Outer<T>[] arr;
                public T m(int i) { return this.arr[i].nested.val; }
            }
            """);
        diags.HasErrors.Should().BeFalse(diags.ToString());
    }

    [Fact]
    public void Direct_Instantiated_FieldAccess_Regression()
    {
        // Regression: 直接 instantiated 字段访问（无数组中介）仍工作。
        var diags = Check("""
            namespace Demo;
            public class Box<T> { public T val; }
            public class User {
                public int read(Box<int> b) { return b.val; }
            }
            """);
        diags.HasErrors.Should().BeFalse(diags.ToString());
    }

    [Fact]
    public void Direct_Instantiated_MethodCall_Regression()
    {
        // Regression: 直接 instantiated method call 仍工作。
        var diags = Check("""
            namespace Demo;
            public class Box<T> {
                public T get() { return null; }
            }
            public class User {
                public string read(Box<string> b) { return b.get(); }
            }
            """);
        diags.HasErrors.Should().BeFalse(diags.ToString());
    }

    [Fact]
    public void D2b_ExactPattern_SubSlot_With_ActionT()
    {
        // 完整复现 D2b 原 bug 模式：_SubSlot<Action<T>>[] 元素访问 + 接口方法
        // 字段 sub 类型是 ISubscription<TD>（TD = Action<T>）—— 双层 generic
        // 嵌套 + interface 包含 delegate type-arg。这是历史报错的原始场景。
        var diags = Check("""
            namespace Demo;
            public delegate void Action<T>(T arg);
            public interface ISub<TD> {
                TD Get();
                bool IsAlive();
            }
            public class Slot<TD> {
                public ISub<TD> sub;
            }
            public class Bus<T> {
                Slot<Action<T>>[] slots;
                public Bus() { this.slots = new Slot<Action<T>>[4]; }
                public bool check(int i) {
                    return this.slots[i].sub.IsAlive();
                }
            }
            """);
        diags.HasErrors.Should().BeFalse(diags.ToString());
    }

    [Fact]
    public void Interface_Field_TypeArg_Substitution()
    {
        // 直接验证 D2b 场景：generic class 字段类型是
        // generic interface 实例化（持 outer T），通过 generic instance 访问
        // 后接接口 method —— substitution 必须正确传递到 InterfaceType.TypeArgs。
        var diags = Check("""
            namespace Demo;
            public interface ISub<TD> {
                TD Get();
            }
            public class Holder<T> {
                public ISub<T> sub;
            }
            public class Bus<T> {
                Holder<T> h;
                public T pick() { return this.h.sub.Get(); }
            }
            """);
        diags.HasErrors.Should().BeFalse(diags.ToString());
    }
}
