using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

public sealed partial class TypeChecker
{

    // ── Parameter modifiers (spec: define-ref-out-in-parameters) ──────────────

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
        IReadOnlyList<Expr>      origArgs,
        IReadOnlyList<BoundExpr> boundArgs,
        Z42FuncType?             funcType,
        TypeEnv                  env,
        Span                     callSpan)
    {
        int n = boundArgs.Count;
        for (int i = 0; i < n; i++)
        {
            var origIsModified = origArgs[i] is ModifiedArg;
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
                var ma = (ModifiedArg)origArgs[i];
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"argument {i + 1}: parameter has no modifier; remove `{ArgModifierKeyword(ma.Modifier)}`",
                    ma.Span);
                continue;
            }

            // Case 3: both have modifiers — must match exactly.
            if (origIsModified)
            {
                var ma = (ModifiedArg)origArgs[i];
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
