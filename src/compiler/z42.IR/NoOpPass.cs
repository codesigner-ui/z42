namespace Z42.IR;

/// <summary>
/// Identity transformation pass — returns the input module unchanged.
///
/// <para>Purpose: keeps <see cref="IrPassManager"/> exercised by tests even
/// when no real optimization passes are registered. Acts as a documented
/// template that future optimization-pass authors can copy.</para>
///
/// <para>Use it in tests:</para>
/// <code>
///   var pm = new IrPassManager().Add(new NoOpPass());
///   var result = pm.RunAll(module);
///   // result is reference-equal to module
/// </code>
///
/// docs/review.md Part 6 F5 #7 placeholder (2026-05-24).
/// </summary>
public sealed class NoOpPass : IIrPass
{
    public string Name => "no-op";

    public IrModule Run(IrModule input, PassContext ctx) => input;
}
