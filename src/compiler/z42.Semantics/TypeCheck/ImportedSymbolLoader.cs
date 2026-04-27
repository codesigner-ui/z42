using Z42.IR;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// Reconstructs Z42Type objects from <see cref="ExportedModule"/> metadata
/// loaded from zpkg TSIG sections. These imported symbols are merged into
/// the <see cref="SymbolCollector"/> before type checking begins.
///
/// 加载分为两阶段（两阶段加载，C# / Java 编译器经典做法）：
///
///   Phase 1 — 骨架登记：为每个 ExportedClass / ExportedInterface 创建空成员
///             的 Z42ClassType / Z42InterfaceType，仅填 Name + TypeParams +
///             BaseClassName。登记进 classes / interfaces 字典。
///   Phase 2 — 填充成员：遍历每个类型，解析其 Fields / Methods 签名。此时
///             ResolveTypeName 在 classes / interfaces 字典里能查到对应骨架，
///             不再降级为 Z42PrimType。
///
/// 这样消除了 self-reference / forward-reference 的"未知名 → Z42PrimType"
/// 降级，从源头保证 ImportedSymbols 中所有跨类型 reference 都是正确的
/// Z42ClassType / Z42InterfaceType 对象。
/// </summary>
public static class ImportedSymbolLoader
{
    /// <summary>
    /// Load exported modules and produce a merged SymbolTable containing
    /// all imported types filtered by the given <paramref name="usings"/>.
    /// </summary>
    public static ImportedSymbols Load(
        IReadOnlyList<ExportedModule> modules,
        IReadOnlyList<string> usings)
    {
        var allowedNs  = new HashSet<string>(usings, StringComparer.Ordinal);
        // 2026-04-27 fix-using-prelude-include：`Std` 是 z42.core 的隐式 prelude
        // （见 docs/design/stdlib.md "z42.core auto-load semantics"）。无论用户
        // 写什么 using，IEquatable / Object / Exception 等基础类型都应可见。
        // 缺这个会导致 `using Std.Collections;` + `Dictionary<string,int>` 报
        // "string does not satisfy IEquatable on Dictionary"（Dictionary 约束
        // 引用 IEquatable，但 IEquatable 在 Std 命名空间被过滤掉了）。
        allowedNs.Add("Std");
        var classes    = new Dictionary<string, Z42ClassType>(StringComparer.Ordinal);
        var funcs      = new Dictionary<string, Z42FuncType>(StringComparer.Ordinal);
        var interfaces = new Dictionary<string, Z42InterfaceType>(StringComparer.Ordinal);
        var enumConsts = new Dictionary<string, long>(StringComparer.Ordinal);
        var enumTypes  = new HashSet<string>(StringComparer.Ordinal);
        var classNs    = new Dictionary<string, string>(StringComparer.Ordinal);
        // L3-G3d: raw serialized constraints — resolved to bundles later by TypeChecker.
        var classConstraints = new Dictionary<string, List<ExportedTypeParamConstraint>>(StringComparer.Ordinal);
        var funcConstraints  = new Dictionary<string, List<ExportedTypeParamConstraint>>(StringComparer.Ordinal);
        var classInterfaces  = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // Phase 1 first-wins selection records: (mod, cls/iface) we'll fill in Phase 2.
        var selectedClasses    = new List<(ExportedModule mod, ExportedClassDef     cls)>();
        var selectedInterfaces = new List<(ExportedModule mod, ExportedInterfaceDef iface)>();

        // ── Phase 1: 骨架登记 ──
        // 遍历 modules，对每个新名字创建空成员的骨架；first-wins on duplicates.
        foreach (var mod in modules)
        {
            if (!allowedNs.Contains(mod.Namespace)) continue;

            foreach (var cls in mod.Classes)
                if (!classes.ContainsKey(cls.Name))
                {
                    classes[cls.Name] = BuildClassSkeleton(cls);
                    selectedClasses.Add((mod, cls));
                }

            foreach (var iface in mod.Interfaces)
                if (!interfaces.ContainsKey(iface.Name))
                {
                    interfaces[iface.Name] = BuildInterfaceSkeleton(iface);
                    selectedInterfaces.Add((mod, iface));
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
            FillClassMembersInPlace(cls, classes[cls.Name], classes, interfaces);
            classNs[cls.Name] = mod.Namespace;
            if (cls.TypeParamConstraints is { Count: > 0 } cc)
                classConstraints[cls.Name] = cc;
            // L3-G4b primitive-as-struct: preserve declared interface list so
            // the TypeChecker can answer "does stdlib struct int implement
            // IComparable?" via data-driven lookup.
            if (cls.Interfaces.Count > 0)
                classInterfaces[cls.Name] = new List<string>(cls.Interfaces);
        }

        foreach (var (_, iface) in selectedInterfaces)
        {
            // Same in-place strategy for interface methods + static members.
            // For interfaces, static members may need to be promoted from null
            // to a populated dict — handled by replacing the record in that
            // edge case (interface methods dict is always pre-allocated).
            interfaces[iface.Name] = FillInterfaceMembersInPlace(
                iface, interfaces[iface.Name], classes, interfaces);
        }

        // Enums and free functions don't reference other class/interface types
        // structurally for the purpose of the two-phase fix-up; they can be
        // processed in a single pass alongside Phase 2.
        foreach (var mod in modules)
        {
            if (!allowedNs.Contains(mod.Namespace)) continue;

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
                        classes, interfaces);
                    if (fn.TypeParamConstraints is { Count: > 0 } fc)
                        funcConstraints[fn.Name] = fc;
                }
        }

