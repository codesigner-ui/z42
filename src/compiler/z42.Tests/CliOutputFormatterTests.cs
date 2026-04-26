using FluentAssertions;
using Z42.Driver;
using Z42.Project;

namespace Z42.Tests;

/// 验证 CliOutputFormatter 在 pretty=false 时返回原 message；pretty=true 时
/// 仍包含原始内容（颜色/格式化是叠加而非替换）。
public sealed class CliOutputFormatterTests
{
    [Fact]
    public void Format_NoPretty_ReturnsRawMessage()
    {
        var ex = Z42Errors.PolicyViolation("build.out_dir", "dist", "custom", "/r/z42.workspace.toml", "/r/libs/foo/foo.z42.toml");
        var output = CliOutputFormatter.Format(ex, pretty: false);
        output.Should().Be(ex.Message);
    }

    [Fact]
    public void Format_PreservesContent()
    {
        var ex = Z42Errors.UnknownTemplateVariable("/r/foo.toml", "[test]", "unknown");
        var output = CliOutputFormatter.Format(ex, pretty: true);
        // 即使彩色化，原始内容必须保留
        output.Should().Contain("WS037").And.Contain("unknown");
    }

    [Fact]
    public void Format_NoColor_EnvVarSuppressesAnsi()
    {
        Environment.SetEnvironmentVariable("NO_COLOR", "1");
        try
        {
            var ex = Z42Errors.CircularInclude(new[] { "a.toml", "b.toml", "a.toml" });
            var output = CliOutputFormatter.Format(ex, pretty: true);
            // NO_COLOR 时返回原始 message（无 ANSI 转义）
            output.Should().NotContain("\u001b[");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NO_COLOR", null);
        }
    }
}
