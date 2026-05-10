using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Semantics.Bound;
using Z42.Semantics.Symbols;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

public sealed partial class TypeChecker
{

    /// Modifier-aware overload lookup (spec: define-ref-out-in-parameters,
    /// Decision 7). Tries keys in order:
    ///   1. `Name$Arity$<argModSig>` — modifier-tagged key (same-arity overload)
    ///   2. `Name`                    — bare (single method, no overload)
    ///   3. `Name$Arity`              — legacy arity-only key (no modifier collision)
    /// Returns matching method symbol + the resolved key for IR codegen, or (null, null).
    /// (split-symbol-from-type Phase 2: dict value type changed from Z42FuncType to
    /// IMethodSymbol; callers access `.Signature` on the returned symbol.)
    internal static (IMethodSymbol? Sym, string? Key) LookupMethodOverload(
        IReadOnlyDictionary<string, IMethodSymbol> table,
        string memberName,
        IReadOnlyList<Expr> args)
    {
        var argModSig = ModifierMangling.PatternFromArgs(args);
        if (!string.IsNullOrEmpty(argModSig))
        {
            var modKey = $"{memberName}${args.Count}${argModSig}";
            if (table.TryGetValue(modKey, out var modSym))
                return (modSym, modKey);
        }
        if (table.TryGetValue(memberName, out var bare)) return (bare, memberName);
        var arityKey = $"{memberName}${args.Count}";
        if (table.TryGetValue(arityKey, out var ar)) return (ar, arityKey);
        return (null, null);
    }

    private void CheckArgTypes(
        IReadOnlyList<Expr>      origArgs,
        IReadOnlyList<BoundExpr> boundArgs,
        IReadOnlyList<Z42Type>   paramTypes)
    {
        int n = Math.Min(boundArgs.Count, paramTypes.Count);
        for (int i = 0; i < n; i++)
        {
            // For modifier-bearing arguments (ref/out/in), require exact type
            // match (no implicit conversion across the reference boundary).
            // For `out var x`, the local was declared as Unknown and CheckArgModifiers
            // patches it from paramType — skip strict check here.
            if (boundArgs[i] is BoundModifiedArg bma)
            {
                if (bma.OutDecl is not null) continue;
                if (!paramTypes[i].Equals(bma.Inner.Type)
                    && bma.Inner.Type is not Z42ErrorType
                    && bma.Inner.Type is not Z42UnknownType
                    && paramTypes[i] is not Z42UnknownType)
                {
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"argument {i + 1}: `{ArgModifierKeyword(bma.Modifier)}` requires exact type match — expected `{paramTypes[i]}`, got `{bma.Inner.Type}`",
                        origArgs[i].Span);
                }
                continue;
            }
            RequireAssignable(paramTypes[i], boundArgs[i].Type, origArgs[i].Span,
                $"argument {i + 1}: expected `{paramTypes[i]}`, got `{boundArgs[i].Type}`");
        }
    }

    /// Uppercase first character ("string" → "String") for mapping a primitive
    /// type name to its stdlib class name (`struct int` / `class String` etc).
    private static string CapitalizeFirst(string name) =>
        name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name[1..];

    /// L3-G2: infer type args for a generic function from its argument types,
    /// then validate each satisfies the corresponding `where` constraint.
    /// Inference algorithm (single-level, sufficient for L3-G2):
    ///   for each parameter i whose signature type is `Z42GenericParamType(T)`,
    ///   bind T to `boundArgs[i].Type` (first binding wins; conflicts ignored for now).
    private void InferAndValidateFuncConstraints(
        string funcName, Z42FuncType ft, IReadOnlyList<BoundExpr> boundArgs, Span callSpan)
    {
        var inferred = InferFuncTypeArgs(ft, boundArgs);
        if (inferred.Count == 0) return;
        var typeParams = inferred.Keys.ToList();
        var typeArgs   = typeParams.Select(k => inferred[k]).ToList();
        ValidateGenericConstraints(funcName, typeParams, typeArgs, _funcConstraints, callSpan);
    }

    /// Infer T → concrete-type map by scanning parameter positions of generic type T.
    /// First occurrence wins.
    private static Dictionary<string, Z42Type> InferFuncTypeArgs(
        Z42FuncType ft, IReadOnlyList<BoundExpr> boundArgs)
    {
        var inferred = new Dictionary<string, Z42Type>(StringComparer.Ordinal);
        int n = Math.Min(boundArgs.Count, ft.Params.Count);
        for (int i = 0; i < n; i++)
        {
            if (ft.Params[i] is Z42GenericParamType gp && !inferred.ContainsKey(gp.Name))
                inferred[gp.Name] = boundArgs[i].Type;
        }
        return inferred;
    }

    /// Substitute generic params in return type using args inferred from parameter positions.
    /// Returns `ft.Ret` unchanged when no substitution applies (non-generic, no match).
    private static Z42Type SubstituteGenericReturn(Z42FuncType ft, IReadOnlyList<BoundExpr> boundArgs)
    {
        if (ft.Ret is not Z42GenericParamType retGp) return ft.Ret;
        var inferred = InferFuncTypeArgs(ft, boundArgs);
        return inferred.TryGetValue(retGp.Name, out var concrete) ? concrete : ft.Ret;
    }

    /// 2026-05-05 fix-generic-extern-infer: substitute every `Z42GenericParamType`
    /// occurrence in the parameter list using inference from boundArgs. Returns
    /// the original list unchanged when nothing is inferred. Used to make
    /// CheckArgTypes accept `BlackBox<T>(int)` etc. as `int` matching `T`'s
    /// inferred slot rather than the literal type-param.
    private static IReadOnlyList<Z42Type> SubstituteGenericParams(
        Z42FuncType ft, IReadOnlyList<BoundExpr> boundArgs)
    {
        var inferred = InferFuncTypeArgs(ft, boundArgs);
        if (inferred.Count == 0) return ft.Params;
        return ft.Params.Select(p =>
            p is Z42GenericParamType gp && inferred.TryGetValue(gp.Name, out var concrete)
                ? concrete : p).ToList();
    }
}
