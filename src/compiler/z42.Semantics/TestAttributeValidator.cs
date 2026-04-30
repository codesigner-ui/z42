using Z42.Core.Diagnostics;
using Z42.IR;
using Z42.Syntax.Parser;

namespace Z42.Semantics;

/// <summary>
/// Spec R4.A — validate <c>z42.test.*</c> attribute usage at compile time.
///
/// Runs as a pass between <c>TypeChecker</c> and <c>IrGen</c>. Examines every
/// <see cref="FunctionDecl"/> with non-empty <see cref="FunctionDecl.TestAttributes"/>
/// and reports E0911 / E0914 / E0915 on misuse:
///
/// - <b>E0911</b> [Test] / [Benchmark]: signature must be `fn() -&gt; void`,
///   no generic type parameters, no instance method receiver. Plus mutual
///   exclusion: a function cannot be both [Test] and [Benchmark].
/// - <b>E0912</b> [Benchmark]: partial signature check (void return + no
///   generics). Full "first param must be <c>Bencher</c>" check pending
///   R2.C (closure dependency).
/// - <b>E0913</b> [ShouldThrow&lt;E&gt;]: reserved for R4.B when generic
///   attribute syntax lands.
/// - <b>E0914</b> [Skip] / [Ignore]: [Skip] needs a non-empty <c>reason</c>
///   named arg; both must be paired with [Test] / [Benchmark].
/// - <b>E0915</b> [Setup] / [Teardown]: same signature contract as [Test];
///   mutually exclusive with [Test] / [Benchmark] / [Skip] / [Ignore].
///
/// Reasoning: catching bad attribute use at compile time turns runtime
/// "function not callable" mysteries into clear localized diagnostics.
/// </summary>
public static class TestAttributeValidator
{
    /// <summary>
    /// Validate test attributes across an entire compilation unit. Reports any
    /// violations into <paramref name="diags"/>; returns silently. Caller checks
    /// <see cref="DiagnosticBag.HasErrors"/> after.
    /// </summary>
    public static void Validate(CompilationUnit cu, DiagnosticBag diags)
    {
        foreach (var fn in cu.Functions)
            ValidateFunction(fn, diags);

        foreach (var cls in cu.Classes)
            foreach (var m in cls.Methods)
                ValidateFunction(m, diags);

        foreach (var impl in cu.Impls)
            foreach (var m in impl.Methods)
                ValidateFunction(m, diags);
    }

