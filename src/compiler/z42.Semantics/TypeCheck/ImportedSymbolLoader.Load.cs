using Z42.Core;
using Z42.IR;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

public static partial class ImportedSymbolLoader
{

    public static ImportedSymbols Load(
        IReadOnlyList<ExportedModule> modules,
        IReadOnlyList<string> usings)
    {
        // 把每个 namespace 视作一个虚拟 package（同名）。
        // tests 写 `Module("Std", ...)` + `usings=["Std"]` → "Std" 被激活。
        var packageOf = new Dictionary<ExportedModule, string>();
        foreach (var m in modules) packageOf[m] = m.Namespace;
        var activated = new HashSet<string>(usings, StringComparer.Ordinal);
        return Load(modules, packageOf, activated, preludePackages: new HashSet<string>());
    }

    /// <summary>
    /// 主 API：按 (package, namespace, class) 三元组组织 modules，
    /// 仅 `package ∈ activated ∪ prelude` 的类进入返回值。
    /// 同 (namespace, class-name) 跨多 activated package → Collisions 列表。
    /// </summary>
    public static ImportedSymbols Load(
        IReadOnlyList<ExportedModule>                     modules,
        IReadOnlyDictionary<ExportedModule, string>       packageOf,
        IReadOnlyCollection<string>                       activatedPackages,
        IReadOnlyCollection<string>?                      preludePackages = null)
    {
        preludePackages ??= PreludePackages.Names;
        var allowedPkgs = new HashSet<string>(activatedPackages, StringComparer.Ordinal);
        allowedPkgs.UnionWith(preludePackages);

        // fix-prelude-wins-for-ambiguous-names (2026-05-17): re-order modules
        // so prelude packages (z42.core) are processed BEFORE explicit
        // `using`-activated packages, and within each group by package name +
        // module namespace. The Phase-1 loop below uses first-wins for
        // ambiguous bare names (e.g. `Assert` lives in both `Std` from
        // z42.core prelude and `Std.Test` from z42.test). Caller-side path
        // iteration in `TsigCache` is already sorted, but explicit ordering
        // here also pins the semantics: prelude beats explicit using, so an
        // unqualified `Assert` keeps resolving to `Std.Assert` regardless of
        // OS / .NET hash-randomised dictionary iteration. Use FQN
        // (`<package>.<namespace>`) qualified call sites to override.
        var preludeSet = new HashSet<string>(preludePackages, StringComparer.Ordinal);
        modules = modules
            .OrderBy(m => preludeSet.Contains(packageOf.TryGetValue(m, out var pk) ? pk : "") ? 0 : 1)
            .ThenBy(m => packageOf.TryGetValue(m, out var pk2) ? pk2 : "", StringComparer.Ordinal)
            .ThenBy(m => m.Namespace ?? "", StringComparer.Ordinal)
            .ToList();

        // (namespace, class-name) → 已贡献该名字的 package 集合（用于冲突检测）
        var contributors = new Dictionary<(string Ns, string Name), List<string>>();
        var classes    = new Dictionary<string, Z42ClassType>(StringComparer.Ordinal);
        var funcs      = new Dictionary<string, Z42FuncType>(StringComparer.Ordinal);
        var funcDecls  = new Dictionary<string, FunctionDecl>(StringComparer.Ordinal);
        var interfaces = new Dictionary<string, Z42InterfaceType>(StringComparer.Ordinal);
        var enumConsts = new Dictionary<string, long>(StringComparer.Ordinal);
        var enumTypes  = new HashSet<string>(StringComparer.Ordinal);
        var classNs    = new Dictionary<string, string>(StringComparer.Ordinal);
        var classPkg   = new Dictionary<string, string>(StringComparer.Ordinal);
        // L3-G3d: raw serialized constraints — resolved to bundles later by TypeChecker.
        var classConstraints = new Dictionary<string, List<ExportedTypeParamConstraint>>(StringComparer.Ordinal);
        var funcConstraints  = new Dictionary<string, List<ExportedTypeParamConstraint>>(StringComparer.Ordinal);
        var classInterfaces  = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // Phase 1 first-wins selection records: (mod, cls/iface) we'll fill in Phase 2.
        var selectedClasses    = new List<(ExportedModule mod, ExportedClassDef     cls)>();
        var selectedInterfaces = new List<(ExportedModule mod, ExportedInterfaceDef iface)>();

        // 2026-05-06: collect every namespace declared by an activated module — used
        // for E0602 `using <ns>;` resolution. Includes namespaces from impl-only
        // packages (e.g. cross-zpkg `impl Trait for Type` modules).
        var resolvedNs = new HashSet<string>(StringComparer.Ordinal);

        // ── Phase 1: 骨架登记 ──
        // 遍历 modules，对每个新名字创建空成员的骨架；first-wins on duplicates.
        // 同时记录每个 (ns, class-name) 被哪些 package 贡献，用于冲突检测。
        //
        // 2026-05-07 add-class-arity-overloading: shadow-only mangling for
        // imported classes. First scan for same-name + arity-disjoint sets per
        // module so that, when an imported package declares e.g. both
        // `MulticastException` and `MulticastException<R>`, the generic version
        // is registered under `Name$N` rather than colliding on the bare name.
        var importMangleNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mod in modules)
        {
            if (!packageOf.TryGetValue(mod, out var modPkg)) continue;
            if (!allowedPkgs.Contains(modPkg)) continue;
            var byName = mod.Classes
                .GroupBy(c => c.Name)
                .Where(g => g.Count() >= 2 && g.Select(c => c.TypeParams?.Count ?? 0).Distinct().Count() >= 2)
                .Select(g => g.Key);
            foreach (var n in byName) importMangleNames.Add(n);
        }
        string ImportKey(ExportedClassDef cls) =>
            cls.TypeParams is { Count: > 0 } tps && importMangleNames.Contains(cls.Name)
                ? $"{cls.Name}${tps.Count}"
                : cls.Name;

