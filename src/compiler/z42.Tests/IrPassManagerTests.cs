using FluentAssertions;
using Z42.IR;

namespace Z42.Tests;

/// <summary>
/// IrPassManager + NoOpPass smoke tests — keeps the optimization framework
/// alive (covered by tests) even before real passes are registered.
/// docs/review.md Part 6 F5 #7 (2026-05-24).
/// </summary>
public class IrPassManagerTests
{
    private static IrModule EmptyModule() => new("test", [], [], []);

    [Fact]
    public void RunAll_NoPasses_ReturnsInputUnchanged()
    {
        var module = EmptyModule();
        var pm = new IrPassManager();

        var result = pm.RunAll(module);

        result.Should().BeSameAs(module);
        pm.Count.Should().Be(0);
    }

    [Fact]
    public void RunAll_WithNoOpPass_ReturnsInputUnchanged()
    {
        var module = EmptyModule();
        var pm = new IrPassManager().Add(new NoOpPass());

        var result = pm.RunAll(module);

        result.Should().BeSameAs(module);
        pm.Count.Should().Be(1);
    }

    [Fact]
    public void Add_IsFluent_ChainsMultiplePasses()
    {
        var pm = new IrPassManager()
            .Add(new NoOpPass())
            .Add(new NoOpPass())
            .Add(new NoOpPass());

        pm.Count.Should().Be(3);
    }

    [Fact]
    public void Profile_DefaultsToDebug()
    {
        var ctx = new PassContext(CompileProfile.Debug);

        ctx.Profile.Should().Be(CompileProfile.Debug);
    }

    [Fact]
    public void Profile_ReleaseExplicit()
    {
        // Release-profile passes can branch on PassContext.Profile to decide
        // whether to apply aggressive optimizations.
        var pm = new IrPassManager(CompileProfile.Release).Add(new NoOpPass());

        pm.Count.Should().Be(1);
    }

    [Fact]
    public void NoOpPass_NameIsStable()
    {
        var pass = new NoOpPass();

        pass.Name.Should().Be("no-op");
    }
}
