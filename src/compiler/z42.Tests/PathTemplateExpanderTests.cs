using FluentAssertions;
using Z42.Project;

namespace Z42.Tests;

/// 验证 PathTemplateExpander 的 4 内置变量、$$ 转义、字段白名单与错误码。
public sealed class PathTemplateExpanderTests
{
    static readonly PathTemplateExpander.Context Ctx = new(
        WorkspaceDir: "/repo",
        MemberDir:    "/repo/apps/hello",
        MemberName:   "hello",
        Profile:      "release");

    static readonly PathTemplateExpander Sut = new();

    static string Path(string s) =>
        Sut.Expand(s, Ctx, "test.toml", "[test]", PathTemplateExpander.FieldKind.Path);

    static string Scalar(string s) =>
        Sut.Expand(s, Ctx, "test.toml", "[test]", PathTemplateExpander.FieldKind.Scalar);

    // ── 4 个内置变量 ─────────────────────────────────────────────────────────

    [Fact]
    public void Expand_WorkspaceDir() =>
        Path("${workspace_dir}/presets/lib.toml").Should().Be("/repo/presets/lib.toml");

    [Fact]
    public void Expand_MemberDir() =>
        Path("${member_dir}/src").Should().Be("/repo/apps/hello/src");

    [Fact]
    public void Expand_MemberName() =>
        Path("dist/${member_name}.zpkg").Should().Be("dist/hello.zpkg");

    [Fact]
    public void Expand_Profile() =>
        Path("dist/${profile}").Should().Be("dist/release");

    [Fact]
    public void Expand_MultipleVariablesInOne() =>
        Path("${workspace_dir}/dist/${profile}/${member_name}.zpkg")
            .Should().Be("/repo/dist/release/hello.zpkg");

    // ── $$ 转义 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Expand_DollarEscape() =>
        Path("$$keep").Should().Be("$keep");

    [Fact]
    public void Expand_DollarEscapeMixedWithVar() =>
        Path("price-$$${member_name}").Should().Be("price-$hello");

    // ── 错误：未知变量（WS037）────────────────────────────────────────────────

    [Fact]
    public void Expand_UnknownVariable_Throws_WS037()
    {
        var act = () => Path("${unknown}");
        act.Should().Throw<ManifestException>().WithMessage("*WS037*${unknown}*");
    }

    [Fact]
    public void Expand_EnvVariable_NotSupportedInC1()
    {
        var act = () => Path("${env:HOME}");
        act.Should().Throw<ManifestException>()
           .WithMessage("*WS037*env*future*");
    }

    // ── 错误：语法错误（WS038）────────────────────────────────────────────────

    [Fact]
    public void Expand_NestedVariable_Throws_WS038()
    {
        var act = () => Path("${a${b}}");
        act.Should().Throw<ManifestException>().WithMessage("*WS038*nested*");
    }

    [Fact]
    public void Expand_UnclosedVariable_Throws_WS038()
    {
        var act = () => Path("${unfinished");
        act.Should().Throw<ManifestException>().WithMessage("*WS038*unclosed*");
    }

    [Fact]
    public void Expand_EmptyVariableName_Throws_WS038()
    {
        var act = () => Path("${}");
        act.Should().Throw<ManifestException>().WithMessage("*WS038*empty*");
    }

    [Fact]
    public void Expand_StrayDollar_Throws_WS038()
    {
        var act = () => Path("price-$50");
        act.Should().Throw<ManifestException>().WithMessage("*WS038*stray*");
    }

    [Fact]
    public void Expand_InvalidVarChar_Throws_WS038()
    {
        var act = () => Path("${a-b}");
        act.Should().Throw<ManifestException>().WithMessage("*WS038*invalid character*");
    }

    // ── 错误：变量出现在不允许字段（WS039）────────────────────────────────────

    [Fact]
    public void Expand_ScalarFieldWithVariable_Throws_WS039()
    {
        var act = () => Scalar("${profile}");
        act.Should().Throw<ManifestException>().WithMessage("*WS039*not allowed*");
    }

    [Fact]
    public void Expand_ScalarFieldWithoutVariable_Passes()
    {
        Scalar("0.1.0").Should().Be("0.1.0");
        Scalar("hello").Should().Be("hello");
    }

    [Fact]
    public void Expand_ScalarFieldWithEscapedDollar_StillReturned()
    {
        // 标量字段不展开，原样返回
        Scalar("$$literal").Should().Be("$$literal");
    }

    // ── 边界 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Expand_EmptyString() =>
        Path("").Should().Be("");

    [Fact]
    public void Expand_NoVariables() =>
        Path("plain/path/without/vars").Should().Be("plain/path/without/vars");
}
