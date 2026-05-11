using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

public sealed partial class TypeChecker
{

    // ── Argument binding (spec: add-named-arguments + define-ref-out-in-parameters) ──

    /// Bind a single callsite argument. Drops the name (TypeCheck reorder layer
    /// uses .Name separately); routes the inner value through `BindModifiedArg`
    /// when a modifier is present, otherwise plain `BindExpr`.
    private BoundExpr BindArgValue(Argument a, TypeEnv env)
    {
        return a.Value is ModifiedArg ma
            ? BindModifiedArg(ma, env)
            : BindExpr(a.Value, env);
    }

    /// spec add-named-arguments: validate positional-before-named ordering at
    /// the call site. Reports `Z1001` on each offending positional arg that
    /// follows a named one. Does not throw; caller continues binding.
    private void CheckPositionalBeforeNamed(IReadOnlyList<Argument> args)
    {
        bool sawNamed = false;
        foreach (var a in args)
        {
            if (a.Name is not null) { sawNamed = true; continue; }
            if (sawNamed)
                _diags.Error(DiagnosticCodes.PositionalAfterNamed,
                    "positional argument cannot follow a named argument",
                    a.Span);
        }
    }

    /// spec add-named-arguments: reorder named args to their parameter
    /// positions. Returns a list (length = `args.Count`) where each entry's
    /// `.Position` is the resolved 0-based param index. Out-of-order named
    /// args are reordered; positional args keep their index. Reports
    /// Z1002 / Z1003 / Z1004 for binding errors. The returned tuples are in
    /// **call-site order** (not reordered) — caller projects into positional
    /// shape via `ReorderToPositional`.
    ///
    /// `paramByName` is the callee's `paramName → index` lookup table. Null
    /// means callee unknown (imported / DepIndex fallback) — in that case
    /// named args fall through with `Position = -1`.
    private List<(Argument Arg, int Position)> ResolveArgPositions(
        IReadOnlyList<Argument> args,
        IReadOnlyDictionary<string, int>? paramByName)
    {
        var result = new List<(Argument, int)>(args.Count);
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        int positional = 0;
        foreach (var a in args)
        {
            if (a.Name is null)
            {
                result.Add((a, positional++));
                continue;
            }
            if (!seenNames.Add(a.Name))
            {
                _diags.Error(DiagnosticCodes.DuplicateArgumentName,
                    $"argument `{a.Name}` already specified",
                    a.NameSpan ?? a.Span);
                result.Add((a, -1));
                continue;
            }
            int pos = -1;
            if (paramByName is not null && !paramByName.TryGetValue(a.Name, out pos))
            {
                _diags.Error(DiagnosticCodes.UnknownArgumentName,
                    $"no parameter named `{a.Name}` on this callee",
                    a.NameSpan ?? a.Span);
                pos = -1;
            }
            else if (paramByName is null)
            {
                // Unknown callee shape — pass-through; downstream may degrade.
                pos = -1;
            }
            result.Add((a, pos));
        }
        return result;
    }

    /// spec add-named-arguments: produce a positional list of length
    /// `paramCount` from resolved (Arg, Position) pairs. Slots with no
    /// supplied arg are left `null` (caller fills from defaults). Reports
    /// `Z1004` when a position is set both positionally and by name.
    private (List<Argument?> Positional, List<BoundExpr?> Bound) ReorderToPositional(
        IReadOnlyList<(Argument Arg, int Position)> resolved,
        int paramCount,
        TypeEnv env)
    {
        var pos = new List<Argument?>(paramCount);
        var bnd = new List<BoundExpr?>(paramCount);
        for (int i = 0; i < paramCount; i++) { pos.Add(null); bnd.Add(null); }

        foreach (var (a, p) in resolved)
        {
            if (p < 0 || p >= paramCount) continue;   // already-diagnosed (Z1002/Z1003) or overflow
            if (pos[p] is not null)
            {
                _diags.Error(DiagnosticCodes.ParameterDoublySpecified,
                    a.Name is null
                        ? $"parameter {p + 1} specified by both positional and named argument"
                        : $"parameter `{a.Name}` specified by both positional and named argument",
                    a.Span);
                continue;
            }
            pos[p] = a;
            bnd[p] = BindArgValue(a, env);
        }
        return (pos, bnd);
    }

