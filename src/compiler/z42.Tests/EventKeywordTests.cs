using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// 2026-05-03 add-event-keyword-multicast (D2c-多播) regression tests.
/// 验证 event keyword 解析、parser 阶段合成 add_X/remove_X、TypeChecker
/// `+=` / `-=` desugar。单播 event 留 Spec 2b。
public sealed class EventKeywordTests
{
    private static (CompilationUnit cu, DiagnosticBag diags) Check(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var cu     = parser.ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        foreach (var d in parser.Diagnostics.All) diags.Add(d);
        new TypeChecker(diags).Check(cu);
        return (cu, diags);
    }

    [Fact]
    public void Event_Keyword_Parsed_As_FieldDecl_With_IsEvent()
    {
        var (cu, diags) = Check("""
            namespace Demo;
            using Std;
            public class Bus {
                public event MulticastAction<int> Clicked;
            }
            """);
        diags.HasErrors.Should().BeFalse(diags.ToString());
        var bus = cu.Classes.Should().ContainSingle().Subject;
        var evt = bus.Fields.Should().ContainSingle().Subject;
        evt.Name.Should().Be("Clicked");
        evt.IsEvent.Should().BeTrue();
    }

    [Fact]
    public void Event_Field_Has_AutoInit_Synthesized()
    {
        var (cu, _) = Check("""
            namespace Demo;
            using Std;
            public class Bus {
                public event MulticastAction<int> Clicked;
            }
            """);
        var evt = cu.Classes[0].Fields[0];
        evt.Initializer.Should().NotBeNull("multicast event field auto-initializes to new MulticastAction<T>()");
        evt.Initializer.Should().BeOfType<NewExpr>();
    }

    [Fact]
    public void Event_Synthesizes_AddX_And_RemoveX_Methods()
    {
        var (cu, _) = Check("""
            namespace Demo;
            using Std;
            public class Bus {
                public event MulticastAction<int> Clicked;
            }
            """);
        var bus = cu.Classes[0];
        bus.Methods.Select(m => m.Name).Should().Contain(["add_Clicked", "remove_Clicked"]);

        var add = bus.Methods.First(m => m.Name == "add_Clicked");
        add.Params.Should().ContainSingle().Which.Name.Should().Be("h");
        add.Visibility.Should().Be(Visibility.Public);

        var remove = bus.Methods.First(m => m.Name == "remove_Clicked");
        remove.Params.Should().ContainSingle().Which.Name.Should().Be("h");
        remove.Visibility.Should().Be(Visibility.Public);
    }

    // ── D-7 单播 event (2026-05-04 add-event-keyword-singlecast) ─────────────

    [Fact]
    public void SingleCast_Event_Synthesizes_Add_Remove_Methods()
    {
        var (cu, _) = Check("""
            namespace Demo;
            using Std;
            public class Bus {
                public event Action<int> OnKey;
            }
            """);
        var bus = cu.Classes[0];
        bus.Methods.Select(m => m.Name).Should().Contain(["add_OnKey", "remove_OnKey"]);
        var add = bus.Methods.First(m => m.Name == "add_OnKey");
        // 2026-05-04 D-7-residual：单播 add 返回 IDisposable（与多播对齐）
        add.ReturnType.Should().BeOfType<NamedType>()
            .Which.Name.Should().Be("IDisposable");
        // handler 类型直接是字段裸类型 Action<int>
        add.Params[0].Type.Should().BeOfType<GenericType>()
            .Which.Name.Should().Be("Action");
    }

    [Fact]
    public void SingleCast_Event_Field_Is_Nullable()
    {
        var (cu, _) = Check("""
            namespace Demo;
            using Std;
            public class Bus {
                public event Action<int> OnKey;
            }
            """);
        var field = cu.Classes[0].Fields[0];
        field.Type.Should().BeOfType<OptionType>("single-cast event field is nullable");
        field.Initializer.Should().BeNull("nullable defaults to null");
    }

