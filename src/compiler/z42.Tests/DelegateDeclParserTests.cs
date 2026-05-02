using FluentAssertions;
using Z42.Core.Features;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// 2026-05-02 add-delegate-type Parser regression tests.
/// 验证 `delegate` 关键字 + 顶层 / 嵌套 / 泛型 / where / `delegate*` 各场景。
public sealed class DelegateDeclParserTests
{
    private static CompilationUnit Parse(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        return new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
    }

    [Fact]
    public void Simple_TopLevel_Delegate_Parses()
    {
        var cu = Parse("public delegate void OnClick(int x, int y);");
        cu.Delegates.Should().NotBeNull();
        cu.Delegates!.Should().HaveCount(1);
        cu.Delegates[0].Name.Should().Be("OnClick");
        cu.Delegates[0].Params.Should().HaveCount(2);
        cu.Delegates[0].TypeParams.Should().BeNull();
    }

    [Fact]
    public void Delegate_With_Return_Type_Parses()
    {
        var cu = Parse("public delegate int Sq(int x);");
        cu.Delegates![0].Name.Should().Be("Sq");
        cu.Delegates[0].ReturnType.Should().BeOfType<NamedType>()
            .Which.Name.Should().Be("int");
    }

    [Fact]
    public void Delegate_Without_Params_Parses()
    {
        var cu = Parse("public delegate void Done();");
        cu.Delegates![0].Params.Should().BeEmpty();
    }

    [Fact]
    public void Generic_Delegate_With_TypeParams_Parses()
    {
        var cu = Parse("public delegate R Func<T, R>(T arg);");
        cu.Delegates![0].TypeParams.Should().BeEquivalentTo(new[] { "T", "R" });
    }

    [Fact]
    public void Delegate_With_Where_Clause_Parses()
    {
        var cu = Parse("public delegate void Run<T>(T arg) where T : class;");
        cu.Delegates![0].Where.Should().NotBeNull();
    }

    [Fact]
    public void Nested_Delegate_In_Class_Parses()
    {
        var cu = Parse("""
            public class Btn {
                public delegate void OnClick(int x, int y);
            }
            """);
        cu.Classes.Should().HaveCount(1);
        cu.Classes[0].NestedDelegates.Should().NotBeNull();
        cu.Classes[0].NestedDelegates!.Should().HaveCount(1);
        cu.Classes[0].NestedDelegates![0].Name.Should().Be("OnClick");
    }

    [Fact]
    public void Delegate_Star_Reports_Unmanaged_Not_Supported()
    {
        // Parser 内置 DiagnosticBag —— ParseException 被捕获并转为 diagnostic
        // 而非外抛。验证诊断包含 unmanaged 关键字。
        var tokens = new Lexer("public delegate*<int, int> Fn;").Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        parser.ParseCompilationUnit();
        parser.Diagnostics.HasErrors.Should().BeTrue();
        parser.Diagnostics.All.Any(d => d.Message.Contains("unmanaged"))
            .Should().BeTrue();
    }
}