    /// spec add-named-arguments — Part 2 unified entry point.
    ///
    /// Binds raw call-site arguments to a callee's parameter shape, reordering
    /// named arguments to their declared positions and filling missing slots
    /// with bound default expressions. Returns parallel lists of
    /// (originalArg, boundExpr) indexed by parameter position.
    ///
    /// `calleeParams`: AST param list when available (local symbol with `.Decl`).
    /// When null (imported / cross-CU symbol with no AST), any named argument
    /// triggers `Z1002 UnknownArgumentName` — the call falls back to positional
    /// binding to preserve forward progress on overload / type checks.
    ///
    /// Fast path: when no argument has `.Name != null` and `calleeParams` is
    /// null OR matching arity, returns positional bind directly (no allocation).
    private (List<Argument> Orig, List<BoundExpr> Bound) BindArgsReordered(
        IReadOnlyList<Argument> rawArgs,
        IReadOnlyList<Param>?    calleeParams,
        TypeEnv env,
        Span callSpan,
        Z42FuncType?             sig = null)
    {
        bool hasNamed = false;
        for (int i = 0; i < rawArgs.Count; i++)
            if (rawArgs[i].Name is not null) { hasNamed = true; break; }

        // Fast path: positional-only — preserve existing behaviour exactly.
        if (!hasNamed)
            return (rawArgs.ToList(), rawArgs.Select(a => BindArgValue(a, env)).ToList());

        // Callee shape unknown: report Z1002 for each named arg, bind as positional.
        if (calleeParams is null)
        {
            foreach (var a in rawArgs)
                if (a.Name is not null)
                    _diags.Error(DiagnosticCodes.UnknownArgumentName,
                        $"named argument `{a.Name}` not supported for this callee (parameter names unknown)",
                        a.NameSpan ?? a.Span);
            return (rawArgs.ToList(), rawArgs.Select(a => BindArgValue(a, env)).ToList());
        }

        // Build paramName → index map; report duplicates / unknowns via resolver helpers.
        var paramByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < calleeParams.Count; i++)
            paramByName[calleeParams[i].Name] = i;

        var resolved = ResolveArgPositions(rawArgs, paramByName);
        var (origPos, boundPos) = ReorderToPositional(resolved, calleeParams.Count, env);

