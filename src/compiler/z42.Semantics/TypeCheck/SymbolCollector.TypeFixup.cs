using Z42.Semantics.Symbols;

namespace Z42.Semantics.TypeCheck;

public sealed partial class SymbolCollector
{
    // ── Post-collection type-reference fixup ────────────────────────────────
    //
    // `ResolveType` resolves a member type per-CU during collection. When a
    // method/field type references a class (or interface) whose CU hasn't been
    // collected *yet*, it degrades to a `Z42PrimType(name)` sentinel (the same
    // forward / cross-file reference problem two-phase loaders solve). Because
    // CU collection order is not deterministic (common-pitfalls.md §1 — OS file
    // enumeration), whether a given reference degrades depends on build shape:
    // single-package vs workspace can land on opposite orders.
    //
    // Downstream this bites at `RequireAssignable`: `T x = arr[i].M()` where M's
    // return type degraded to `Z42PrimType("T")` fails identity against the LHS
    // annotation's real `Z42ClassType("T")` (same name, different kind) → spurious
    // `E0402: cannot assign T to T`. The same sentinel would also mislead IrGen.
    //
    // Once every CU is collected, _classes / _interfaces are complete, so this
    // one-shot pass upgrades every such sentinel back to the real type in place —
    // physically eliminating the degraded state rather than tolerating it at each
    // consumer (philosophy.md "修复必须从根因出发" → Phase-2 fixup pass).

    /// Upgrade degraded `Z42PrimType` class/interface references across all
    /// locally-declared member signatures + free functions. Idempotent; imported
    /// symbols are skipped (already resolved by the imported-symbol loader, and
    /// upgrading them risks conflating a same-named local class).
    internal void FinalizeTypeReferences()
    {
        foreach (var (name, ct) in _classes)
        {
            if (_importedClassNames.Contains(name)) continue;
            UpgradeMethods(ct.Methods);
            UpgradeMethods(ct.StaticMethods);
            UpgradeFields(ct.Fields);
            UpgradeFields(ct.StaticFields);
        }
        foreach (var (name, it) in _interfaces)
        {
            if (_importedInterfaceNames.Contains(name)) continue;
            UpgradeMethods(it.Methods);
        }
        foreach (var key in _funcs.Keys.ToList())
        {
            if (_importedFuncNames.Contains(key)) continue;
            if (UpgradeType(_funcs[key]) is Z42FuncType ft && !ReferenceEquals(ft, _funcs[key]))
                _funcs[key] = ft;
        }
    }

    private void UpgradeMethods(IReadOnlyDictionary<string, IMethodSymbol> methods)
    {
        foreach (var m in methods.Values)
            if (m is MethodSymbol ms && UpgradeType(ms.Signature) is Z42FuncType sig
                && !ReferenceEquals(sig, ms.Signature))
                ms.Signature = sig;
    }

    private void UpgradeFields(IReadOnlyDictionary<string, IFieldSymbol> fields)
    {
        foreach (var f in fields.Values)
            if (f is FieldSymbol fs)
            {
                var t = UpgradeType(fs.Type);
                if (!ReferenceEquals(t, fs.Type)) fs.Type = t;
            }
    }

    /// Recursively replace any `Z42PrimType(name)` that names a known local class
    /// / interface with the real type. Returns the same instance when nothing
    /// changed (lets callers skip writes). Truly-unknown names (typos), generic
    /// params, primitives and enums are left untouched.
    private Z42Type UpgradeType(Z42Type t)
    {
        switch (t)
        {
            case Z42PrimType p:
                if (_classes.TryGetValue(p.Name, out var ct))    return ct;
                if (_interfaces.TryGetValue(p.Name, out var it))  return it;
                return t;

            // fix-stale-classtype-in-signature (2026-06-18): a signature built
            // during collection captures whatever `_classes[name]` held at that
            // moment — for a self/forward reference that is the *skeleton* (or an
            // intermediate pre-inheritance-merge version) with an empty / partial
            // Methods dict, because classes are immutable records replaced (not
            // mutated) as members/inheritance are merged. Left stale, `factory()
            // .method()` chains (`TypeEnv.Root(s).WithClassGeneric(...)`,
            // `E.Root().With(5)`) resolve the member against the empty skeleton →
            // spurious `E0402 has no method` (local) / silent Unknown (imported).
            // Re-point to the final registry entry — same root-cause Phase-2 fixup
            // as the PrimType sentinel above. Skip when already the final instance.
            // Only non-generic class refs: a stale `Z42ClassType` carries no type
            // args (instantiations are `Z42InstantiatedType`, handled below), so
            // re-pointing to the registry entry can't lose generic info. Interfaces
            // are intentionally NOT upgraded here — a generic-interface ref like
            // `ISubscription<(T) -> void>` carries TypeArgs that the bare registry
            // entry lacks, so upgrading would strip them (E0402 on the stdlib
            // Multicast delegates). Interface forward-refs already degrade to a
            // PrimType sentinel (handled above), not a stale Z42InterfaceType.
            case Z42ClassType ctRef:
                if (_classes.TryGetValue(ctRef.Name, out var fullCt)
                    && !ReferenceEquals(fullCt, ctRef))
                    return fullCt;
                return t;

            case Z42ArrayType a:
                var elem = UpgradeType(a.Element);
                return ReferenceEquals(elem, a.Element) ? a : new Z42ArrayType(elem);

            case Z42OptionType o:
                var inner = UpgradeType(o.Inner);
                return ReferenceEquals(inner, o.Inner) ? o : new Z42OptionType(inner);

            case Z42FuncType f:
                var ps = f.Params.Select(UpgradeType).ToList();
                var ret = UpgradeType(f.Ret);
                bool changed = !ReferenceEquals(ret, f.Ret);
                for (int i = 0; i < ps.Count && !changed; i++)
                    if (!ReferenceEquals(ps[i], f.Params[i])) changed = true;
                return changed ? f with { Params = ps, Ret = ret } : f;

            case Z42InstantiatedType inst:
                var args = inst.TypeArgs.Select(UpgradeType).ToList();
                bool ch = false;
                for (int i = 0; i < args.Count && !ch; i++)
                    if (!ReferenceEquals(args[i], inst.TypeArgs[i])) ch = true;
                return ch ? inst with { TypeArgs = args } : inst;

            default:
                return t;
        }
    }
}
