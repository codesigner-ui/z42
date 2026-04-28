namespace Z42.Core;

/// <summary>
/// 隐式 prelude 包名单。这些包内的所有 namespace 默认激活，无需 using。
///
/// 当前仅 z42.core；扩展任何包都需走 spec proposal（trust model 决策点）。
///
/// 设计参见 spec/archive/2026-04-28-strict-using-resolution/。
/// </summary>
public static class PreludePackages
{
    public static readonly IReadOnlySet<string> Names =
        new HashSet<string>(StringComparer.Ordinal) { "z42.core" };

    public static bool IsPrelude(string packageName) => Names.Contains(packageName);

    /// stdlib 包名前缀（`z42.*`）。这些包可以使用保留 namespace 前缀（Std/Std.*）。
    /// 第三方包以非 `z42.` 开头，使用 Std.* 会触发 W0603 警告。
    public static bool IsStdlibPackage(string packageName) =>
        packageName.StartsWith("z42.", StringComparison.Ordinal);

    /// 保留的 namespace 前缀；非 stdlib 包声明这些前缀会发 W0603 警告。
    public static readonly IReadOnlyList<string> ReservedNamespacePrefixes =
        new[] { "Std" };

    /// 检查给定 namespace 是否落在保留前缀下。
    /// 例：`Std`、`Std.Foo`、`Std.Foo.Bar` 均匹配 prefix `Std`。
    public static bool IsReservedNamespace(string ns)
    {
        foreach (var prefix in ReservedNamespacePrefixes)
        {
            if (ns == prefix) return true;
            if (ns.StartsWith(prefix + ".", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
