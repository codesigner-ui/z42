using Z42.Core;
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
public static partial class ImportedSymbolLoader
{
    /// <summary>
    /// 兼容入口：按 namespace 字符串过滤（无 package 概念）。
    /// 主要给单元测试 / GoldenTests 用，每个 namespace 独立形成一个虚拟 package。
    /// 生产代码（PackageCompiler / SingleFileCompiler）应改用四参版本，按
    /// activated/prelude package 过滤（strict-using-resolution，2026-04-28）。
    /// </summary>
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
        var classPackages    = MergeNullable(low.ClassPackages,    high.ClassPackages);

        // strict-using-resolution: 合并 collisions（intraSymbols 端理论无冲突，
        // 但保留 low 端的 collisions 以免 PackageCompiler 路径丢信息）。
        IReadOnlyList<NamespaceCollision>? collisions = null;
        if (low.Collisions is { Count: > 0 } lc || high.Collisions is { Count: > 0 } hc)
        {
            var merged = new List<NamespaceCollision>();
            if (low.Collisions  is not null) merged.AddRange(low.Collisions);
            if (high.Collisions is not null) merged.AddRange(high.Collisions);
            collisions = merged;
        }

        var delegates = MergeNullable(low.Delegates, high.Delegates);

        HashSet<string>? resolvedNs = null;
        if (low.ResolvedNamespaces is not null || high.ResolvedNamespaces is not null)
        {
            resolvedNs = new HashSet<string>(StringComparer.Ordinal);
            if (low.ResolvedNamespaces  is not null) resolvedNs.UnionWith(low.ResolvedNamespaces);
            if (high.ResolvedNamespaces is not null) resolvedNs.UnionWith(high.ResolvedNamespaces);
        }

        return new ImportedSymbols(classes, funcs, interfaces, enumConsts, enumTypes, classNs,
            classConstraints, funcConstraints, classInterfaces, classPackages, collisions,
            Delegates: delegates,
            ResolvedNamespaces: resolvedNs);
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
    Dictionary<string, List<string>>?    ClassInterfaces = null,
    /// strict-using-resolution (2026-04-28): per-class source package, for
    /// "did you forget `using <ns>;`?" hints + collision diagnostics.
    Dictionary<string, string>?          ClassPackages   = null,
    /// strict-using-resolution: collision records — same (namespace, class-name)
    /// claimed by 2+ activated packages. TypeChecker emits E0601 from this list.
    /// Each entry: (namespace, className, packageNames[]).
    IReadOnlyList<NamespaceCollision>?   Collisions      = null,
    /// 2026-05-02 add-generic-delegates (D1c): imported delegate type registry
    /// (key style matches `SymbolTable.Delegates`: `Foo` / `Foo$N` /
    /// `Class.Foo` / `Class.Foo$N`). Replaces hardcoded `Action`/`Func` desugar.
    Dictionary<string, DelegateInfo>?    Delegates       = null,
    /// 2026-05-06 fix-cross-zpkg-using-resolution: namespaces declared by any
    /// activated module (via `mod.Namespace`), regardless of whether the module
    /// contributes classes / interfaces / enums. Required for E0602 to recognize
    /// `using <ns>;` on packages that contribute only impl blocks (no classes),
    /// e.g. cross-zpkg `impl Trait for Type` where the impl-providing zpkg has
    /// its own namespace but no class declarations.
    HashSet<string>?                     ResolvedNamespaces = null);

/// strict-using-resolution: 跨包同 (ns, name) 冲突描述。
public sealed record NamespaceCollision(
    string                Namespace,
    string                ClassName,
    IReadOnlyList<string> Packages);
