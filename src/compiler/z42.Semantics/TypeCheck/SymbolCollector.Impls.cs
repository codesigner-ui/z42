using Z42.Core.Diagnostics;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

internal sealed partial class SymbolCollector
{
    /// Pass 0e (L3-G2.5 extern impl, Change 1): merge `impl Trait for Target { ... }`
    /// blocks into their target class's InterfaceTypes + Methods. Same-zpkg orphan
    /// rule: target must be a user class declared in this CompilationUnit.
    private void CollectImpls(CompilationUnit cu)
    {
        // Track locally-declared class names so we can enforce the same-zpkg orphan rule.
        var localClassNames = new HashSet<string>(cu.Classes.Select(c => c.Name));

        foreach (var impl in cu.Impls)
        {
            // Target must be a plain named type (no generic args in Change 1) referring to
            // a user class/struct in this compilation unit.
            if (impl.TargetType is not NamedType targetNt)
            {
                _diags.Error(DiagnosticCodes.InvalidImpl,
                    "extern impl target must be a simple user class name (generic targets not yet supported)",
                    impl.TargetType.Span);
                continue;
            }
            var targetName = targetNt.Name;
            if (!localClassNames.Contains(targetName))
            {
                _diags.Error(DiagnosticCodes.InvalidImpl,
                    $"extern impl target `{targetName}` must be a user class/struct declared in the same zpkg",
                    impl.TargetType.Span);
                continue;
            }
            if (!_classes.TryGetValue(targetName, out var targetClass))
            {
                _diags.Error(DiagnosticCodes.InvalidImpl,
                    $"extern impl target `{targetName}` not found",
                    impl.TargetType.Span);
                continue;
            }

            // Trait must resolve to an interface (may carry type args).
            var resolvedTrait = ResolveType(impl.TraitType);
            if (resolvedTrait is not Z42InterfaceType traitIface)
            {
                _diags.Error(DiagnosticCodes.InvalidImpl,
                    $"extern impl trait must be an interface, got `{resolvedTrait}`",
                    impl.TraitType.Span);
                continue;
            }
            if (!_interfaces.TryGetValue(traitIface.Name, out var ifaceShape))
            {
                _diags.Error(DiagnosticCodes.InvalidImpl,
                    $"extern impl trait `{traitIface.Name}` not found",
                    impl.TraitType.Span);
                continue;
            }

            // Build signatures for impl methods (target class's type params flow through).
            if (targetClass.TypeParams is { Count: > 0 } tps)
                _activeTypeParams = new HashSet<string>(tps);
            var implMethodSigs = new Dictionary<string, Z42FuncType>();
            foreach (var m in impl.Methods)
            {
                if (implMethodSigs.ContainsKey(m.Name))
                {
                    _diags.Error(DiagnosticCodes.InvalidImpl,
                        $"extern impl for `{targetName}` has duplicate method `{m.Name}`",
                        m.Span);
                    continue;
                }
                var retType = ResolveType(m.ReturnType);
                implMethodSigs[m.Name] = BuildFuncSignature(m.Params, retType);
            }
            _activeTypeParams = null;

            // Signature alignment check: every method in the interface must have a matching
            // impl method; report missing / mismatched arity / mismatched return type.
            foreach (var (mName, ifaceSig) in ifaceShape.Methods)
            {
                if (!implMethodSigs.TryGetValue(mName, out var implSig))
                {
                    _diags.Error(DiagnosticCodes.InvalidImpl,
                        $"extern impl for `{targetName} : {ifaceShape.Name}` is missing method `{mName}`",
                        impl.Span);
                    continue;
                }
                if (implSig.Params.Count != ifaceSig.Params.Count)
                    _diags.Error(DiagnosticCodes.InvalidImpl,
                        $"extern impl method `{mName}` on `{targetName}` has {implSig.Params.Count} params, " +
                        $"interface `{ifaceShape.Name}` expects {ifaceSig.Params.Count}",
                        impl.Span);
            }

            // Merge: add impl methods to the class's Methods map; attach trait to InterfaceTypes.
            // Collision check uses the target's *own* declared methods (cu.Classes[].Methods),
            // not inherited-via-base methods already present in the class's merged Methods map.
            var ownDeclared = cu.Classes.FirstOrDefault(c => c.Name == targetName)?.Methods
                                .Select(m => m.Name).ToHashSet() ?? [];
            var mergedMethods = new Dictionary<string, Z42FuncType>(targetClass.Methods);
            foreach (var (mName, sig) in implMethodSigs)
            {
                if (ownDeclared.Contains(mName))
                    _diags.Error(DiagnosticCodes.InvalidImpl,
                        $"extern impl method `{mName}` on `{targetName}` conflicts with an existing class method",
                        impl.Span);
                else
                    mergedMethods[mName] = sig;  // OK to shadow inherited (e.g. Object.Equals)
            }
            _classes[targetName] = targetClass with { Methods = mergedMethods };

            // Append trait to class's interface list (resolved Z42InterfaceType carries TypeArgs).
            if (!_classInterfaces.TryGetValue(targetName, out var ifaces))
            {
                ifaces = new List<Z42InterfaceType>();
                _classInterfaces[targetName] = ifaces;
            }
            ifaces.Add(traitIface);
        }
    }
}
