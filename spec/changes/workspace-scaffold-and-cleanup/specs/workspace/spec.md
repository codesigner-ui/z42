# Spec: workspace 脚手架 + 清理 + WS004 移除（C4c）

## ADDED Requirements

### Requirement: z42c clean

#### Scenario: 集中清理
- **WHEN** workspace 内 `z42c clean`
- **THEN** 删除 `<workspace_root>/<out_dir>` 整棵树（含所有 .zpkg）
- **AND** 删除 `<workspace_root>/<cache_dir>` 整棵树

#### Scenario: per-member 清理
- **WHEN** `z42c clean -p foo`
- **THEN** 删除 `<out_dir>/foo.zpkg`
- **AND** 删除 `<cache_dir>/foo/` 子目录（其他 member cache 不动）

#### Scenario: 单工程模式
- **WHEN** 单工程 `z42c clean`
- **THEN** 删除 member-local `dist/` + `.cache/`

#### Scenario: 产物不存在不报错
- **WHEN** `z42c clean` 但 dist/ 不存在
- **THEN** 静默成功（exit 0），输出 "nothing to clean"

---

### Requirement: z42c new --workspace

#### Scenario: 生成空 workspace
- **WHEN** `z42c new --workspace mymonorepo`
- **THEN** 创建目录 `mymonorepo/` + 子结构：
  - `z42.workspace.toml`（含 [workspace]/[workspace.project]/[workspace.build] 默认）
  - `.gitignore`（含 `dist/` / `.cache/`）
  - `presets/lib-defaults.toml` + `presets/exe-defaults.toml`
  - `libs/` 空目录
  - `apps/` 空目录

#### Scenario: 已存在目录
- **WHEN** 目标目录已存在且非空
- **THEN** 报错 `DirectoryNotEmpty`，不覆盖

---

### Requirement: z42c new -p

#### Scenario: workspace 内新增 lib member
- **WHEN** workspace 内 `z42c new -p foo --kind lib`
- **THEN** 创建 `libs/foo/foo.z42.toml`（含 `[project] name="foo" kind="lib" version.workspace=true`）
- **AND** 创建 `libs/foo/src/Foo.z42`（占位 namespace）

#### Scenario: workspace 内新增 exe member
- **WHEN** `z42c new -p hello --kind exe --entry Hello.main`
- **THEN** 创建 `apps/hello/hello.z42.toml`（含 `entry = "Hello.main"`）
- **AND** 创建 `apps/hello/src/Main.z42`

#### Scenario: 不在 workspace
- **WHEN** 单工程模式 `z42c new -p ...`
- **THEN** 报错 `NotInWorkspace`，提示先 `z42c new --workspace` 或 `z42c init`

---

### Requirement: z42c init

#### Scenario: 单 manifest 升级为 workspace
- **WHEN** 当前目录有 `<name>.z42.toml`，运行 `z42c init`
- **THEN** 在父目录创建 `z42.workspace.toml`（含 `members = ["<name>"]`）
- **AND** 不修改原 manifest

#### Scenario: 已是 workspace
- **WHEN** 目录已含 `z42.workspace.toml`
- **THEN** 报错 `AlreadyInWorkspace`，不重复创建

---

### Requirement: z42c fmt

#### Scenario: 格式化单个 manifest
- **WHEN** workspace 内 `z42c fmt`
- **THEN** 格式化所有 `*.z42.toml`（含 z42.workspace.toml + 各 member）：
  - 字段按规范顺序（[project] / [workspace] / [sources] / [build] / [dependencies] / [profile.*] / [policy]）
  - 段内字段按字母序
  - 缩进 4 空格

#### Scenario: 保留注释
- **WHEN** 原 manifest 有注释
- **THEN** 注释保留位置（用 Tomlyn round-trip）

#### Scenario: 单工程模式
- **WHEN** 单工程 `z42c fmt`
- **THEN** 仅格式化当前 manifest

---

## MODIFIED Requirements

### Requirement: WS004 完全移除

**Before**：C3 阶段 `WS004` 在 ManifestErrors.cs 中标 `[Obsolete]`，相关引用归并入 WS010；docs/design/error-codes.md 中作为占位提及。

**After**：C4c 阶段：
- ManifestErrors.cs 中删除 `WS004` 常量声明
- docs/design/error-codes.md 删除 WS004 占位条目
- `grep -r "WS004"` 在 src/ docs/ 无残留（除 spec/archive 历史档外）

### Requirement: z42c subcommand 路由

**Before**：C4a 加 build/check；C4b 加 info/metadata/tree/lint-manifest。

**After**：C4c 完成完整 12 个 subcommand：build / check / info / metadata / tree / lint-manifest / clean / new / init / fmt （仍保留 run / test 占位 future）。

---

## Pipeline Steps

- [x] CLI 命令矩阵（最终完整）
- [x] WS004 移除
- [ ] Lexer / Parser / TypeChecker / IR Codegen / VM —— 不动

## IR Mapping

无 IR 变更。

## 错误码

C4c 不新增 WSxxx 错误码；仅清除 WS004。
