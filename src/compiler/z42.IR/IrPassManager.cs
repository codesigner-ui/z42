namespace Z42.IR;

/// <summary>
/// Compilation profile hint for pass selection.
/// Passes can inspect this to decide whether to run or how aggressively to optimize.
/// </summary>
public enum CompileProfile { Debug, Release }

/// <summary>
/// Context passed to each IR pass — carries profile and diagnostics sink.
/// Extensible: add fields here as passes require more context.
/// </summary>
public sealed class PassContext(CompileProfile Profile)
{
    public CompileProfile Profile { get; } = Profile;
}

/// <summary>
/// A single IR-to-IR transformation pass.
/// </summary>
public interface IIrPass
{
    /// Human-readable pass name (for logging / diagnostics).
    string Name { get; }

    /// Transform the module in-place or return a new module.
    IrModule Run(IrModule input, PassContext ctx);
}

/// <summary>
/// Composable pipeline of <see cref="IIrPass"/> transformations.
///
/// Usage:
/// <code>
///   var pm = new IrPassManager(CompileProfile.Release)
///       .Add(new ConstantFoldingPass())
///       .Add(new DeadCodeEliminationPass());
///   var optimized = pm.RunAll(module);
/// </code>
///
/// Currently ships with 0 passes — the framework is in place for future optimization work.
/// </summary>
public sealed class IrPassManager
{
    private readonly List<IIrPass> _passes = [];
    private readonly PassContext _ctx;

    public IrPassManager(CompileProfile profile = CompileProfile.Debug)
    {
        _ctx = new PassContext(profile);
    }

    /// Add a pass to the end of the pipeline. Returns this for fluent chaining.
    public IrPassManager Add(IIrPass pass)
    {
        _passes.Add(pass);
        return this;
    }

    /// Run all passes in order, threading the module through each.
    public IrModule RunAll(IrModule module)
    {
        foreach (var pass in _passes)
            module = pass.Run(module, _ctx);
        return module;
    }

    /// Number of registered passes.
    public int Count => _passes.Count;
}
