using FluentAssertions;
using Z42.Core.Diagnostics;

namespace Z42.Tests;

/// add-generic-func-constraint (2026-05-11): `where T: Func<...>` / `where T: (T)->R`
/// function-type constraint tests.
///
/// Notes: the test helper does NOT load stdlib, so we use local `delegate`
/// declarations + `(T) -> R` literal forms rather than referencing the stdlib
/// `Func<>` / `Action<>` / `Predicate<>` names.
public partial class TypeCheckerTests
{
    // ── Literal-form constraint ─────────────────────────────────────────────

    [Fact]
    public void FuncConstraint_Literal_Passes()
    {
        var src = @"
R Apply<T, R>(T f, int x) where T: (int) -> R { return f(x); }
void Main() { }";
        Check(src).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void FuncConstraint_LiteralVoid_Passes()
    {
        var src = @"
void Run<T>(T h, int x) where T: (int) -> void { h(x); }
void Main() { }";
        Check(src).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void FuncConstraint_LiteralZeroArg_Passes()
    {
        var src = @"
void RunVoid<T>(T h) where T: () -> void { h(); }
void Main() { }";
        Check(src).HasErrors.Should().BeFalse();
    }

    // ── Named-form constraint via user-defined delegate ─────────────────────

    [Fact]
    public void FuncConstraint_NamedDelegate_Passes()
    {
        var src = @"
delegate R MyFunc<T, R>(T x);
R Apply<T, R>(T f, int x) where T: MyFunc<int, R> { return f(x); }
void Main() { }";
        Check(src).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void FuncConstraint_NamedActionDelegate_Passes()
    {
        var src = @"
delegate void MyAction<T>(T x);
void Run<T>(T h, int x) where T: MyAction<int> { h(x); }
void Main() { }";
        Check(src).HasErrors.Should().BeFalse();
    }

    // ── Body call dispatch ──────────────────────────────────────────────────

    [Fact]
    public void FuncConstraint_BodyCall_WithConstraint_Allowed()
    {
        // body calls `f(x)` where T has a func constraint — should typecheck.
        var src = @"
R Call<T, R>(T f, int n) where T: (int) -> R { return f(n); }
void Main() { }";
        Check(src).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void FuncConstraint_BodyCall_NoConstraint_Rejected()
    {
        // Without a func constraint, calling `f(x)` should fail typechecking.
        var src = @"
R Call<T, R>(T f, int n) { return f(n); }
void Main() { }";
        Check(src).HasErrors.Should().BeTrue(because: "T without func constraint is not callable");
    }

    // ── v1 combination rule (E0423) ──────────────────────────────────────────
    //
    // v1 limit: only a single, standalone func constraint per type parameter.
    // The `+` separator within a single constraint list triggers E0423 when it
    // mixes a func signature with another shape. Multi-entry combination
    // (`T: A, T: B`) is a separate parser path not exercised here — see
    // proposal Out of Scope #3.

    [Fact]
    public void FuncConstraint_PlusInterface_InSameEntry_Rejected()
    {
        // Same entry: `T: (int)->int + ICloneable` should hit E0423.
        var src = @"
public interface ICloneable { void Clone(); }
void X<T>() where T: (int) -> int + ICloneable { }
void Main() { }";
        var diags = Check(src);
        diags.HasErrors.Should().BeTrue();
        diags.All.Any(d => d.Code == DiagnosticCodes.InvalidFuncConstraint).Should().BeTrue();
    }

    [Fact]
    public void FuncConstraint_PlusFunc_InSameEntry_Rejected()
    {
        // Two func signatures in the same constraint list — `multiple function-type
        // constraints` E0423.
        var src = @"
void X<T>() where T: (int) -> int + (string) -> bool { }
void Main() { }";
        var diags = Check(src);
        diags.HasErrors.Should().BeTrue();
        diags.All.Any(d => d.Code == DiagnosticCodes.InvalidFuncConstraint).Should().BeTrue();
    }
}
