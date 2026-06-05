# Spec: Project manifest — build output directories

## MODIFIED Requirements

### Requirement: `[build]` 段 schema 重构为 output_dir / cache_dir / dist_dir 三件套

**Before:**
- `[build].out_dir`（string, 默认 `"dist"`）—— 唯一可调输出目录字段
- `[build].cache_dir` 不存在（仅 workspace 层有）

**After:**
- `[build].output_dir`（string, optional, 默认见 Scenario） —— 顶层输出根
- `[build].cache_dir`（string, optional, 默认 `${output_dir}/.cache`）—— 中间产物
- `[build].dist_dir`（string, optional, 默认 `${output_dir}/dist`）—— 最终分发产物
- 老字段 `out_dir` **不再识别** —— 触发 WS008 unknown-key warning，Levenshtein 建议 `dist_dir`

#### Scenario: 三字段全部 unset → 默认值符合约定

- **WHEN** `.z42.toml` 的 `[build]` 段未设 `output_dir` / `cache_dir` / `dist_dir`
- **THEN** effective `output_dir` = `dirname(.z42.toml)`；effective `cache_dir` = `${output_dir}/.cache`；effective `dist_dir` = `${output_dir}/dist`

#### Scenario: 仅设 output_dir → cache 和 dist 跟随

- **WHEN** `[build].output_dir = "/build/myproj"`，`cache_dir` / `dist_dir` 未设
- **THEN** effective `cache_dir` = `/build/myproj/.cache`；effective `dist_dir` = `/build/myproj/dist`

#### Scenario: 单独覆盖 cache_dir（如放 tmpfs）

- **WHEN** `[build].output_dir = "/build/myproj"` + `[build].cache_dir = "/dev/shm/myproj-cache"`，`dist_dir` 未设
- **THEN** effective `cache_dir` = `/dev/shm/myproj-cache`；effective `dist_dir` = `/build/myproj/dist`（仍跟随 output_dir）

#### Scenario: 三字段全部显式覆盖

- **WHEN** `[build].output_dir = "/a"` + `[build].cache_dir = "/b"` + `[build].dist_dir = "/c"`
- **THEN** effective 路径完全等于各字段显式值（output_dir 仅作为 "未设时跟随" 的基准；显式覆盖时三者解耦）

#### Scenario: 老 out_dir 字段触发 WS008 warning

- **WHEN** `.z42.toml` 含 `[build].out_dir = "dist"`
- **THEN** manifest 解析出 WS008 unknown-key warning，message 包含 `out_dir` + Levenshtein 建议 `dist_dir`；effective `dist_dir` 走默认（`${output_dir}/dist`）—— 老字段不被采用

### Requirement: Workspace `[build]` 段同 schema + member 继承

#### Scenario: workspace 设 output_dir / member 全 unset

- **WHEN** `z42-workspace.toml` 的 `[build].output_dir = "/build/ws"`，某 member `member-a/z42.toml` 的 `[build]` 三字段全 unset
- **THEN** member-a effective：`output_dir` = `/build/ws`（继承）；`cache_dir` = `/build/ws/.cache`；`dist_dir` = `/build/ws/dist`

#### Scenario: workspace 设 output_dir / member 覆盖 cache_dir

- **WHEN** workspace `[build].output_dir = "/build/ws"`，member `[build].cache_dir = "/dev/shm/a"`
- **THEN** member effective：`output_dir` = `/build/ws`（继承）；`cache_dir` = `/dev/shm/a`（覆盖）；`dist_dir` = `/build/ws/dist`（跟随 workspace output_dir）

#### Scenario: workspace 全 unset / member 全 unset

- **WHEN** workspace `[build]` 和 member `[build]` 均未设三字段
- **THEN** member effective：`output_dir` = workspace_root；`cache_dir` = `${workspace_root}/.cache`；`dist_dir` = `${workspace_root}/dist`

### Requirement: 单文件编译 `output_dir` 默认 = 源文件所在目录

#### Scenario: `z42c build foo.z42` 无 manifest 无 CLI override

- **WHEN** 用户在 `/home/u/work/` 跑 `z42c build /home/u/src/foo.z42`
- **THEN** effective `output_dir` = `/home/u/src`；产物落 `/home/u/src/dist/foo.zpkg`（或 `.zbc`）；cache 落 `/home/u/src/.cache/`
- **AND** 在不同 cwd 跑同一命令产物位置不变（不受 cwd 影响）

#### Scenario: `z42c build foo.z42 --output-dir /tmp/build`

- **WHEN** CLI 传 `--output-dir`
- **THEN** effective `output_dir` = `/tmp/build`（CLI > manifest > 默认）

### Requirement: `[policy]` 受管字段集同步

#### Scenario: 老 `build.out_dir` 不再是受管字段

- **WHEN** policy 文件引用 `build.out_dir`
- **THEN** PolicyFieldPath 抛 "unknown managed field" 错误

#### Scenario: 新三字段可被 policy 受管

- **WHEN** policy 文件引用 `build.dist_dir` / `build.output_dir` / `build.cache_dir`
- **THEN** PolicyFieldPath 正确解析 + GetValue 返回 manifest 中对应字段的 raw 值（unset = null）

### Requirement: CentralizedBuildLayout 暴露 effective 三路径

#### Scenario: ResolvedBuild 暴露 EffectiveOutputDir / EffectiveCacheDir / EffectiveDistDir

- **WHEN** 调用 `CentralizedBuildLayout.Compute(workspace, member, profile)`
- **THEN** 返回结构含 `EffectiveOutputDir` / `EffectiveCacheDir` / `EffectiveDistDir` 三个 absolute 路径
- **AND** 旧 `EffectiveOutDir` 字段不再存在（rename 到 `EffectiveDistDir`）

#### Scenario: `${output_dir}` 模板变量在 cache_dir / dist_dir 字面值里插值

- **WHEN** `[build].cache_dir = "${output_dir}/intermediate"`
- **THEN** effective `cache_dir` = effective `output_dir` + `/intermediate`（模板变量在 effective 计算时先插值）

## ADDED Requirements

### Requirement: `[sources]` 段 glob + exclude 文档化（无代码改动）

#### Scenario: design doc 含 include glob 示例

- **WHEN** 用户读 `docs/design/compiler/project.md`
- **THEN** 文档展示 `[sources].include` 至少 3 个示例（顶层 src/ / 多目录 / 单文件覆盖）+ `[sources].exclude` 用法说明 + `[[exe]].src` 覆盖关系
- **AND** 文档明确说明：未引入 negation pattern（`!path`）；默认 exclude 仍为空（保持现行为）

## Pipeline Steps

变更影响的 pipeline 阶段（按顺序）：
- [x] Lexer：无影响
- [x] Parser / AST：无影响
- [x] TypeChecker：无影响
- [x] IR Codegen：无影响
- [x] VM interp：无影响
- [x] **Project / Manifest parsing**（核心变更点）
- [x] **Driver / PackageCompiler**（消费 effective 三路径）
