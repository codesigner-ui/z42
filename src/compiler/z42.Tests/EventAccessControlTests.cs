using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// 2026-05-04 D-7-residual add-singlecast-event-idisposable-token tests.
/// 验证 event field 严格 access control：外部访问 invoke / assign / 裸读
/// 报 E0414，类内部访问通过，外部 += / -= desugar 不受影响。覆盖单播 + 多播。
public sealed class EventAccessControlTests
{
    private static (CompilationUnit cu, DiagnosticBag diags) Check(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        new TypeChecker(diags).Check(cu);
        return (cu, diags);
    }

    private const string Singlecast = """
        namespace Demo;
        using Std;
        public class Btn { public event Action<int> OnClick; }
        """;

    private const string Multicast = """
        namespace Demo;
        using Std;
        public class Bus { public event MulticastAction<int> OnTick; }
        """;

    [Fact]
    public void Singlecast_External_Invoke_Reports_E0414()
    {
        var (_, diags) = Check(Singlecast + """

            void Use(Btn b) { b.OnClick.Invoke(1); }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.EventFieldExternalAccess);
    }

    [Fact]
    public void Singlecast_External_Assign_Reports_E0414()
    {
        var (_, diags) = Check(Singlecast + """

            void Use(Btn b) { b.OnClick = (int x) => {}; }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.EventFieldExternalAccess);
    }

    [Fact]
    public void Multicast_External_Invoke_Reports_E0414()
    {
        var (_, diags) = Check(Multicast + """

            void Use(Bus b) { b.OnTick.Invoke(1); }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.EventFieldExternalAccess);
    }

    [Fact]
    public void Multicast_External_Assign_Reports_E0414()
    {
        var (_, diags) = Check(Multicast + """

            void Use(Bus b) { b.OnTick = new MulticastAction<int>(); }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.EventFieldExternalAccess);
    }

    [Fact]
    public void Internal_Access_Allowed()
    {
        // 类内部 read event field 允许（典型用法：合成的 add_X / remove_X
        // 自身就是访问 this.X）。此处简单 read 验证不命中 E0414。
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            public class Btn {
                public event Action<int> OnClick;
                Action<int>? Probe() { return this.OnClick; }
            }
            """);
        diags.HasErrors.Should().BeFalse(
            "类内部对 event field 裸读允许（不命中 E0414）");
    }

    [Fact]
    public void External_PlusEq_MinusEq_Allowed()
    {
        var (_, diags) = Check(Multicast + """

            void Sub(Bus b, Action<int> h) {
                b.OnTick += h;
                b.OnTick -= h;
            }
            """);
        diags.HasErrors.Should().BeFalse(
            "外部 += / -= 走 add_X / remove_X 合成方法路径，不命中 BindMemberExpr 的 E0414");
    }

    [Fact]
    public void Singlecast_Add_Returns_IDisposable()
    {
        var (cu, _) = Check(Singlecast);
        var add = cu.Classes[0].Methods.First(m => m.Name == "add_OnClick");
        add.ReturnType.Should().BeOfType<NamedType>()
            .Which.Name.Should().Be("IDisposable",
                "D-7-residual：单播 add_X 返回 IDisposable");
    }
}
