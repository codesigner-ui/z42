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
    ///
    /// split-symbol-from-type Phase 5 (2026-05-10): for class methods,
    /// `IMethodSymbol.TestAttributes` is the canonical source of attribute
    /// information. The cu.Classes walk is kept for source-order iteration +
    /// FunctionDecl access (ValidateFunction needs the AST node for span /
    /// signature checks); but the `TestAttributes` list returned by both
    /// `m.TestAttributes` and `symbol.TestAttributes` is the SAME list reference
    /// (populated by SymbolCollector at construction). The walk just confirms:
    /// no AST methods exist that bypass Symbol-layer attribute exposure.
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
        bool hasTimeout     = false;
        TestAttribute? skipAttr        = null;
        TestAttribute? shouldThrowAttr = null;
        TestAttribute? timeoutAttr     = null;

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
                case "Timeout":
                    // add-test-timeout-attribute (2026-05-30): detect duplicate
                    // [Timeout] eagerly here rather than waiting for the
                    // post-loop check, so the diagnostic is anchored at the
                    // SECOND occurrence (closer to user error).
                    if (hasTimeout)
                    {
                        diags.Error(
                            DiagnosticCodes.TimeoutValueInvalid,
                            $"function `{fn.Name}` `[Timeout]` applied more than once on the same method",
                            attr.Span);
                    }
                    hasTimeout = true;
                    timeoutAttr = attr;
                    break;
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
            // add-test-timeout-attribute (2026-05-30): named-arg value is
            // now a typed AttributeArg discriminator. RequireStringArg
            // emits a clear "must be string" diagnostic on wrong-type, then
            // returns null so the empty/missing branch reports the
            // original "reason: required" message.
            var reason = RequireStringArg(
                skipAttr, "reason", fn.Name,
                DiagnosticCodes.SkipReasonMissing, diags);
            if (string.IsNullOrEmpty(reason))
            {
                diags.Error(
                    DiagnosticCodes.SkipReasonMissing,
                    $"function `{fn.Name}` `[Skip(...)]` requires a non-empty `reason:` argument explaining why the test is skipped",
                    skipAttr.Span);
            }
        }

        // ── E0916 [Timeout(milliseconds: ...)] validation ─────────────────
        // add-test-timeout-attribute (2026-05-30)
        if (hasTimeout && timeoutAttr is not null)
        {
            ValidateTimeoutAttribute(fn, timeoutAttr, hasTest, hasBenchmark, diags);
        }

        // ── E0911 [Test] signature ────────────────────────────────────────
        if (hasTest && !hasBenchmark) // skip sig check when E0911-conflict already emitted
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

    /// add-test-timeout-attribute (2026-05-30) — full E0916 check for
    /// `[Timeout(milliseconds: <int>)]`. Emits one diagnostic per
    /// independent failure (caller is OK seeing multiple — e.g. "no [Test]"
    /// AND "value out of range" together) so the user can fix everything
    /// in one pass.
    private static void ValidateTimeoutAttribute(
        FunctionDecl fn, TestAttribute attr, bool hasTest, bool hasBenchmark,
        DiagnosticBag diags)
    {
        if (!hasTest && !hasBenchmark)
        {
            diags.Error(
                DiagnosticCodes.TimeoutValueInvalid,
                $"function `{fn.Name}` `[Timeout(...)]` requires `[Test]` or `[Benchmark]` on the same method",
                attr.Span);
        }

        if (attr.NamedArgs is null
            || !attr.NamedArgs.TryGetValue("milliseconds", out var arg))
        {
            diags.Error(
                DiagnosticCodes.TimeoutValueInvalid,
                $"function `{fn.Name}` `[Timeout(...)]` requires a single named arg \"milliseconds: <int>\"",
                attr.Span);
            return;
        }

        if (arg is not AttributeArgInt intArg)
        {
            diags.Error(
                DiagnosticCodes.TimeoutValueInvalid,
                $"function `{fn.Name}` `[Timeout(milliseconds: ...)]` value must be an integer literal (got string)",
                arg.Span);
            return;
        }

        if (intArg.Value <= 0)
        {
            diags.Error(
                DiagnosticCodes.TimeoutValueInvalid,
                $"function `{fn.Name}` `[Timeout(milliseconds: ...)]` value must be > 0 (got {intArg.Value})",
                arg.Span);
        }
        else if (intArg.Value > int.MaxValue)
        {
            diags.Error(
                DiagnosticCodes.TimeoutValueInvalid,
                $"function `{fn.Name}` `[Timeout(milliseconds: ...)]` value must fit in i32 (got {intArg.Value})",
                arg.Span);
        }
    }

    /// Read a string-valued named arg from a test attribute, reporting a
    /// type-mismatch diagnostic under <paramref name="code"/> when the arg
    /// is present but not an <see cref="AttributeArgString"/>.
    ///
    /// Returns:
    ///   - the unwrapped string when present + correctly typed
    ///   - <c>null</c> when the key is absent (caller handles "missing")
    ///   - <c>null</c> + emits diagnostic when present but wrong-type
    ///
    /// add-test-timeout-attribute (2026-05-30): introduced alongside the
    /// AttributeArg discriminator so existing string-arg consumers keep
    /// reading their values via a single uniform helper. Future
    /// int/float/ident variants will get parallel <c>RequireXxxArg</c>
    /// helpers as needed.
    private static string? RequireStringArg(
        TestAttribute attr, string key, string fnName,
        string code, DiagnosticBag diags)
    {
        if (attr.NamedArgs is null) return null;
        if (!attr.NamedArgs.TryGetValue(key, out var arg)) return null;
        if (arg is AttributeArgString s) return s.Value;
        diags.Error(code,
            $"function `{fnName}` `[{attr.Name}({key}:...)]` value must be a string literal",
            arg.Span);
        return null;
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
