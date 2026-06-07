using FluentAssertions;
using Xunit;
using Z42.Driver;

namespace Z42.Tests;

/// fix-run-forward-script-args (2026-06-07): `z42c run app.z42 -- a b c` must
/// forward everything after the first `--` to the executed program. The split
/// happens in Program.Main (before System.CommandLine parses, which in beta4
/// prints help on post-`--` tokens). These cover the pure split helper.
public class RunForwardedArgsTests
{
    [Fact]
    public void DashDash_SplitsAtFirstSeparator()
    {
        var (pre, fwd) = BuildCommand.SplitForwardedArgs(
            ["run", "app.z42", "--", "a", "b", "c"]);
        pre.Should().Equal("run", "app.z42");
        fwd.Should().Equal("a", "b", "c");
    }

    [Fact]
    public void NoDashDash_NothingForwarded()
    {
        var (pre, fwd) = BuildCommand.SplitForwardedArgs(["run", "app.z42"]);
        pre.Should().Equal("run", "app.z42");
        fwd.Should().BeEmpty();
    }

    [Fact]
    public void TrailingDashDash_EmptyForwarded()
    {
        var (pre, fwd) = BuildCommand.SplitForwardedArgs(["run", "app.z42", "--"]);
        pre.Should().Equal("run", "app.z42");
        fwd.Should().BeEmpty();
    }

    [Fact]
    public void OnlyFirstDashDashSplits_SecondIsForwardedVerbatim()
    {
        // A second `--` belongs to the program, not z42c.
        var (pre, fwd) = BuildCommand.SplitForwardedArgs(
            ["run", "app.z42", "--", "x", "--", "y"]);
        pre.Should().Equal("run", "app.z42");
        fwd.Should().Equal("x", "--", "y");
    }

    [Fact]
    public void Empty_NoForwarded()
    {
        var (pre, fwd) = BuildCommand.SplitForwardedArgs([]);
        pre.Should().BeEmpty();
        fwd.Should().BeEmpty();
    }
}
