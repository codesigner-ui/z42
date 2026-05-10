# Design: include 机制（C2）

## Architecture

```
ManifestLoader.LoadMember(memberPath)
  ├─ 1. 解析 member <name>.z42.toml 原始 TOML
  ├─ 2. 应用 workspace 共享继承（C1 已有）
  │     - [workspace.project] 引用展开
  │     - [workspace.dependencies] 引用展开
  ├─ 3. ★ NEW: IncludeResolver.Resolve(memberPath)
  │     - 沿 include 链 DFS
  │     - 检测循环（WS020） / 深度（WS022） / 路径（WS023/024）
  │     - 解析每个 preset 文件 → 校验段限制（WS021）
  │     - 输出有序 preset 列表（按 include 声明顺序展开）
  ├─ 4. ★ NEW: ManifestMerger.Merge(presets, member)
  │     - 按顺序：preset_1 → preset_2 → ... → member
  │     - 标量覆盖、表合并、数组整体覆盖
  ├─ 5. 字段验证（含路径模板展开 via PathTemplateExpander）
  ├─ 6. ResolvedManifest 输出（Origins 字典含 IncludePreset 来源）
  └─ 7. policy 强制应用（C3 才实施，C2 占位）
```

## Decisions

### Decision D2.1: include 路径不允许 glob

**问题**：是否支持 `include = ["presets/*.toml"]`？

**决定**：不允许（D7 已锁）。

**理由**：
- 显式可读 > 简短：列出每个 preset 比"匹配什么"更易溯源
- 避免新增 preset 文件意外被某 member 自动拉入（行为变化）
- 有需求时用户可显式列举多个文件

### Decision D2.2: 数组整体覆盖（不连接）

**问题**：preset 写 `[sources] include = [...]`、member 也写 → 合并还是覆盖？

**选项**：
- A. 数组连接（preset + member 元素拼接）
- B. 数组整体覆盖（member 完全替换 preset）

**决定**：选 B。

**理由**：
- 连接语义会让最终列表难推测（"preset 加了哪些？被去重了吗？"）
- 整体覆盖等于"member 自己声明的就是最终值"，与标量字段一致，认知一致性高
- 用户需要"在 preset 基础上添加"时显式写 `include = [...preset_的内容..., 自己的]`，反而强制思考

### Decision D2.3: 嵌套深度上限 8 层

**问题**：嵌套深度限制取多少。

**决定**：8 层。

**理由**：
- 防爆栈（DFS 无限循环也由 WS020 兜底，但深度限制是 defense-in-depth）
- 实际工程中超过 3 层已属罕见，8 层留足余量
- 报错时清晰列出整条链，便于用户自检

### Decision D2.4: include 在合并顺序中的位置

**问题**：include 链的合并是在 workspace 共享继承之前还是之后？

**选项**：
- A. include → workspace 共享 → member（include 是最低优先级默认）
- B. workspace 共享 → include → member（推荐）
- C. workspace 共享 → member → include（include 反过来覆盖 member）

**决定**：选 B。

**理由**：
- workspace 共享是"全仓元数据"（如 license / version），优先级应高于子树 preset（preset 是子树默认而非治理）
- member 自身字段是最终决定者，应能覆盖任何继承层（除 policy）
- C 选项的"include 反过来覆盖 member"违反"声明 include 表示拉入默认"的直觉

### Decision D2.5: preset 文件命名约定

**问题**：是否强制 preset 文件名（如 `*.preset.toml` / 必须放 `presets/`）？

**决定**：不强制。

**理由**：
- 强制约定限制用户组织自由
- 通过 `include` 字段已经显式声明"这个文件是 preset"，无需再用文件名标记
- 社区约定 `presets/` 目录会自然形成

### Decision D2.6: workspace 根本身的 include

**问题**：`z42.workspace.toml` 是否可以写 include 拉入"全局共享 preset"？

**选项**：
- A. 允许（最大化灵活性）
- B. 不允许（C2 简化）

**决定**：选 B（C2 阶段）；future 评估。

**理由**：
- workspace 根的 `[workspace.project]` / `[workspace.dependencies]` / `[policy]` 已是"全局共享"集中点
- 让根 manifest 也走 include 会增加治理溯源复杂度（policy 可能间接来自 preset）
- 现有需求未论证（无人提出"多个 workspace 共享同一组 policy"的真实场景）
- 锁住简单语义，待真实需求出现再放开

### Decision D2.7: 重复 include（菱形）

**问题**：`include = [a, b]`，a 和 b 都 include c → c 应被合并几次？

**决定**：去重，c 只合并一次。