    [Fact]
    public void Interface_SingleCast_Event_Synthesizes_Signatures()
    {
        var (cu, _) = Check("""
            namespace Demo;
            using Std;
            public interface IBus {
                event Action<int> OnKey;
            }
            """);
        var iface = cu.Interfaces[0];
        iface.Methods.Select(m => m.Name).Should().Contain(["add_OnKey", "remove_OnKey"]);
        var add = iface.Methods.First(m => m.Name == "add_OnKey");
        // 2026-05-04 D-7-residual：interface 端单播 event add 同样返回 IDisposable
        add.ReturnType.Should().BeOfType<NamedType>()
            .Which.Name.Should().Be("IDisposable");
    }

    [Fact]
    public void Event_Unknown_Generic_Type_Reports_Error()
    {
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            public class Bus {
                public event Foo<int> Bar;
            }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Message.Contains("not supported"));
    }

    [Fact]
    public void Event_With_Auto_Property_Brace_Reports_Error()
    {
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            public class Bus {
                public event MulticastAction<int> Clicked { get; }
            }
            """);
        diags.HasErrors.Should().BeTrue();
    }

    // 多播 event 的 `+=` / `-=` desugar 端到端验证由 golden test
    // (multicast_event_keyword) 覆盖 —— 单元测试环境无 stdlib，
    // 合成的 add_X body `return this.X.Subscribe(h)` 拿不到 MulticastAction
    // 的 Methods 定义会报 TypeCheck 错。

    // ── D2d-1 多播 Func / Predicate event (2026-05-03 add-multicast-func-predicate) ─

    [Fact]
    public void MulticastFunc_Event_Synthesizes_Add_With_Func_Handler()
    {
        var (cu, _) = Check("""
            namespace Demo;
            using Std;
            public class Bus {
                public event MulticastFunc<int, bool> Validate;
            }
            """);
        var bus = cu.Classes[0];
        bus.Methods.Select(m => m.Name).Should().Contain(["add_Validate", "remove_Validate"]);
        var add = bus.Methods.First(m => m.Name == "add_Validate");
        var hType = (GenericType)add.Params[0].Type;
        hType.Name.Should().Be("Func");
        hType.TypeArgs.Should().HaveCount(2, "Func<int, bool>");
    }

    [Fact]
    public void MulticastPredicate_Event_Synthesizes_Add_With_Predicate_Handler()
    {
        var (cu, _) = Check("""
            namespace Demo;
            using Std;
            public class Filt {
                public event MulticastPredicate<int> Filter;
            }
            """);
        var add = cu.Classes[0].Methods.First(m => m.Name == "add_Filter");
        var hType = (GenericType)add.Params[0].Type;
        hType.Name.Should().Be("Predicate");
        hType.TypeArgs.Should().HaveCount(1);
    }

    [Fact]
    public void Interface_MulticastFunc_Event_Synthesizes_Signatures()
    {
        var (cu, _) = Check("""
            namespace Demo;
            using Std;
            public interface IFilt {
                event MulticastFunc<int, bool> Validate;
            }
            """);
        var iface = cu.Interfaces[0];
        iface.Methods.Select(m => m.Name).Should().Contain(["add_Validate", "remove_Validate"]);
    }

    // ── interface event default (2026-05-03 add-interface-event-default) ─────

    [Fact]
    public void Interface_Event_Synthesizes_AddX_RemoveX_MethodSignatures()
    {
        var (cu, _) = Check("""
            namespace Demo;
            using Std;
            public interface IBus {
                event MulticastAction<int> Clicked;
            }
            """);
        var iface = cu.Interfaces.Should().ContainSingle().Subject;
        iface.Methods.Select(m => m.Name).Should().Contain(["add_Clicked", "remove_Clicked"]);
        var add = iface.Methods.First(m => m.Name == "add_Clicked");
        add.Params.Should().ContainSingle();
        add.IsStatic.Should().BeFalse();
        add.IsVirtual.Should().BeFalse();
        add.Body.Should().BeNull("instance abstract signature");
    }

    // Interface_SingleCast_Event_Reports_Not_Yet_Supported removed:
    // 2026-05-04 D-7 add-event-keyword-singlecast 解锁 interface 单播 event。
    // 见 Interface_SingleCast_Event_Synthesizes_Signatures 上面正路径覆盖。
}
