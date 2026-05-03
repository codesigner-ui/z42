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

    [Fact]
    public void SingleCast_Event_Reports_Not_Yet_Supported()
    {
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            public class Bus {
                public event Action<int> OnKey;
            }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Message.Contains("single-cast event not yet supported"));
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
}