**理由**：
- 重复合并对最终值无影响（c 自我覆盖自己），但会让 Origins 来源链冗余
- 与 C/C++ `#include` 的 `#pragma once` 行为一致，符合直觉
- 检测方式：按规范化绝对路径 hash 去重

## Implementation Notes

### IncludeResolver

```csharp
public sealed class IncludeResolver
{
    public sealed record IncludeChainNode(
        string AbsolutePath,
        string DeclaredIn,        // 声明此 include 的文件
        int Depth);

    // 输出：按合并顺序排列的 preset 列表（深度优先，include 数组顺序）
    // 失败抛 ManifestException（WS020/022/023/024）
    public IReadOnlyList<IncludeChainNode> Resolve(string memberPath, PathTemplateExpander expander);
}
```

DFS 实现要点：
- `visiting` 集合检测当前路径上的环（DFS 着色）
- `visited` 集合检测菱形（已合并过的不再二次合并）
- 深度计数器在每次进入新文件时 +1，超过 8 → WS022
- 路径规范化用 `Path.GetFullPath`，统一 `/` 与 `\`

### ManifestMerger

```csharp
public sealed class ManifestMerger
{
    public sealed record MergeResult(
        ResolvedManifest Manifest,
        Dictionary<string, FieldOrigin> Origins);

    public MergeResult Merge(
        WorkspaceSharedView workspaceShared,    // C1 已有
        IReadOnlyList<MemberManifest> presets,  // 按 include 顺序
        MemberManifest selfMember);
}
```

合并语义实现：
- 标量：`result.X = candidate.X ?? result.X`（后者覆盖）
- 表：递归合并字段
- 数组：`result.Arr = candidate.Arr ?? result.Arr`（整体覆盖）
- Origins：每次覆盖时更新该字段的来源记录

### Preset 段限制校验

```csharp
public static class PresetValidator
{
    // 解析 TOML 后立即调用，扫描禁用段
    // 含禁用段 → 抛 ManifestException(WS021)
    public static void ValidatePresetSections(TomlTable raw, string presetPath);
}
```

禁用段集合（C2 实现）：

| 路径 | 报错原因 |
|---|---|
| `[workspace]` / `[workspace.*]` | 全仓共享必须从根下发 |
| `[policy]` | 治理一致性：策略不能由 preset 注入 |
| `[profile]` / `[profile.*]` | profile 集中在 workspace 根 |
| `[project].name` | 身份字段，必须 member 自己声明 |
| `[project].entry` | 同上 |
| `[project].version`（除非引用 workspace） | 同上 |

### 与现行单工程模式的关系

C1 已支持 `[workspace]` 与"无 workspace 单工程"两条路径。C2 的 include 在**两条路径都生效**：

- workspace 模式：member 可写 include 拉 preset
- 单工程模式：单 manifest 也可写 include 拉 preset（如复用一组 lints）

但单工程模式下 preset 不能引用 `[workspace.*]`（无 workspace 上下文）；这一约束由 PresetValidator 保证（preset 永远不允许 workspace 段）。

### Origins 数据模型扩展

```csharp
public enum OriginKind {
    MemberDirect,
    WorkspaceProject,
    WorkspaceDependency,
    IncludePreset,        // ★ NEW
    PolicyLocked,         // C3 占位
}

public sealed record FieldOrigin(
    string FilePath,
    string FieldPath,
    OriginKind Kind,
    IReadOnlyList<string>? IncludeChain = null  // ★ NEW: preset 来源时记录展开链
);
```

### 测试策略

| 测试类 | 覆盖 |
|---|---|
| `IncludeResolverTests` | DFS / 循环 / 深度 / 路径合法性 / WS020/022/023/024 |
| `ManifestMergerTests` | 标量覆盖 / 表合并 / 数组覆盖 / preset 段校验 WS021 / Origins 准确性 |
| `IncludeIntegrationTests` | 端到端：从 example workspace 加载 → ResolvedManifest 与 expected_resolved.json 对照 |

## Open Risks

| 风险 | 缓解 |
|---|---|
| TOML 库对 "数组整体覆盖" 没有原生支持 | 我们手动控制合并；TOML 只用作解析 |
| Path.GetFullPath 在 macOS / Windows 路径分隔符差异 | 入参规范化为 `/`，输出再交给 .NET |
| Origins.IncludeChain 在深嵌套时显示长 | `info --resolved` 默认折叠，加 `--verbose` 才展开（C4 实施）|

## C3/C4 衔接

- C3 增加 `OriginKind.PolicyLocked` 与 `FieldOrigin.IsLocked` 标记
- C4 的 `info --resolved` 直接消费 `Origins`，无需重新设计
