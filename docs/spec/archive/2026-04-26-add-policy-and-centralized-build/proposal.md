# Proposal: 引入 [policy] 强制策略与集中产物布局（C3）

## Why

C1 留下了 `[workspace.build]` 与 `[policy]` 段的占位语法，但实际行为未实施：

1. **治理无强制力**：workspace 想强制"所有 release 必须 strip"或"统一 cache 位置"，目前只能写在 `[profile.release]` 里，member 仍可在自己 manifest 中写 `[profile.release] strip = false` 绕过（C1 已禁，但未给治理替代）
2. **产物分散**：每个 member 各自的 `dist/` + `.cache/`，monorepo 中：
   - 缓存命中率低（同名源文件互相覆盖风险）
   - 路径不可预测（IDE / CI 工具难以集中扫描产物）
   - `clean` 操作必须遍历每个 member
3. **profile 派生产物路径无表达力**：`debug` / `release` 想分流到不同目录（`dist/debug/` / `dist/release/`）目前无机制

C3 把 C1 占位的两块语法落地为**实际行为**：

- `[policy]` 段：声明字段路径锁定值，member 与 include 的对应字段不可覆盖
- `[workspace.build]`：集中 `dist/` + `.cache/` 到 workspace 根，member 的 `[build]` 在 workspace 模式下被忽略

## What Changes

| 变更 | 说明 |
|---|---|
| **`[policy]` 段实际生效** | 字段路径表达式（如 `"profile.release.strip"`）映射为锁定值；member / preset / include 的对应字段若与之冲突 → WS010 |
| **默认 policy 锁定**（D5 决策） | `build.out_dir` / `build.cache_dir` 默认锁定到 `[workspace.build]` 的值，无需用户在 `[policy]` 显式声明 |
| **`[workspace.build]` 集中产物** | `dist/<member>.zpkg` / `.cache/<member>/<file>.zbc` 在 workspace 根；workspace 模式下 member 的 `[build]` 被忽略 + warning |
| **profile 维度产物路径** | `out_dir = "dist/${profile}"` 通过 PathTemplateExpander（C1 已实施）展开为 `dist/release` 等 |
| **member `[build]` 行为变化** | workspace 模式：被忽略并报 `WS004 BuildSettingOverridden`（warning）；单工程模式：保留原行为 |
| **错误码 WS010 / WS011** | policy 冲突 / policy 字段路径不存在 |

## Scope（允许改动的文件）

### 新增（NEW）

| 文件路径 | 说明 |
|---------|------|
| `src/compiler/z42.Project/PolicyEnforcer.cs` | policy 字段路径解析、锁定值检查、与 member/preset 冲突检测 |
| `src/compiler/z42.Project/CentralizedBuildLayout.cs` | 计算 member 产物路径与 cache 路径（统一从 workspace 根派生） |
| `src/compiler/z42.Project/PolicyFieldPath.cs` | 字段路径表达式解析（如 `"profile.release.strip"` → tokens） |
| `src/compiler/z42.Tests/PolicyEnforcerTests.cs` | 默认 policy / 显式 policy / WS010 / WS011 |
| `src/compiler/z42.Tests/CentralizedBuildLayoutTests.cs` | 产物路径派生 / cache 路径 / profile 模板展开 |
| `src/compiler/z42.Tests/PolicyIntegrationTests.cs` | 端到端：含 policy 的样例 → 解析后产物路径与冲突报错 |
| `examples/workspace-with-policy/z42.workspace.toml` | 含 [workspace.build] + [policy] 的样例 |
| `examples/workspace-with-policy/libs/foo/foo.z42.toml` | 服从 policy 的 member |
| `examples/workspace-with-policy/libs/foo/src/Foo.z42` | 样例源 |
| `examples/workspace-with-policy/expected_resolved.json` | 解析结果 golden |

### 修改（MODIFY）

| 文件路径 | 说明 |
|---------|------|
| `src/compiler/z42.Project/ManifestLoader.cs` | 在 ManifestMerger 之后调用 PolicyEnforcer；产物路径走 CentralizedBuildLayout |
| `src/compiler/z42.Project/ResolvedManifest.cs` | `BuildConfig` 增加 `IsCentralized` / `EffectiveOutDir` / `EffectiveCacheDir`；`FieldOrigin.IsLocked` 在 PolicyLocked 来源时为 true |
| `src/compiler/z42.Project/ManifestErrors.cs` | 追加 WS010 / WS011；提升 WS004 为正式实现（C1 占位） |
| `src/compiler/z42.Project/WorkspaceManifest.cs` | `[policy]` 段从占位变为实际数据模型（字段路径 → 锁定值字典） |
| `src/compiler/z42.Compiler/PackageCompiler.cs` | 产物输出路径从 member-local 改为 `ResolvedManifest.EffectiveOutDir`；cache 路径同理 |
| `src/compiler/z42.Compiler/IncrementalCompiler.cs`（如存在） | cache 路径走 CentralizedBuildLayout |
| `docs/design/project.md` | L6.6 章节"policy 与集中产物"；修订 L3 段说明 workspace 模式下 [build] 行为差异 |
| `docs/design/compiler-architecture.md` | PolicyEnforcer / CentralizedBuildLayout 的设计原理 |
| `docs/design/error-codes.md` | 追加 WS010 / WS011；更新 WS004 描述 |

### 删除（DELETE）

无。

### 只读引用

| 文件路径 | 用途 |
|---------|------|
| `src/compiler/z42.Project/IncludeResolver.cs` | 理解 include 链（C2 已实施） |
| `src/compiler/z42.Project/ManifestMerger.cs` | 理解合并顺序（C2 已实施） |
| `src/compiler/z42.Project/PathTemplateExpander.cs` | 理解 `${profile}` 展开（C1 已实施） |
| `src/compiler/z42.Project/ZpkgWriter.cs` | 理解产物写入路径（C3 改其调用方而非 ZpkgWriter 本身） |

## Out of Scope

- **lockfile（z42.lock）**：与 semver 解析体系绑定，future
- **Member-level 覆盖 profile 的能力**：D6 已锁定不引入
- **Policy 字段函数 / 条件**：永不引入（与"声明式"哲学冲突）
- **跨 workspace 共享 policy**：每个 workspace 自包含
- **集中产物的并行编译调度**：由 C4 的 WorkspaceBuildOrchestrator 处理

## Open Questions

无。

## 决策记录摘要

| # | 决策 | 选择 |
|---|------|-----|
| D3.1 | Policy 字段路径表达式语法 | 点路径（`"profile.release.strip"` / `"build.out_dir"`） |
| D3.2 | 默认锁定哪些字段（D5） | `build.out_dir` / `build.cache_dir` |
| D3.3 | 集中产物路径模式 | `dist/<member>.zpkg`（一层），cache 用 `cache/<member>/<file>.zbc`（两层）|
| D3.4 | member `[build]` 在 workspace 模式 | 忽略 + WS004 warning（不直接 error，便于现有工程平滑迁移） |
| D3.5 | profile 派生产物 | 用模板 `${profile}` 表达，不引入新字段 |
| D3.6 | Policy 字段路径不存在如何处理 | WS011，启动时即报，避免运行时静默失效 |
