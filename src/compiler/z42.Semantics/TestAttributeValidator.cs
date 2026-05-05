using Z42.Core.Diagnostics;
using Z42.IR;
using Z42.Semantics.TypeCheck;
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
    public static void Validate(CompilationUnit cu, SemanticModel sem, DiagnosticBag diags)
    {
        foreach (var fn in cu.Functions)
            ValidateFunction(fn, sem, diags);

        foreach (var cls in cu.Classes)
            foreach (var m in cls.Methods)
                ValidateFunction(m, sem, diags);

        foreach (var impl in cu.Impls)
            foreach (var m in impl.Methods)
                ValidateFunction(m, sem, diags);
    }

    private static void ValidateFunction(FunctionDecl fn, SemanticModel sem, DiagnosticBag diags)
    {
        if (fn.TestAttributes is null || fn.TestAttributes.Count == 0)
            return;

        // Reduce attributes to a per-function classification.
        bool hasTest        = false;
        bool hasBenchmark   = false;
        bool hasSetup       = false;
        bool hasTeardown    = false;
        bool hasSkip        = false;
        bool hasIgnore      = false;
        bool hasShouldThrow = false;
        TestAttribute? skipAttr        = null;
        TestAttribute? shouldThrowAttr = null;

        foreach (var attr in fn.TestAttributes)
        {
            switch (attr.Name)
            {
                case "Test":         hasTest = true;      break;
                case "Benchmark":    hasBenchmark = true; break;
                case "Setup":        hasSetup = true;     break;
                case "Teardown":     hasTeardown = true;  break;
                case "Skip":         hasSkip = true; skipAttr = attr; break;
                case "Ignore":       hasIgnore = true;    break;
                case "ShouldThrow":  hasShouldThrow = true; shouldThrowAttr = attr; break;
            }
        }

        // ── E0913 type arg on attribute that does not accept it ────────────
        // Parser accepts `[Name<T>]` for any z42.test.* attribute name;
        // semantics gates which names actually permit it.
        foreach (var attr in fn.TestAttributes)
        {
            if (attr.TypeArg is not null && attr.Name != "ShouldThrow")
            {
                diags.Error(
                    DiagnosticCodes.ShouldThrowTypeInvalid,
                    $"function `{fn.Name}` `[{attr.Name}]` does not accept a type argument; `<...>` syntax is reserved for `[ShouldThrow<E>]`",
                    attr.Span);
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

        // ── E0914 [Skip]/[Ignore]/[ShouldThrow] standalone (without [Test]/[Benchmark]) ─
        if ((hasSkip || hasIgnore || hasShouldThrow) && !(hasTest || hasBenchmark))
        {
            string standalone = hasSkip ? "[Skip]"
                              : hasIgnore ? "[Ignore]"
                              : "[ShouldThrow]";
            diags.Error(
                DiagnosticCodes.SkipReasonMissing,
                $"function `{fn.Name}` has `{standalone}` but no `[Test]` or `[Benchmark]` — `{standalone}` is a modifier and requires a primary test attribute",
                fn.Span);
        }

        // ── E0913 [ShouldThrow<E>] type validation ────────────────────────
        if (hasShouldThrow && shouldThrowAttr is not null)
        {
            ValidateShouldThrowType(fn, shouldThrowAttr, sem, diags);
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
            ValidateBenchmarkFullSignature(fn, diags);
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

    /// R4.B — [ShouldThrow&lt;E&gt;] type checks. Three rules:
    ///   1. type argument required (must not be bare `[ShouldThrow]`)
    ///   2. type must be declared (visible class in this CU or imported)
    ///   3. type must derive from `Exception` (transitively via BaseClassName chain)
    private static void ValidateShouldThrowType(
        FunctionDecl fn, TestAttribute attr, SemanticModel sem, DiagnosticBag diags)
    {
        if (attr.TypeArg is null)
        {
            diags.Error(DiagnosticCodes.ShouldThrowTypeInvalid,
                $"function `{fn.Name}` `[ShouldThrow]` requires a single type argument naming the expected exception type (e.g. `[ShouldThrow<TestFailure>]`)",
                attr.Span);
            return;
        }

        // Resolve short name → Z42ClassType. SemanticModel.Classes is keyed
        // by short name (parallels how `class X : Exception` base resolution
        // looks up "Exception" — see FunctionEmitter.ResolveBaseCtorKey).
        if (!sem.Classes.TryGetValue(attr.TypeArg, out var clsType))
        {
            diags.Error(DiagnosticCodes.ShouldThrowTypeInvalid,
                $"function `{fn.Name}` `[ShouldThrow<{attr.TypeArg}>]` references unknown type `{attr.TypeArg}`",
                attr.Span);
            return;
        }

        if (!InheritsFromException(clsType, sem))
        {
            diags.Error(DiagnosticCodes.ShouldThrowTypeInvalid,
                $"function `{fn.Name}` `[ShouldThrow<{attr.TypeArg}>]` type must derive from `Exception`",
                attr.Span);
        }
    }

    /// Walks <c>BaseClassName</c> chain looking for "Exception". Cycles guarded
    /// by a visited set (defensive — TypeChecker should have rejected cyclic
    /// inheritance earlier, but this keeps the validator honest).
    private static bool InheritsFromException(Z42ClassType type, SemanticModel sem)
    {
        var visited = new HashSet<string>();
        var current = type;
        while (current is not null && visited.Add(current.Name))
        {
            if (current.Name == "Exception")
                return true;
            if (current.BaseClassName is null)
                return false;

            // BaseClassName may be qualified ("Std.Exception") — strip to short name.
            var shortKey = current.BaseClassName.Contains('.')
                ? current.BaseClassName[(current.BaseClassName.LastIndexOf('.') + 1)..]
                : current.BaseClassName;
            if (shortKey == "Exception")
                return true;
            sem.Classes.TryGetValue(shortKey, out current);
        }
        return false;
    }

    /// R2 完整版 — full [Benchmark] signature check.
    ///
    /// Rules:
    ///   1. return type is void
    ///   2. not generic
    ///   3. exactly one parameter, of type `Bencher` (short name match;
    ///      Bencher class lives in `Std.Test`)
    private static void ValidateBenchmarkFullSignature(FunctionDecl fn, DiagnosticBag diags)
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

        if (fn.Params.Count == 0)
        {
            diags.Error(DiagnosticCodes.BenchmarkSignatureInvalid,
                $"function `{fn.Name}` decorated with `[Benchmark]` must have first parameter of type `Bencher`",
                fn.Span);
            return;
        }

        if (fn.Params.Count > 1)
        {
            diags.Error(DiagnosticCodes.BenchmarkSignatureInvalid,
                $"function `{fn.Name}` decorated with `[Benchmark]` must take exactly one parameter (the Bencher); got {fn.Params.Count}",
                fn.Span);
            // Don't return — still validate the first param's type below.
        }

        var firstParamTypeName = ExtractTypeName(fn.Params[0].Type);
        if (firstParamTypeName != "Bencher")
        {
            diags.Error(DiagnosticCodes.BenchmarkSignatureInvalid,
                $"function `{fn.Name}` decorated with `[Benchmark]` first parameter must be `Bencher`, got `{firstParamTypeName}`",
                fn.Params[0].Span);
        }
    }

    /// Extract the short name from a TypeExpr — `NamedType.Name` or
    /// `GenericType.Name` (no parameter brackets). Used for shape-only checks.
    private static string ExtractTypeName(TypeExpr t) => t switch
    {
        NamedType nt   => nt.Name,
        GenericType gt => gt.Name,
        _              => "<unknown>",
    };
}
