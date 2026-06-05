# Proposal: Restructure build output directories — `output_dir` / `cache_dir` / `dist_dir`

## Why

`[build].out_dir` 当前是 z42 项目编译产物路径的唯一可调按钮，语义模糊：

- "out" 既可指中间产物（`.zbc` / 增量元数据）也可指最终产物（`.zpkg` / exe），现实里它专门指最终产物 —— 名字与角色不匹配。
- 项目层级没有 `cache_dir` 字段。中间产物的位置目前由 PackageCompiler 写死在 `${out_dir}/.cache/` 或散落到 IR pipeline 临时目录，用户不能搬走（例如想把 cache 放到 tmpfs / RAM disk）。
- Workspace 层级有 `cache_dir` 但与项目层级不对齐，造成 schema 不一致。
- 单文件编译 `z42c build foo.z42` 无 `.z42.toml`，产物默认落在 cwd —— 在不同目录下跑同一文件输出位置漂移。

### What Changes

1. 项目 `[build]` 段引入三个字段：
   - **`output_dir`**：顶层输出根目录；默认 = `.z42.toml` 所在目录（单文件编译 = 源文件目录）
   - **`cache_dir`**：中间产物目录（`.zbc` / 索引 / 增量元数据）；默认 = `${output_dir}/.cache`
   - **`dist_dir`**：最终分发产物目录（`.zpkg` / exe）；默认 = `${output_dir}/dist`
2. **重命名** `[build].out_dir` → `[build].dist_dir`（pre-1.0 直接换名，不留 alias）。
3. Workspace `[build]` 同 schema —— member 继承 workspace 默认值，可单独覆盖任一字段。
4. `[policy]` 受管字段集同步：删 `build.out_dir`，加 `build.dist_dir` / `build.output_dir` / `build.cache_dir`。
5. 单文件编译 `z42c build foo.z42`：`output_dir = dirname(foo.z42)`，产物落在源文件旁的 `.cache/` 和 `dist/`。
6. **`[sources]` 段不动**（已支持 `include` glob + `exclude` glob）—— iteration 2 收敛为文档化（design doc 加示例 + 迁移说明）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Project/ProjectManifest.cs` | MODIFY | `BuildSection`：删 `OutDir`，加 `OutputDir` / `CacheDir` / `DistDir`；`KnownBuildKeys`：删 `out_dir`，加新三个；`ParseBuild` 解析 + 默认值占位（`null` 表示未设；effective 路径在 ResolvedManifest / CentralizedBuildLayout 计算） |
| `src/compiler/z42.Project/WorkspaceManifest.cs` | MODIFY | `WorkspaceBuildSection`：同 ProjectManifest，三字段对齐 |
| `src/compiler/z42.Project/ResolvedManifest.cs` | MODIFY | `Resolved*Build` 暴露 `OutputDir` / `CacheDir` / `DistDir` 三个 effective 路径（默认值在此 computed） |
| `src/compiler/z42.Project/CentralizedBuildLayout.cs` | MODIFY | `EffectiveOutDir` → `EffectiveDistDir`；加 `EffectiveOutputDir` / `EffectiveCacheDir`；`${output_dir}` 模板变量加入插值集合 |
| `src/compiler/z42.Project/PolicyFieldPath.cs` | MODIFY | `KnownFields`：删 `build.out_dir`，加 `build.dist_dir` / `build.output_dir` / `build.cache_dir`；GetValue switch 同步 |
| `src/compiler/z42.Driver/CompileOptions.cs` 或同义 | MODIFY | 单文件编译路径：`output_dir = dirname(sourcePath)` 写入解析后的 BuildSection |
| `src/compiler/z42.Driver/Driver.cs`（或 Compile 入口）| MODIFY | 把 `out_dir` 引用全部替换为 effective `dist_dir` / `cache_dir`（取决于产物类型） |
| `src/compiler/z42.Compiler/PackageCompiler.cs` | MODIFY | 写入 `.zpkg` / `.zbc` 时用 effective `dist_dir` / `cache_dir`；现有 `.cache` 子目录硬编码改成读 `cache_dir` |
| `src/compiler/z42.Tests/ProjectManifestTests.cs` | MODIFY | 现有 `out_dir` 测试改 `dist_dir`；加 `output_dir` / `cache_dir` 默认计算 + 显式覆盖测试 |
| `src/compiler/z42.Tests/WorkspaceManifestTests.cs` | MODIFY | 同上 workspace 层级 |
| `src/compiler/z42.Tests/CentralizedBuildLayoutTests.cs` | MODIFY | `EffectiveOutDir` 引用全替；加 `EffectiveOutputDir` / `EffectiveCacheDir` 断言 |
| `src/compiler/z42.Tests/PolicyFieldPathTests.cs` | MODIFY | 更新已知 field 集 |
| `examples/**/*.z42.toml`（仓库内现存）| MODIFY | `out_dir = "..."` → `dist_dir = "..."` 或删除（命中默认） |
| `src/libraries/**/z42.*.z42.toml`（22 个 stdlib）| MODIFY | 同上 |
| `src/toolchain/**/*.z42.toml`（launcher / xtask）| MODIFY | 同上 |
| `scripts/xtask.z42.toml` | MODIFY | 同上 |
| `docs/design/compiler/project.md` | MODIFY | schema 文档：三字段说明 + 默认值表 + 迁移说明 + sources glob 现状示例（iteration 2 文档化部分） |
| `docs/design/compiler/workspace.md` | MODIFY | workspace 同步 |
| `docs/spec/archive/2026-06-06-restructure-build-output-dirs/` | NEW | 归档目录（spec 完成后 mv） |

**只读引用**：
- `src/compiler/z42.Project/CentralizedBuildLayout.cs`（理解模板变量与 `${workspace_dir}` 现有插值）
- `src/compiler/z42.Compiler/PackageCompiler.cs`（理解 `.zpkg` / `.zbc` 写入路径决策）
- `docs/design/compiler/project.md`（理解 schema 现有结构）

## Out of Scope

- `[sources]` 段任何 schema 变更（已支持 include glob + exclude；iteration 2 仅文档化，不动代码）。
- 模板变量扩充（`${profile}` / `${member_name}` / `${workspace_dir}` 保留现状；不新增）。
- 单文件编译时引入隐式 `.z42.toml`（仍是 no-manifest 路径，`output_dir` 默认值在 Driver 算）。
- 引入第三个/第四个 output dir（如分离 debug-info / coverage）—— 三件套就够当前所有用例。
- pre-1.0 deprecation / alias —— 直接 rename，与 [philosophy.md "不为旧版本提供兼容"](../../../.claude/rules/philosophy.md) 对齐。

## Open Questions

- [ ] 单文件编译时若用户传 `--output-dir` CLI 覆盖，优先级是否高于源文件目录？（推荐：是 —— CLI > manifest > 默认）
- [ ] `cache_dir` 是否需要 per-profile 拆分（debug vs release 共用还是分开）？（推荐：共用 —— 增量元数据本就 profile-aware）