        foreach (var mod in modules)
        {
            var pkg = packageOf.TryGetValue(mod, out var p) ? p : "";
            if (!allowedPkgs.Contains(pkg)) continue;

            // Every activated module's namespace is reachable via `using`, regardless
            // of whether it contributes types in this loader pass (impl blocks count).
            if (!string.IsNullOrEmpty(mod.Namespace)) resolvedNs.Add(mod.Namespace);

            foreach (var cls in mod.Classes)
            {
                var importKey = ImportKey(cls);
                var key       = (mod.Namespace, importKey);
                if (!contributors.TryGetValue(key, out var list))
                {
                    list = new List<string>();
                    contributors[key] = list;
                }
                if (!list.Contains(pkg)) list.Add(pkg);

                if (!classes.ContainsKey(importKey))
                {
                    classes[importKey] = BuildClassSkeleton(cls, importKey);
                    classPkg[importKey] = pkg;
                    selectedClasses.Add((mod, cls));
                }
            }

            foreach (var iface in mod.Interfaces)
                if (!interfaces.ContainsKey(iface.Name))
                {
                    interfaces[iface.Name] = BuildInterfaceSkeleton(iface);
                    selectedInterfaces.Add((mod, iface));
                }
        }

        // ── Phase 1.5: 加载 delegates（2026-05-02 D2a/D1c）──
        // delegate 签名只引用 classes / interfaces 骨架 + 基本元素 + 自身泛型
        // 占位，Phase 1 已就绪；放在 Phase 2 之前，让 Phase 2 method 类型解析
        // 也能识别 `Action<T>` 等 delegate 名。
        var delegates = new Dictionary<string, DelegateInfo>(StringComparer.Ordinal);
        foreach (var mod in modules)
        {
            if (mod.Delegates is not { } modDelegates) continue;
            if (!packageOf.TryGetValue(mod, out var pkg2)) continue;
            if (!allowedPkgs.Contains(pkg2)) continue;
            foreach (var d in modDelegates)
            {
                var tpSet = d.TypeParams is { Count: > 0 } tps
                    ? new HashSet<string>(tps) : null;
                var paramTypes = d.Params.Select(p =>
                    ResolveTypeName(p.TypeName, tpSet, classes, interfaces)).ToList();
                var retType = ResolveTypeName(d.ReturnType, tpSet, classes, interfaces);
                var sig = new Z42FuncType(paramTypes, retType);
                var info = new DelegateInfo(sig,
                    (IReadOnlyList<string>)(d.TypeParams ?? new List<string>()),
                    d.ContainerClass);

                var arity = d.TypeParams?.Count ?? 0;
                var bareName = d.ContainerClass is null
                    ? d.Name : $"{d.ContainerClass}.{d.Name}";
                var key = arity > 0 ? $"{bareName}${arity}" : bareName;
                delegates.TryAdd(key, info);
                if (d.ContainerClass is not null)
                {
                    var simpleKey = arity > 0 ? $"{d.Name}${arity}" : d.Name;
                    delegates.TryAdd(simpleKey, info);
                }
            }
        }

