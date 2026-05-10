# Design: Policy 与集中产物布局（C3）

## Architecture

```
ManifestLoader.LoadMember(memberPath)
  ├─ 1-4. C1+C2 已有阶段（解析 → workspace 共享 → include → 合并）
  ├─ 5. 字段验证（含 PathTemplateExpander，C1 已有）
  ├─ 6. ★ NEW: PolicyEnforcer.Enforce(merged, workspacePolicy)
  │     - 收集 workspace 显式 [policy] 字段路径
  │     - 加入默认锁定字段（build.out_dir / build.cache_dir）
  │     - 遍历 merged manifest，比对每个 policy 字段
  │     - 不一致 → 抛 ManifestException(WS010)
  │     - 一致 → 在 Origins 中标记 PolicyLocked
  ├─ 7. ★ NEW: CentralizedBuildLayout.Resolve(merged, profile)
  │     - 计算 EffectiveOutDir / EffectiveCacheDir / EffectiveProductPath
  │     - 应用 ${profile} / ${member_name} 模板展开（PathTemplateExpander）
  └─ 8. ResolvedManifest 输出（Build.IsCentralized / EffectiveProductPath / Origins）

PackageCompiler.Compile(member)
  ├─ 调用 ResolvedManifest.Build.EffectiveProductPath
  └─ 写产物到该路径
```

## Decisions

### Decision D3.1: Policy 字段路径表达式语法

**问题**：怎么表达"锁定 `[profile.release]` 的 `strip` 字段"？

**选项**：
- A. 嵌套 TOML：`[policy.profile.release] strip = true`
- B. 字符串键 + 点路径：`[policy] "profile.release.strip" = true`
- C. 字段函数：`[policy] lock("profile.release.strip", true)`

**决定**：选 B。

**理由**：
- A. 嵌套 TOML 语法简洁但语义混乱：`[policy]` 段本身代表"锁定字典"，里面再嵌套 `[profile.release]` 像是 policy 自己有 profile 配置
- B. 字符串键路径直观、扁平、机读机写都简单（dictionary 形态）
- C. 函数语法引入 DSL，与"声明式"哲学冲突
- B 与 MSBuild `<PropertyName>` / Cargo `[profile]` 表达力相当，无认知负担

### Decision D3.2: 默认锁定字段集合（D5）

**问题**：哪些字段不需要用户在 `[policy]` 显式声明就自动锁定？

**决定**：仅锁定 `build.out_dir` 与 `build.cache_dir`。

**理由**：
- 集中产物路径是 workspace 模型的核心承诺（"所有产物在一处"）；如果不默认锁定，member 可改 `out_dir` 导致产物分散，破坏整个集中布局
- 其他字段（如 `profile.release.strip`）有合理的 per-member 覆盖场景（如某 member 是 stub 不需要 strip）—— 让用户显式声明
- "默认锁定"是 opinionated default：z42 选择"集中"作为默认 monorepo 形态，与 Cargo `target/` 一致

### Decision D3.3: 集中产物路径模式

**问题**：`dist/` 下产物如何组织？

**选项**：
- A. 一层：`dist/<member>.zpkg`
- B. 两层：`dist/<member>/<name>.zpkg`
- C. 按 kind 分类：`dist/libs/<member>.zpkg` / `dist/apps/<member>.zpkg`

**决定**：产物 `dist/<member>.zpkg`（一层），cache `cache/<member>/<file>.zbc`（两层）。

**理由**：
- 产物文件名等于 member name（C1 已规定）天然唯一，无需额外子目录
- cache 必须按 member 分目录，否则不同 member 的同名源文件互相覆盖
- 不按 kind 分类（C 选项）：kind 是逻辑标签，不是产物层级；分类制造无意义嵌套
- 与 Cargo `target/<crate>.rlib` 风格一致

### Decision D3.4: Member `[build]` 在 workspace 模式的处理

**问题**：member 写 `[build] out_dir = "x"` 是 warning（忽略）还是 error？

**选项**：
- A. Warning + 忽略（"宽容迁移"）
- B. Error（"显式优先"）

**决定**：选 B（直接 WS010 error）。

**理由**：
- z42 处于 pre-1.0，无现有工程需迁移
- "宽容忽略"会让 member 作者误以为字段生效，调试时困惑
- "显式优先"哲学一致：被 policy 锁定的字段就是不能写，写就是错
- 错误信息明确告知"由 workspace 锁定，不可在此覆盖"

### Decision D3.5: profile 派生产物的表达方式

**问题**：怎么实现 `dist/debug/` 与 `dist/release/` 分离？

**选项**：
- A. 引入新字段 `[workspace.build.profile.<name>] out_dir = ...`
- B. 用 `${profile}` 模板：`out_dir = "dist/${profile}"`

**决定**：选 B。

**理由**：
- B 复用 C1 已实施的 PathTemplateExpander，零新增机制
- A 引入新嵌套段，增加 schema 复杂度且不能与其他模板变量组合
- 用户想要 `dist/<member>-<profile>.zpkg` 等灵活布局也直接表达

### Decision D3.6: Policy 字段路径不存在的处理

**问题**：用户在 `[policy]` 写错字段路径（如 `"buld.out_dir"`）怎么办？

**选项**：
- A. 静默忽略（与 Cargo profile 写错字段类似）
- B. 启动时报 WS011

**决定**：选 B。

**理由**：
- 静默会让"我以为锁定了但实际没锁"成为难调试 bug
- z42 整体偏好 fail-fast；早报错让用户立刻发现
- 错误信息附编辑距离最近的有效字段路径建议（如 `"build.out_dir"`），降低用户负担

