using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// add-warn-captured-value-snapshot-assign (2026-05-30): W0604 fires
/// when a closure body writes to a captured value-type variable. The
/// write only mutates the closure's local snapshot (closure.md §4.1) —
/// outer scope keeps its original value, so the assignment is almost
/// always a silent bug. Spec §4.4 says to use a class wrap or array
/// cell to share state; this warning nudges callers in that direction.
public sealed class WarnCapturedValueSnapshotAssignTests
{
    private static DiagnosticBag Check(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        _ = new TypeChecker(diags, LanguageFeatures.Phase1).Check(cu);
        return diags;
    }

    private static IEnumerable<Diagnostic> W0604(DiagnosticBag d) =>
        d.All.Where(x => x.Code == DiagnosticCodes.CapturedValueSnapshotAssign);

    // ── Positive: value-type capture writes warn ──────────────────────────

    [Fact]
    public void BoolCapture_WrittenInLambda_Warns()
    {
        var diags = Check("""
            void Main() {
                var x = false;
                () -> void a = () => { x = true; };
            }
            """);
        diags.HasErrors.Should().BeFalse();
        W0604(diags).Should().ContainSingle()
            .Which.Message.Should().Contain("`x`");
    }

    [Fact]
    public void IntCompoundAssign_WrittenInLambda_Warns()
    {
        var diags = Check("""
            void Main() {
                var n = 0;
                () -> void a = () => { n = n + 1; };
            }
            """);
        W0604(diags).Should().ContainSingle()
            .Which.Message.Should().Contain("`n`");
    }

    // ── Negative: reference / non-capture writes do NOT warn ─────────────

    [Fact]
    public void ClassFieldWriteThroughCapture_NoWarn()
    {
        // `c.n = ...` writes a FIELD on the captured object, not the
        // captured slot itself. BoundAssign.Target is BoundMember here,
        // not BoundCapturedIdent — so W0604 should stay silent.
        var diags = Check("""
            class Counter { public int n; Counter() { this.n = 0; } }
            void Main() {
                var c = new Counter();
                () -> void a = () => { c.n = c.n + 1; };
            }
            """);
        diags.HasErrors.Should().BeFalse();
        W0604(diags).Should().BeEmpty();
    }

    [Fact]
    public void ArrayCellElementWrite_NoWarn()
    {
        // The canonical "share a primitive across the closure" pattern —
        // capture a 1-element array. Assigning the element (BoundIndex
        // target) is allowed and silent; `cell` itself is not reseated.
        var diags = Check("""
            void Main() {
                var cell = new bool[1];
                cell[0] = false;
                () -> void a = () => { cell[0] = true; };
            }
            """);
        diags.HasErrors.Should().BeFalse();
        W0604(diags).Should().BeEmpty();
    }

    [Fact]
    public void LambdaLocalVarReassign_NoWarn()
    {
        // A var declared INSIDE the lambda body is not a capture; writing
        // it is normal local mutation and must not warn.
        var diags = Check("""
            void Main() {
                () -> int a = () => {
                    var local = 5;
                    local = 10;
                    return local;
                };
            }
            """);
        diags.HasErrors.Should().BeFalse();
        W0604(diags).Should().BeEmpty();
    }

    [Fact]
    public void TopLevelFunctionVar_NoCapture_NoWarn()
    {
        // Plain function body — no lambda anywhere. Capture analysis
        // never even runs, so W0604 must stay silent.
        var diags = Check("""
            void Main() {
                var g = false;
                g = true;
            }
            """);
        diags.HasErrors.Should().BeFalse();
        W0604(diags).Should().BeEmpty();
    }

    // ── Nested lambdas: inner write to outer-captured ─────────────────────

    [Fact]
    public void NestedLambda_InnerWritesOuterCapture_Warns()
    {
        // `x` is captured by `outer` (used in inner's body) and by `inner`
        // (assigned there). The write is on inner's BoundCapturedIdent,
        // so W0604 fires once at the assignment span.
        var diags = Check("""
            void Main() {
                var x = 0;
                () -> void outer = () => {
                    () -> void inner = () => { x = 1; };
                    inner();
                };
            }
            """);
        diags.HasErrors.Should().BeFalse();
        W0604(diags).Should().ContainSingle()
            .Which.Message.Should().Contain("`x`");
    }
}