        // ── Phase 2: 填充成员 + 其他副产物 dicts ──
        // 关键：Phase 1 骨架内嵌的 Dictionary 是 mutable；Phase 2 直接对同一
        // 字典 Add 字段/方法，**不替换 Z42ClassType record 实例**。这样在 Phase 2
        // 内 ResolveTypeName lookup 拿到的 ClassType 引用与最终 ImportedSymbols
        // 输出的 ClassType 引用是同一对象，避免 record immutability 导致的
        // "字段持有空骨架" 问题。
        foreach (var (mod, cls) in selectedClasses)
        {
            var importKey = ImportKey(cls);
            FillClassMembersInPlace(cls, classes[importKey], classes, interfaces, delegates);
            classNs[importKey] = mod.Namespace;
            if (cls.TypeParamConstraints is { Count: > 0 } cc)
                classConstraints[importKey] = cc;
            // L3-G4b primitive-as-struct: preserve declared interface list so
            // the TypeChecker can answer "does stdlib struct int implement
            // IComparable?" via data-driven lookup.
            if (cls.Interfaces.Count > 0)
                classInterfaces[importKey] = new List<string>(cls.Interfaces);
        }

        foreach (var (_, iface) in selectedInterfaces)
        {
            // Same in-place strategy for interface methods + static members.
            // For interfaces, static members may need to be promoted from null
            // to a populated dict — handled by replacing the record in that
            // edge case (interface methods dict is always pre-allocated).
            interfaces[iface.Name] = FillInterfaceMembersInPlace(
                iface, interfaces[iface.Name], classes, interfaces, delegates);
        }

        // Enums and free functions don't reference other class/interface types
        // structurally for the purpose of the two-phase fix-up; they can be
        // processed in a single pass alongside Phase 2.
        foreach (var mod in modules)
        {
            var pkg = packageOf.TryGetValue(mod, out var p) ? p : "";
            if (!allowedPkgs.Contains(pkg)) continue;

            foreach (var en in mod.Enums)
            {
                enumTypes.Add(en.Name);
                foreach (var m in en.Members)
                    enumConsts.TryAdd($"{en.Name}.{m.Name}", m.Value);
            }

            foreach (var fn in mod.Functions)
                if (!funcs.ContainsKey(fn.Name))
                {
                    funcs[fn.Name] = RebuildFuncType(
                        fn.Params, fn.ReturnType, fn.MinArgCount, null,
                        classes, interfaces, delegates);
                    funcDecls[fn.Name] = SynthesizeImportedDecl(fn.Name, fn.Params, fn.ReturnType);
                    if (fn.TypeParamConstraints is { Count: > 0 } fc)
                        funcConstraints[fn.Name] = fc;
                }
        }

        // ── Phase 3: 合并跨 zpkg `impl Trait for Type` 块 (L3-Impl2) ──
        // For each ExportedImplDef, look up target class by FQ name, append impl
        // methods to its Methods dict (first-wins), and add trait to classInterfaces.
        // Methods/traits added here are visible to TypeChecker downstream — VM dispatch
        // works via func_index in the impl-providing zpkg's MODS section (no VM change).
        MergeImpls(modules, packageOf, allowedPkgs, classes, interfaces, classNs, classInterfaces);

        // ── Phase 4: 抽取 collisions 列表（同 (ns, name) 多 package 贡献）──
        List<NamespaceCollision>? collisions = null;
        foreach (var ((ns, name), pkgs) in contributors)
        {
            if (pkgs.Count >= 2)
            {
                collisions ??= new List<NamespaceCollision>();
                collisions.Add(new NamespaceCollision(ns, name, pkgs));
            }
        }

        return new ImportedSymbols(classes, funcs, interfaces, enumConsts, enumTypes, classNs,
            classConstraints, funcConstraints, classInterfaces, classPkg, collisions,
            Delegates: delegates.Count > 0 ? delegates : null,
            ResolvedNamespaces: resolvedNs.Count > 0 ? resolvedNs : null,
            FuncDecls: funcDecls.Count > 0 ? funcDecls : null);
    }

}