        // Fill missing slots from bound defaults or report Z1005.
        var origFinal  = new List<Argument>(calleeParams.Count);
        var boundFinal = new List<BoundExpr>(calleeParams.Count);
        for (int i = 0; i < calleeParams.Count; i++)
        {
            if (origPos[i] is { } a && boundPos[i] is { } b)
            {
                origFinal.Add(a);
                boundFinal.Add(b);
                continue;
            }
            var p = calleeParams[i];
            if (p.Default is null)
            {
                // spec extend-named-args-shim (2026-05-12): cross-CU imports
                // synthesize Param with Default=null (TSIG doesn't carry
                // default exprs); use Z42FuncType.MinArgCount to recognize
                // optional slots and emit a typed BoundDefault, consistent
                // with FillDefaults' cross-CU fallback (D-9).
                if (sig is not null && i >= sig.MinArgCount && i < sig.Params.Count)
                {
                    origFinal.Add(new Argument(null, new LitNullExpr(p.Span), p.Span));
                    boundFinal.Add(new BoundDefault(sig.Params[i], callSpan));
                    continue;
                }
                _diags.Error(DiagnosticCodes.MissingRequiredArgument,
                    $"missing required argument `{p.Name}` (parameter {i + 1})",
                    callSpan);
                // Placeholder so downstream Bound layer stays well-typed.
                origFinal.Add(new Argument(null, new LitNullExpr(p.Span), p.Span));
                boundFinal.Add(new BoundDefault(Z42Type.Unknown, callSpan));
                continue;
            }
            // Use the param's bound default if already computed; otherwise bind now.
            if (!_boundDefaults.TryGetValue(p, out var boundDefault))
            {
                boundDefault = BindExpr(p.Default, env);
                _boundDefaults[p] = boundDefault;
            }
            origFinal.Add(new Argument(null, p.Default, p.Span));
            boundFinal.Add(boundDefault);
        }
        return (origFinal, boundFinal);
    }

    /// Bind a callsite `ref` / `out` / `in` argument. For `out var x`, declare
    /// the local in the caller's scope with placeholder `Unknown` type; the
    /// matched parameter's type patches it in `CheckArgTypes`.
    private BoundExpr BindModifiedArg(ModifiedArg ma, TypeEnv env)
    {
        BoundOutVarDecl? boundDecl = null;
        if (ma.Modifier == ArgModifier.Out && ma.OutDecl is { } decl)
        {
            // Placeholder Unknown — patched once signature is known.
            env.Define(decl.Name, Z42Type.Unknown);
            boundDecl = new BoundOutVarDecl(decl.Name, Z42Type.Unknown, decl.Span);
        }

        var inner = BindExpr(ma.Inner, env);
        return new BoundModifiedArg(inner, ma.Modifier, boundDecl, inner.Type, ma.Span);
    }

    /// Validate that `ref` / `out` / `in` callsite usage matches the callee's
    /// parameter modifiers, that argument positions tagged as ref/out/in carry
    /// lvalue inner expressions, and that types match exactly across the
    /// modifier boundary (no implicit conversion). Patches `out var x` local
    /// types from matching parameter types.
    private void CheckArgModifiers(
        IReadOnlyList<Argument>  origArgs,
        IReadOnlyList<BoundExpr> boundArgs,
        Z42FuncType?             funcType,
        TypeEnv                  env,
        Span                     callSpan)
    {
        int n = boundArgs.Count;
        for (int i = 0; i < n; i++)
        {
            var origIsModified = origArgs[i].Value is ModifiedArg;
            var boundIsModified = boundArgs[i] is BoundModifiedArg;
            var paramMod = funcType?.ModifierAt(i) ?? ParamModifier.None;

            // Sanity: bound and orig should agree on modifier presence.
            // (Defensive — should never desync given BindModifiedArg path.)
            if (origIsModified != boundIsModified) continue;

            // Case 1: signature has modifier, callsite missing — error.
            if (paramMod != ParamModifier.None && !origIsModified)
            {
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"argument {i + 1}: parameter has `{ModifierKeyword(paramMod)}` modifier; " +
                    $"call site must use `{ModifierKeyword(paramMod)}`",
                    origArgs[i].Span);
                continue;
            }

            // Case 2: callsite has modifier, signature missing — error.
            if (paramMod == ParamModifier.None && origIsModified)
            {
                var ma = (ModifiedArg)origArgs[i].Value;
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"argument {i + 1}: parameter has no modifier; remove `{ArgModifierKeyword(ma.Modifier)}`",
                    ma.Span);
                continue;
            }

            // Case 3: both have modifiers — must match exactly.
            if (origIsModified)
            {
                var ma = (ModifiedArg)origArgs[i].Value;
                var bma = (BoundModifiedArg)boundArgs[i];
                if (!ModifiersMatch(ma.Modifier, paramMod))
                {
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"argument {i + 1}: expected `{ModifierKeyword(paramMod)}`, got `{ArgModifierKeyword(ma.Modifier)}`",
                        ma.Span);
                    continue;
                }

                // Lvalue requirement: ref / out / in inner must be a writable
                // location reference (variable, array element, or object field).
                // For `out var x`, the inline declaration is by definition lvalue.
                if (ma.OutDecl is null && !IsLvalueForRef(bma.Inner))
                {
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"argument {i + 1}: `{ArgModifierKeyword(ma.Modifier)}` requires an lvalue (variable, field, or array element)",
                        ma.Span);
                    continue;
                }

                // Patch `out var x` local type from the matched parameter type.
                if (ma.OutDecl is { } d && i < (funcType?.Params.Count ?? 0))
                {
                    var pType = funcType!.Params[i];
                    env.Define(d.Name, pType);
                }
            }
        }
    }

    private static bool ModifiersMatch(ArgModifier arg, ParamModifier param)
    {
        return (arg, param) switch
        {
            (ArgModifier.Ref, ParamModifier.Ref) => true,
            (ArgModifier.Out, ParamModifier.Out) => true,
            (ArgModifier.In,  ParamModifier.In)  => true,
            (ArgModifier.None, ParamModifier.None) => true,
            _ => false,
        };
    }

    private static string ModifierKeyword(ParamModifier m) => m switch
    {
        ParamModifier.Ref => "ref",
        ParamModifier.Out => "out",
        ParamModifier.In  => "in",
        _ => "<none>",
    };

    private static string ArgModifierKeyword(ArgModifier m) => m switch
    {
        ArgModifier.Ref => "ref",
        ArgModifier.Out => "out",
        ArgModifier.In  => "in",
        _ => "<none>",
    };

    /// Test whether a bound expression is a writable location reference for
    /// the purposes of `ref` / `out` / `in` arguments. Allowed:
    ///   - `BoundIdent` resolving to a local variable (or `out var x` decl)
    ///   - `BoundIndex` (`a[i]` — array / list element)
    ///   - `BoundMember` (`obj.field` — object field; not a method/property)
    ///
    /// Disallowed: literals, function calls, arithmetic, casts, `this`-only
    /// receivers without member access.
    internal static bool IsLvalueForRef(BoundExpr expr) => expr switch
    {
        BoundIdent       => true,
        BoundIndex       => true,
        BoundMember      => true,
        _                => false,
    };

}
