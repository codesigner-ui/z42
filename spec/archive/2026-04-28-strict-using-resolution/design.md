# Design: strict-using-resolution

## Architecture

```
[zpkg files on disk]
       │
       ▼
TsigCache (path → meta.PackageName + namespaces)
       │
       │  [activatedPackages, preludePackages]
       ▼
ImportedSymbolLoader.Load(modules, packageOfModule, activated, prelude)
       │
       ▼
ImportedSymbols { Classes, Interfaces, ..., PackageOf, Collisions }
       │
       ▼
TypeChecker
   ├─ 报 E0210 (collision)
   ├─ 报 E0211 (unresolved using)
   └─ 类型解析时根据 PackageOf 提供 "missing using?" 提示
```

## Decisions

### D1: 哪些包是 prelude？

- **方案 A（采纳）**：硬编码 `["z42.core"]`
- 方案 B：z42.workspace.toml `[workspace.prelude]` 字段配置

**决定 A**：trust model 简单，无 supply-chain 攻击面；后续如需扩展再升级到 B。

### D2: 同 namespace 多包场景

- 同 `(namespace, class-name)` 必唯一 → 报 E0210 错误
- 同 `namespace` 不同类名 → 合并允许（C# assembly 风格；Std.Collections 在
  z42.core 与 z42.collections 间分裂的合法用法）

### D3: 保留前缀策略

- 方案 A：硬错误（破坏第三方临时调试）
- **方案 B（采纳）**：软警告 W0212，不阻断
- 方案 C：无策略

**决定 B**：避免对外部包过度限制，但提示开发者改名。

### D4: PackageOf 信息源

- ExportedModule 不动（保持纯类型 schema）
- TsigCache 持有副表 `path → packageName`，调用 ImportedSymbolLoader 时
  把 `module → packageName` 字典一起传入
- ImportedSymbols.PackageOf: `Dictionary<className, packageName>`，TypeChecker
  消费做 "missing using?" 提示

### D5: Golden test 迁移策略

- 一次性扫描全部 source.z42，按使用类型推断需要的 using 列表
- 工具脚本 `scripts/audit-missing-usings.sh`（可后续删）
- 用户已确认（2026-04-28）：可一次性补齐，不留兼容路径

## Implementation Notes

### PreludePackages.cs（新文件）

```csharp
namespace Z42.Core;

public static class PreludePackages
{
    /// 隐式 prelude 包名单。这些包内的所有 namespace 默认激活，无需 using。
    /// 当前仅 z42.core；扩展需 spec proposal。
    public static readonly IReadOnlySet<string> Names =
        new HashSet<string>(StringComparer.Ordinal) { "z42.core" };

    public static bool IsPrelude(string packageName) => Names.Contains(packageName);
}
```

### ImportedSymbolLoader 新签名

```csharp
public static ImportedSymbols Load(
    IReadOnlyList<ExportedModule> modules,
    IReadOnlyDictionary<ExportedModule, string> packageOf,  // module → 包名
    IReadOnlyCollection<string> activatedPackages,           // user usings 解析出来
    IReadOnlyCollection<string>? preludePackages = null)     // null → 用 PreludePackages.Names
```

- 第一行：`preludePackages ??= PreludePackages.Names;`
- 第二行：`var allowed = new HashSet<string>(activatedPackages); allowed.UnionWith(preludePackages);`
- Phase 1 骨架登记时按 packageOf 过滤，并对每个 (ns, name) 维护"哪些 package
  已贡献过"映射；冲突写入 Collisions 列表
- 删除原 `allowedNs.Add("Std")` 硬编码

### TsigCache 改造

- 现 `_nsToPaths: Dictionary<string, List<string>>` → 加 `_pathToPkgName:
  Dictionary<string, string>`（zpkg path → packageName）
- LoadAll / LoadForUsings 返回 `(modules, packageOfModule)` tuple
- LoadForUsings 接到 caller 主路径

### PackageCompiler 调用点

```csharp
private static ImportedSymbols LoadExternalImported(
    TsigCache? tsigCache,
    IReadOnlyList<string> userUsings,  // ← 新增参数
    IReadOnlyDictionary<string, ExportedModule>? cachedExports = null)
{
    if (tsigCache is null) return ImportedSymbolLoader.Empty();

    // prelude 包内 namespace 全集
    var preludeNamespaces = tsigCache.NamespacesOfPackages(PreludePackages.Names);
    var allNamespaces = new HashSet<string>(userUsings);
    allNamespaces.UnionWith(preludeNamespaces);

    var (modules, packageOf) = tsigCache.LoadForNamespaces(allNamespaces);
    // ... cachedExports 注入 ...
    return ImportedSymbolLoader.Load(
        modules, packageOf,
        activatedPackages: tsigCache.PackagesProviding(userUsings),
        preludePackages: PreludePackages.Names);
}
```

### TypeChecker 集成

- 现 TypeChecker 拿 ImportedSymbols 后只用 Classes/Interfaces 等 dict
- 增：进 TypeCheck 主循环前，迭代 `imported.Collisions` 报 E0210
- UsingDirective 解析时：找不到提供该 namespace 的包 → E0211
- UnknownIdentifier 报错时：尝试在所有 zpkg 反查同名类型，找到则附加
  "consider adding `using <ns>;`" hint

### golden test 迁移

- 写脚本 `scripts/audit-missing-usings.sh`：扫描每个 `source.z42`，
  - 检测 `Console.` / `File.` / `Directory.` → 提示加 `using Std.IO;`
  - 检测 `Math.` → `using Std.Math;`
  - 检测 `new Queue<>` / `new Stack<>` → `using Std.Collections;`
  - 检测 `StringBuilder` → `using Std.Text;`
  - 检测 `TestRunner` / `Test.` → `using Std.Test;`
  - 检测 `Random` → `using Std.Math;`
- 自动 patch（追加在 `// Test:` 注释后第一行）
- 跑一遍 build-stdlib + regen + test-vm；剩余红的手工补

## Testing Strategy

- **单元（ImportedSymbolLoaderTests）**：
  - prelude 包默认激活
  - 非 prelude 包不在 activated 列表 → 不可见
  - 同 (ns, name) 多 package → Collisions 含条目
  - PackageOf 反查 hint 用法

- **集成（UsingResolutionTests, NEW）**：
  - 用 fixture 模拟两个 mock zpkg 同 (ns, name) → E0210
  - 用 stdlib 实包：写源码漏 `using Std.IO;` → E0204 + hint
  - 写 `using NoSuch;` → E0211
  - 写 `using Std.Collections;` 后 Queue 可见

- **Golden（端到端）**：100+ test 全绿

- **反向覆盖**：删除 fix-using-prelude-include 的 hardcoded `Std` 注入；
  原回归用例 `21_using_collections/` 应仍 pass（z42.core 是 prelude，
  IEquatable 自动可见 → Dictionary 约束满足）

## Risk

- **大批量 golden test 修改**：50+ 个文件可能要补 using，逐个验证耗时
  - 缓解：脚本辅助 + 一次性补
- **z42.core 内多 CU 互引**：Phase 1 collect 时各 CU 各自有 cu.Usings；
  intraSymbols 仍按"包内全可见"而不是按 using 过滤（同包内不需要 using）
  - 缓解：MergeImported(intraSymbols) 与 MergeImported(externalImported) 路径
    分开；intraSymbols 永远全部可见
- **stdlib 互引**：z42.io 引用 Std → 由于 z42.core 是 prelude，自动可见，
  无需 z42.io 自己写 `using Std;`