## Implementation Notes

### PolicyEnforcer

```csharp
public sealed class PolicyEnforcer
{
    public sealed record PolicyResult(
        Dictionary<string, FieldOrigin> LockedFields,  // 字段路径 → 来源
        IReadOnlyList<PolicyViolation> Violations);

    public sealed record PolicyViolation(
        string FieldPath,
        object LockedValue,
        object MemberValue,
        string LockedFromFile,
        string MemberFromFile);

    public PolicyResult Enforce(
        ResolvedManifest merged,
        IReadOnlyDictionary<string, object> workspacePolicy);
}
```

执行流程：
1. 计算最终 policy 字典：默认锁定（`build.out_dir` / `build.cache_dir`） ∪ workspace 显式 `[policy]`
2. 对每个 policy 字段路径：
   - 在 merged manifest 中查找该字段值（用 PolicyFieldPath token 化）
   - 找不到字段 → WS011
   - 找到但与锁定值不等 → 收集 PolicyViolation → 最后抛 WS010
   - 相等 → Origins[fieldPath].Kind = PolicyLocked
3. 全部检查通过 → 返回 PolicyResult

### PolicyFieldPath

```csharp
public sealed class PolicyFieldPath
{
    public sealed record Token(string Name, bool IsArrayIndex);

    public static IReadOnlyList<Token> Parse(string path);

    // 在 ResolvedManifest 中按 token 路径查找/赋值
    public static object? Resolve(ResolvedManifest m, IReadOnlyList<Token> tokens);
}
```

支持的字段路径前缀：
- `build.<field>`：`out_dir` / `cache_dir` / `incremental` / `mode`
- `profile.<name>.<field>`：profile 子段任意字段
- `project.<field>`：member [project] 字段（除身份字段）
- 不允许：`workspace.*`（workspace 段不被 policy 自我锁定）/ `dependencies.*`（依赖锁定 = 不允许覆盖某依赖；语义复杂，future）

### CentralizedBuildLayout

```csharp
public sealed class CentralizedBuildLayout
{
    public sealed record Layout(
        string EffectiveOutDir,        // 绝对路径
        string EffectiveCacheDir,      // 绝对路径
        string EffectiveProductPath);  // out_dir/<member>.zpkg

    public Layout Resolve(
        string workspaceRoot,
        WorkspaceBuildConfig wsBuild,
        string memberName,
        string profile,
        PathTemplateExpander expander);
}
```

执行：
- 取 `wsBuild.OutDir`（默认 `"dist"`），用 `expander.Expand` 展开（含 `${profile}` / `${workspace_dir}` / `${member_name}`）
- 与 `workspaceRoot` 拼接为绝对路径（如非已绝对）
- `EffectiveProductPath = Path.Combine(EffectiveOutDir, $"{memberName}.zpkg")`
- cache 同理：`Path.Combine(EffectiveCacheDir, memberName, ...)`

### 与现行 PackageCompiler 的衔接

C3 修改 `PackageCompiler.Compile` 流程：

```csharp
// Before
var outDir = manifest.Build.OutDir;
var path = Path.Combine(outDir, $"{name}.zpkg");

// After
var path = manifest.Build.EffectiveProductPath;  // 已由 CentralizedBuildLayout 计算
```

由于 C1 已让 `ResolvedManifest.Build` 是计算后的视图，C3 只在计算阶段补充 `EffectiveProductPath`，`PackageCompiler` 改动极小。

### 错误码处理

- WS010：抛 `ManifestException`，error 级
- WS011：抛 `ManifestException`，error 级，附 fuzzy match 建议（用 Levenshtein 距离≤3 的有效路径）
- WS004：废弃，C3 归档时从 ManifestErrors.cs 删除常量；旧文档中的引用统一改 WS010

### 测试策略

| 测试类 | 覆盖 |
|---|---|
| `PolicyEnforcerTests` | 默认锁定 / 显式锁定 / 相同值不报错 / WS010 / WS011 / fuzzy 建议 / preset 与 member 同时违反 |
| `CentralizedBuildLayoutTests` | 默认布局 / `${profile}` 展开 / 自定义 out_dir / cache 按 member 分目录 |
| `PolicyIntegrationTests` | 端到端 `examples/workspace-with-policy/` → expected_resolved.json / 故意写违规 member → WS010 |

## Open Risks

| 风险 | 缓解 |
|---|---|
| Policy 字段路径与 ResolvedManifest 字段名不严格映射（如驼峰 vs 下划线） | 统一用 toml 字段名（lower_snake_case）作为路径 token；PolicyFieldPath.Resolve 中显式映射 |
| 集中产物变化破坏现有 [src/compiler/z42.Project/ZpkgWriter.cs](../../../src/compiler/z42.Project/ZpkgWriter.cs) 测试 | 测试中改用 `ResolvedManifest.Build.EffectiveProductPath` 作为期望路径，不写死 |
| profile 不在 manifest 中预声明（`--profile staging`）→ ${profile} 怎么处理 | 自定义 profile 必须在 `[profile.<name>]` 中声明，否则报错（已在 C1 规则）|

## C4 衔接

- C4 的 `info --resolved` 直接显示 `Origins[field].Kind == PolicyLocked` 时打 🔒
- C4 的 `clean` 命令直接读 `EffectiveOutDir` / `EffectiveCacheDir`，无需重新计算
- C4 的 `WorkspaceBuildOrchestrator` 拓扑排序按 member name 写产物到 `EffectiveProductPath`
