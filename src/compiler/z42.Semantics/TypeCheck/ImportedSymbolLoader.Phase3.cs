using Z42.Core;
using Z42.IR;
using Z42.Semantics.Symbols;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

public static partial class ImportedSymbolLoader
{

    /// L3-Impl2 Phase 3: merge `impl Trait for Target` declarations into the
    /// target's imported Z42ClassType. Mutates classes/classInterfaces dicts in place.
    private static void MergeImpls(
        IReadOnlyList<ExportedModule>                  modules,
        IReadOnlyDictionary<ExportedModule, string>    packageOf,
        HashSet<string>                                allowedPkgs,
        Dictionary<string, Z42ClassType>               classes,
        Dictionary<string, Z42InterfaceType>           interfaces,
        Dictionary<string, string>                     classNs,
        Dictionary<string, List<string>>               classInterfaces)
    {
        foreach (var mod in modules)
        {
            var pkg = packageOf.TryGetValue(mod, out var p) ? p : "";
            if (!allowedPkgs.Contains(pkg)) continue;
            if (mod.Impls is null) continue;
            foreach (var impl in mod.Impls)
            {
                // FQ "Std.Int32" → short "Int32". Skip if target unknown to import set.
                var (targetNs, targetShort) = SplitFqName(impl.TargetFqName);
                if (targetShort is null) continue;
                if (!classes.TryGetValue(targetShort, out var targetClass)) continue;
                // Only merge if namespace matches (avoid name collision across zpkgs).
                if (classNs.TryGetValue(targetShort, out var existingNs)
                    && targetNs != null
                    && existingNs != targetNs)
                    continue;

                var (_, traitShort) = SplitFqName(impl.TraitFqName);
                if (traitShort is null) continue;
                if (!interfaces.ContainsKey(traitShort)) continue;

                // First-wins method merge into target.Methods.
                var methodsDict = (Dictionary<string, IMethodSymbol>)targetClass.Methods;
                foreach (var m in impl.Methods)
                {
                    var sig = RebuildFuncType(m.Params, m.ReturnType, m.MinArgCount,
                        genericParams: null, classes, interfaces);
                    var mods = ImportedModifiers(m);
                    var sym = new MethodSymbol(m.Name, targetClass, sig, mods,
                                                default(Z42.Core.Text.Span),
                                                ParseVisibility(m.Visibility),
                                                decl: null, testAttributes: null);
                    if (m.IsStatic)
                    {
                        var staticDict = (Dictionary<string, IMethodSymbol>)targetClass.StaticMethods;
                        staticDict.TryAdd(m.Name, sym);
                    }
                    else
                    {
                        methodsDict.TryAdd(m.Name, sym);
                    }
                }

                // Add trait to classInterfaces[targetShort] (dedupe by name).
                if (!classInterfaces.TryGetValue(targetShort, out var ifList))
                    classInterfaces[targetShort] = ifList = new List<string>();
                if (!ifList.Contains(traitShort))
                    ifList.Add(traitShort);
            }
        }
    }

    /// Split a fully-qualified type name (e.g. "Std.Int32") into (namespace, shortName).
    /// Returns (null, name) when there's no dot.
    private static (string? Namespace, string? ShortName) SplitFqName(string fq)
    {
        if (string.IsNullOrEmpty(fq)) return (null, null);
        int dot = fq.LastIndexOf('.');
        if (dot < 0) return (null, fq);
        return (fq[..dot], fq[(dot + 1)..]);
    }

}