    private static void ValidateFunction(FunctionDecl fn, DiagnosticBag diags)
    {
        if (fn.TestAttributes is null || fn.TestAttributes.Count == 0)
            return;

        // Reduce attributes to a per-function classification.
        bool hasTest      = false;
        bool hasBenchmark = false;
        bool hasSetup     = false;
        bool hasTeardown  = false;
        bool hasSkip      = false;
        bool hasIgnore    = false;
        TestAttribute? skipAttr = null;

        foreach (var attr in fn.TestAttributes)
        {
            switch (attr.Name)
            {
                case "Test":      hasTest = true;      break;
                case "Benchmark": hasBenchmark = true; break;
                case "Setup":     hasSetup = true;     break;
                case "Teardown":  hasTeardown = true;  break;
                case "Skip":      hasSkip = true; skipAttr = attr; break;
                case "Ignore":    hasIgnore = true;    break;
            }
        }

        // ── E0911 [Test] vs [Benchmark] mutual exclusion ──────────────────
        if (hasTest && hasBenchmark)
        {
            diags.Error(
                DiagnosticCodes.TestSignatureInvalid,
                $"function `{fn.Name}` cannot be decorated with both `[Test]` and `[Benchmark]`",
                fn.Span);
        }

        // ── E0915 [Setup]/[Teardown] mutually exclusive with test markers ─
        if ((hasSetup || hasTeardown) &&
            (hasTest || hasBenchmark || hasSkip || hasIgnore))
        {
            string conflict = hasSetup ? "[Setup]" : "[Teardown]";
            diags.Error(
                DiagnosticCodes.SetupTeardownSignatureInvalid,
                $"function `{fn.Name}` cannot combine `{conflict}` with `[Test]`/`[Benchmark]`/`[Skip]`/`[Ignore]`; setup/teardown are infrastructure hooks, not tests",
                fn.Span);
        }

        // ── E0914 [Skip]/[Ignore] standalone (without [Test]/[Benchmark]) ─
        if ((hasSkip || hasIgnore) && !(hasTest || hasBenchmark))
        {
            string standalone = hasSkip ? "[Skip]" : "[Ignore]";
            diags.Error(
                DiagnosticCodes.SkipReasonMissing,
                $"function `{fn.Name}` has `{standalone}` but no `[Test]` or `[Benchmark]` — `{standalone}` is a modifier and requires a primary test attribute",
                fn.Span);
        }

        // ── E0914 [Skip] missing/empty reason ─────────────────────────────
        if (hasSkip && skipAttr is not null)
        {
            string? reason = null;
            skipAttr.NamedArgs?.TryGetValue("reason", out reason);
            if (string.IsNullOrEmpty(reason))
            {
                diags.Error(
                    DiagnosticCodes.SkipReasonMissing,
                    $"function `{fn.Name}` `[Skip(...)]` requires a non-empty `reason:` argument explaining why the test is skipped",
                    skipAttr.Span);
            }
        }

        // ── E0911 [Test] signature ────────────────────────────────────────
        if (hasTest && !hasBenchmark) // skip Z0911 sig check when E0911-conflict already emitted
        {
            ValidateNoArgVoidSignature(fn, "[Test]", DiagnosticCodes.TestSignatureInvalid, diags);
        }

        // ── E0912 [Benchmark] signature (partial; full check needs Bencher type — R2.C) ──
        if (hasBenchmark && !hasTest)
        {
            ValidateBenchmarkPartialSignature(fn, diags);
        }

        // ── E0915 [Setup]/[Teardown] signature ────────────────────────────
        if (hasSetup || hasTeardown)
        {
            string label = hasSetup ? "[Setup]" : "[Teardown]";
            ValidateNoArgVoidSignature(fn, label, DiagnosticCodes.SetupTeardownSignatureInvalid, diags);
        }
    }

    /// Common shape for [Test] / [Setup] / [Teardown]: fn() -> void, no
    /// generics, no instance receiver. Reports under <paramref name="code"/>.
    private static void ValidateNoArgVoidSignature(
        FunctionDecl fn, string attrLabel, string code, DiagnosticBag diags)
    {
        if (fn.Params.Count != 0)
        {
            diags.Error(code,
                $"function `{fn.Name}` decorated with `{attrLabel}` must take no parameters (got {fn.Params.Count})",
                fn.Span);
        }

        if (fn.ReturnType is not VoidType)
        {
            diags.Error(code,
                $"function `{fn.Name}` decorated with `{attrLabel}` must return void",
                fn.Span);
        }

        if (fn.TypeParams is { Count: > 0 })
        {
            diags.Error(code,
                $"function `{fn.Name}` decorated with `{attrLabel}` must not be generic",
                fn.Span);
        }
    }

    /// [Benchmark] partial signature check (R4.A scope). The full "first param
    /// must be Bencher" check requires the Bencher type from z42.test, which
    /// is closure-dependent and lands in R2.C. For now we only check the
    /// always-applicable rules: void return + no generics.
    private static void ValidateBenchmarkPartialSignature(FunctionDecl fn, DiagnosticBag diags)
    {
        if (fn.ReturnType is not VoidType)
        {
            diags.Error(DiagnosticCodes.BenchmarkSignatureInvalid,
                $"function `{fn.Name}` decorated with `[Benchmark]` must return void",
                fn.Span);
        }

        if (fn.TypeParams is { Count: > 0 })
        {
            diags.Error(DiagnosticCodes.BenchmarkSignatureInvalid,
                $"function `{fn.Name}` decorated with `[Benchmark]` must not be generic",
                fn.Span);
        }
        // First-parameter-is-Bencher check pending R2.C.
    }
}