        // ── Phase 3: 合并跨 zpkg `impl Trait for Type` 块 (L3-Impl2) ──
        // For each ExportedImplDef, look up target class by FQ name, append impl
        // methods to its Methods dict (first-wins), and add trait to classInterfaces.
        // Methods/traits added here are visible to TypeChecker downstream — VM dispatch
        // works via func_index in the impl-providing zpkg's MODS section (no VM change).
        MergeImpls(modules, allowedNs, classes, interfaces, classNs, classInterfaces);

        return new ImportedSymbols(classes, funcs, interfaces, enumConsts, enumTypes, classNs,
            classConstraints, funcConstraints, classInterfaces);
    }

    /// L3-Impl2 Phase 3: merge `impl Trait for Target` declarations into the
    /// target's imported Z42ClassType. Mutates classes/classInterfaces dicts in place.
    private static void MergeImpls(
        IReadOnlyList<ExportedModule>              modules,
        HashSet<string>                            allowedNs,
        Dictionary<string, Z42ClassType>           classes,
        Dictionary<string, Z42InterfaceType>       interfaces,
        Dictionary<string, string>                 classNs,
        Dictionary<string, List<string>>           classInterfaces)
    {
        foreach (var mod in modules)
        {
            if (!allowedNs.Contains(mod.Namespace)) continue;
            if (mod.Impls is null) continue;
            foreach (var impl in mod.Impls)
            {
                // FQ "Std.int" → short "int". Skip if target unknown to import set.
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
                var methodsDict = (Dictionary<string, Z42FuncType>)targetClass.Methods;
                foreach (var m in impl.Methods)
                {
                    var sig = RebuildFuncType(m.Params, m.ReturnType, m.MinArgCount,
                        genericParams: null, classes, interfaces);
                    if (m.IsStatic)
                    {
                        var staticDict = (Dictionary<string, Z42FuncType>)targetClass.StaticMethods;
                        staticDict.TryAdd(m.Name, sig);
                    }
                    else
                    {
                        methodsDict.TryAdd(m.Name, sig);
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

    /// Split a fully-qualified type name (e.g. "Std.int") into (namespace, shortName).
    /// Returns (null, name) when there's no dot.
    private static (string? Namespace, string? ShortName) SplitFqName(string fq)
    {
        if (string.IsNullOrEmpty(fq)) return (null, null);
        int dot = fq.LastIndexOf('.');
        if (dot < 0) return (null, fq);
        return (fq[..dot], fq[(dot + 1)..]);
    }

    // ── Phase 1 helpers: 骨架构造 ──────────────────────────────────────────────

    private static Z42ClassType BuildClassSkeleton(ExportedClassDef cls)
    {
        IReadOnlyList<string>? typeParams = cls.TypeParams is { Count: > 0 }
            ? cls.TypeParams.AsReadOnly() : null;
        return new Z42ClassType(
            cls.Name,
            Fields:           new Dictionary<string, Z42Type>(),
            Methods:          new Dictionary<string, Z42FuncType>(),
            StaticFields:     new Dictionary<string, Z42Type>(),
            StaticMethods:    new Dictionary<string, Z42FuncType>(),
            MemberVisibility: new Dictionary<string, Visibility>(),
            BaseClassName:    cls.BaseClass,
            TypeParams:       typeParams,
            IsStruct:         false);
    }

    private static Z42InterfaceType BuildInterfaceSkeleton(ExportedInterfaceDef iface)
    {
        IReadOnlyList<string>? typeParams = iface.TypeParams is { Count: > 0 } tps
            ? tps.AsReadOnly() : null;
        return new Z42InterfaceType(
            iface.Name,
            Methods:       new Dictionary<string, Z42FuncType>(),
            TypeArgs:      null,
            StaticMembers: null,
            TypeParams:    typeParams);
    }

    // ── Phase 2 helpers: 填充成员（in-place 修改 Phase 1 骨架的 mutable dict）──

    private static void FillClassMembersInPlace(
        ExportedClassDef                              cls,
        Z42ClassType                                  skeleton,
        IReadOnlyDictionary<string, Z42ClassType>     classes,
        IReadOnlyDictionary<string, Z42InterfaceType> interfaces)
    {
        // Cast back to Dictionary<> — BuildClassSkeleton 创建的是 mutable Dictionary.
        var fields        = (Dictionary<string, Z42Type>)skeleton.Fields;
        var staticFields  = (Dictionary<string, Z42Type>)skeleton.StaticFields;
        var methods       = (Dictionary<string, Z42FuncType>)skeleton.Methods;
        var staticMethods = (Dictionary<string, Z42FuncType>)skeleton.StaticMethods;
        var memberVis     = (Dictionary<string, Visibility>)skeleton.MemberVisibility;
        // L3 generic: propagate class's TypeParams so field/method signatures
        // containing `T` restore as Z42GenericParamType (not Z42PrimType("T")).
        var tpSet = cls.TypeParams is { Count: > 0 } tps
            ? new HashSet<string>(tps) : null;

        foreach (var f in cls.Fields)
        {
            var ft = ResolveTypeName(f.TypeName, tpSet, classes, interfaces);
            if (f.IsStatic) staticFields[f.Name] = ft;
            else            fields[f.Name]        = ft;
            memberVis[f.Name] = ParseVisibility(f.Visibility);
        }

        foreach (var m in cls.Methods)
        {
            var sig = RebuildFuncType(m.Params, m.ReturnType, m.MinArgCount, tpSet,
                classes, interfaces);
            if (m.IsStatic) staticMethods[m.Name] = sig;
            else            methods[m.Name]        = sig;
            string visKey = m.Name.Contains('$') ? m.Name[..m.Name.IndexOf('$')] : m.Name;
            memberVis.TryAdd(visKey, ParseVisibility(m.Visibility));
        }
    }

    /// 接口的 StaticMembers 字段在 Z42InterfaceType 中是 nullable，骨架时为 null。
    /// 如果方法集合包含 static members，需要"升级"骨架为带 StaticMembers 的新 record；
    /// 否则保持 in-place 修改 methods 字典。返回最终 InterfaceType（可能与 skeleton 同一引用，
    /// 也可能是替换实例，取决于是否有 static members）。
    private static Z42InterfaceType FillInterfaceMembersInPlace(
        ExportedInterfaceDef                          iface,
        Z42InterfaceType                              skeleton,
        IReadOnlyDictionary<string, Z42ClassType>     classes,
        IReadOnlyDictionary<string, Z42InterfaceType> interfaces)
    {
        // L3 primitive-as-struct: restore interface's type params so `T` in method
        // signatures (e.g. `T op_Add(T other)` in `INumber<T>`) resolves to
        // Z42GenericParamType on the consumer side rather than `Z42PrimType("T")`.
        var tpSet = iface.TypeParams is { Count: > 0 } tps
            ? new HashSet<string>(tps) : null;
        var methods       = (Dictionary<string, Z42FuncType>)skeleton.Methods;
        Dictionary<string, Z42StaticMember>? staticMembers = null;
        foreach (var m in iface.Methods)
        {
            var sig = RebuildFuncType(m.Params, m.ReturnType, m.MinArgCount, tpSet,
                classes, interfaces);
            if (m.IsStatic)
            {
                staticMembers ??= new Dictionary<string, Z42StaticMember>();
                // L3 static abstract tier (C# 11 alignment): reconstruct Kind from
                // (IsAbstract, IsVirtual) pair exactly as exported.
                var kind = m.IsAbstract ? StaticMemberKind.Abstract
                         : m.IsVirtual  ? StaticMemberKind.Virtual
                         : StaticMemberKind.Concrete;
                staticMembers[m.Name] = new Z42StaticMember(m.Name, sig, kind);
            }
            else
            {
                methods[m.Name] = sig;
            }
        }
        // 如果没 static members，骨架本身已被填充，直接返回；否则替换实例（带 StaticMembers）。
        // 替换实例时 Methods 字典引用保持不变（仍是骨架那个 Dictionary），其他类对该接口
        // 的方法引用仍然有效。
        if (staticMembers is null) return skeleton;
        return new Z42InterfaceType(iface.Name, skeleton.Methods,
            TypeArgs: skeleton.TypeArgs, StaticMembers: staticMembers,
            TypeParams: skeleton.TypeParams);
    }

    private static Z42FuncType RebuildFuncType(
        List<ExportedParamDef>                         parms,
        string                                         retType,
        int                                            minArgCount,
        HashSet<string>?                               genericParams = null,
        IReadOnlyDictionary<string, Z42ClassType>?     classes       = null,
        IReadOnlyDictionary<string, Z42InterfaceType>? interfaces    = null)
    {
        var paramTypes = parms.Select(p =>
            ResolveTypeName(p.TypeName, genericParams, classes, interfaces)).ToList();
        return new Z42FuncType(paramTypes,
            ResolveTypeName(retType, genericParams, classes, interfaces),
            minArgCount == paramTypes.Count ? -1 : minArgCount);
    }

    /// Resolve a type name string (as serialized in TSIG) back to a Z42Type.
    ///
    /// Lookup precedence:
    ///   1. Suffixed forms: `T[]` → array, `T?` → optional
    ///   2. Generic param in scope (`T` etc.)
    ///   3. Built-in primitive (`int`, `string`, ...)
    ///   4. Imported class type (from `classes` dict — Phase 2 only)
    ///   5. Imported interface type (from `interfaces` dict — Phase 2 only)
    ///   6. Fallback: `Z42PrimType(name)` —— Phase 1 占位 / 真未知
    ///
    /// Phase 1 调用时 classes / interfaces 为 null（仅基本元素 + sentinel），
    /// Phase 2 调用必须传入完整字典以避免降级。
    internal static Z42Type ResolveTypeName(
        string                                         name,
        HashSet<string>?                               genericParams = null,
        IReadOnlyDictionary<string, Z42ClassType>?     classes       = null,
        IReadOnlyDictionary<string, Z42InterfaceType>? interfaces    = null)
    {
        if (name.EndsWith("[]"))
            return new Z42ArrayType(ResolveTypeName(name[..^2], genericParams, classes, interfaces));
        if (name.EndsWith("?"))
            return new Z42OptionType(ResolveTypeName(name[..^1], genericParams, classes, interfaces));
        if (genericParams != null && genericParams.Contains(name))
            return new Z42GenericParamType(name);

        switch (name)
        {
            case "int"    or "i32": return Z42Type.Int;
            case "long"   or "i64": return Z42Type.Long;
            case "float"  or "f32": return Z42Type.Float;
            case "double" or "f64": return Z42Type.Double;
            case "bool":            return Z42Type.Bool;
            case "string":          return Z42Type.String;
            case "char":            return Z42Type.Char;
            case "object":          return Z42Type.Object;
            case "void":            return Z42Type.Void;
            case "null":            return Z42Type.Null;
            case "i8":              return Z42Type.I8;
            case "i16":             return Z42Type.I16;
            case "u8":              return Z42Type.U8;
            case "u16":             return Z42Type.U16;
            case "u32":             return Z42Type.U32;
            case "u64":             return Z42Type.U64;
            case "unknown":         return Z42Type.Unknown;
            case "error":           return Z42Type.Error;
        }

        // Phase 2: lookup imported class / interface registries to avoid
        // PrimType downgrade for self-reference / forward-reference fields.
        if (classes is not null && classes.TryGetValue(name, out var ct))
            return ct;
        if (interfaces is not null && interfaces.TryGetValue(name, out var it))
            return it;

        // Fallback: Phase 1 占位 / 真正未知名（拼写错误等，TypeChecker 后续报错）
        return new Z42PrimType(name);
    }

    private static Visibility ParseVisibility(string vis) => vis switch
    {
        "public"    => Visibility.Public,
        "private"   => Visibility.Private,
        "protected" => Visibility.Protected,
        "internal"  => Visibility.Internal,
        _           => Visibility.Public,
    };

    /// 空 ImportedSymbols。Used as start state for `Combine(...)` 折叠。
    public static ImportedSymbols Empty() => new(
        Classes:                new Dictionary<string, Z42ClassType>(StringComparer.Ordinal),
        Functions:              new Dictionary<string, Z42FuncType>(StringComparer.Ordinal),
        Interfaces:             new Dictionary<string, Z42InterfaceType>(StringComparer.Ordinal),
        EnumConstants:          new Dictionary<string, long>(StringComparer.Ordinal),
        EnumTypes:              new HashSet<string>(StringComparer.Ordinal),
        ClassNamespaces:        new Dictionary<string, string>(StringComparer.Ordinal),
        ClassConstraints:       new Dictionary<string, List<ExportedTypeParamConstraint>>(StringComparer.Ordinal),
        FuncConstraints:        new Dictionary<string, List<ExportedTypeParamConstraint>>(StringComparer.Ordinal),
        ClassInterfaces:        new Dictionary<string, List<string>>(StringComparer.Ordinal));

    /// 合并两个 ImportedSymbols。`high` 优先（同名条目覆盖 `low`）。
    /// 用于 PackageCompiler 多 CU 编译时把同包内 intraSymbols (high) 与
    /// 外部 zpkg externalImported (low) 合并：本包内 declarations 总是覆盖
    /// 外部同名（防 stale zpkg 干扰）。
    public static ImportedSymbols Combine(ImportedSymbols low, ImportedSymbols high)
    {
        var classes = new Dictionary<string, Z42ClassType>(low.Classes, StringComparer.Ordinal);
        foreach (var (k, v) in high.Classes) classes[k] = v;

        var funcs = new Dictionary<string, Z42FuncType>(low.Functions, StringComparer.Ordinal);
        foreach (var (k, v) in high.Functions) funcs[k] = v;

        var interfaces = new Dictionary<string, Z42InterfaceType>(low.Interfaces, StringComparer.Ordinal);
        foreach (var (k, v) in high.Interfaces) interfaces[k] = v;

        var enumConsts = new Dictionary<string, long>(low.EnumConstants, StringComparer.Ordinal);
        foreach (var (k, v) in high.EnumConstants) enumConsts[k] = v;

        var enumTypes = new HashSet<string>(low.EnumTypes, StringComparer.Ordinal);
        foreach (var t in high.EnumTypes) enumTypes.Add(t);

        var classNs = new Dictionary<string, string>(low.ClassNamespaces, StringComparer.Ordinal);
        foreach (var (k, v) in high.ClassNamespaces) classNs[k] = v;

        var classConstraints = MergeNullable(low.ClassConstraints, high.ClassConstraints);
        var funcConstraints  = MergeNullable(low.FuncConstraints,  high.FuncConstraints);
        var classInterfaces  = MergeNullable(low.ClassInterfaces,  high.ClassInterfaces);

        return new ImportedSymbols(classes, funcs, interfaces, enumConsts, enumTypes, classNs,
            classConstraints, funcConstraints, classInterfaces);
    }

    private static Dictionary<string, T>? MergeNullable<T>(
        Dictionary<string, T>? low, Dictionary<string, T>? high)
    {
        if (low is null && high is null) return null;
        var result = low is null
            ? new Dictionary<string, T>(StringComparer.Ordinal)
            : new Dictionary<string, T>(low, StringComparer.Ordinal);
        if (high is not null)
            foreach (var (k, v) in high) result[k] = v;
        return result;
    }
}

/// Imported symbols from dependency zpkg TSIG sections, ready to be merged
/// into the SymbolCollector.
public sealed record ImportedSymbols(
    Dictionary<string, Z42ClassType>     Classes,
    Dictionary<string, Z42FuncType>      Functions,
    Dictionary<string, Z42InterfaceType> Interfaces,
    Dictionary<string, long>             EnumConstants,
    HashSet<string>                      EnumTypes,
    /// Maps short class name (e.g. "Console") to its original namespace (e.g. "Std.IO").
    /// Used by IrGen to qualify imported class names with the correct dependency namespace
    /// instead of the local file's namespace.
    Dictionary<string, string>           ClassNamespaces,
    /// L3-G3d: raw serialized `where` constraints keyed by decl short name.
    /// Consumer TypeChecker resolves these to `GenericConstraintBundle` after all
    /// imported interfaces / classes are available, then merges into its constraint maps.
    Dictionary<string, List<ExportedTypeParamConstraint>>? ClassConstraints = null,
    Dictionary<string, List<ExportedTypeParamConstraint>>? FuncConstraints  = null,
    /// L3-G4b primitive-as-struct: imported class → declared interface list
    /// (by short name). Enables data-driven `PrimitiveImplementsInterface`
    /// to work when stdlib `struct int : IComparable<int>` is loaded from a zpkg.
    Dictionary<string, List<string>>?    ClassInterfaces = null);
