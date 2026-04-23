using Z42.Core.Diagnostics;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

internal sealed partial class SymbolCollector
{
    /// L3 extern impl (Change 1): merge `impl Trait for Target { ... }` blocks into
    /// the target class's InterfaceTypes + Methods. Target can be a user class/struct
    /// (local or imported) or a primitive struct (int/double/bool/char — registered
    /// via primitive-as-struct stdlib). Methods must have a body (extern deferred).
    private void CollectImpls(CompilationUnit cu)
    {
        foreach (var impl in cu.Impls)
        {
            // Target must resolve to a class/struct name. Accept:
            // - NamedType referring to a registered class (local or imported)
            // - Primitive type names (int/double/bool/char) — resolve via stdlib struct
            string? targetName = impl.TargetType switch
            {
                NamedType nt => nt.Name,
                _            => null,
            };
            if (targetName is null)
            {
                _diags.Error(DiagnosticCodes.InvalidImpl,
                    "extern impl target must be a class, struct, or primitive type name",
                    impl.TargetType.Span);
                continue;
            }
            if (!_classes.TryGetValue(targetName, out var targetClass))
            {
                _diags.Error(DiagnosticCodes.InvalidImpl,
                    $"extern impl target `{targetName}` is not a known class or primitive struct",
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

            // Build signatures for impl methods. If target is generic, flow its type params.
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

            // Signature alignment: interface methods must all be provided; arity must match.
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

            // Merge: add impl methods to class's Methods map; attach trait to InterfaceTypes.
            // Collision check uses the target's *own* declared methods (cu.Classes[].Methods),
            // not inherited-via-base methods already present in the merged Methods map.
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
                    mergedMethods[mName] = sig;
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
