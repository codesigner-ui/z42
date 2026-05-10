# Spec: Include 机制（C2）

## ADDED Requirements

### Requirement: include 字段语法

#### Scenario: 字符串数组形式
- **WHEN** member `include = ["../presets/lib-defaults.toml"]`
- **THEN** 编译器将该路径加入 include 链解析

#### Scenario: 多元素按声明顺序生效
- **WHEN** `include = ["a.toml", "b.toml"]`
- **THEN** 先合并 `a.toml`、再合并 `b.toml`，b 覆盖 a；声明 include 的文件最终覆盖 a/b

#### Scenario: include 字段缺省
- **WHEN** member 未声明 `include` 字段
- **THEN** 等同 `include = []`（无任何 preset 拉入）

---

### Requirement: include 路径解析

#### Scenario: 相对于声明 include 的文件
- **WHEN** `apps/hello/hello.z42.toml` 含 `include = ["../../presets/exe.toml"]`
- **THEN** 实际解析路径为 `<workspace_root>/presets/exe.toml`

#### Scenario: 路径含模板变量
- **WHEN** `include = ["${workspace_dir}/presets/lib.toml"]`
- **THEN** 调用 `PathTemplateExpander` 先展开变量再解析路径

#### Scenario: 不允许绝对系统路径
- **WHEN** `include = ["/etc/preset.toml"]` 或 `include = ["C:\\preset.toml"]`
- **THEN** 报 `WS024 IncludePathNotAllowed`，提示仅允许相对路径或带变量的路径

#### Scenario: 不允许 URL
- **WHEN** `include = ["https://example.com/preset.toml"]`
- **THEN** 报 `WS024 IncludePathNotAllowed`，提示远端 include 永不支持

#### Scenario: 不允许 glob
- **WHEN** `include = ["presets/*.toml"]`
- **THEN** 报 `WS024 IncludePathNotAllowed`，提示 D7 决策

#### Scenario: 路径不存在
- **WHEN** include 指向的文件不存在
- **THEN** 报 `WS023 IncludePathNotFound`，列出引用文件 + 期望路径

---

### Requirement: 合并语义

#### Scenario: 标量字段后者覆盖
- **WHEN** preset A 写 `[project] kind = "lib"`，member 写 `[project] kind = "exe"`
- **THEN** 最终 `kind = "exe"`

#### Scenario: 表字段级合并
- **WHEN** preset A 写 `[project] license = "MIT"`，member 写 `[project] description = "..."`
- **THEN** 最终 `[project]` 同时含 `license` 和 `description`

#### Scenario: 数组整体覆盖
- **WHEN** preset A 写 `[sources] include = ["src/**/*.z42"]`，member 写 `[sources] include = ["src/**/*.z42", "extra/**/*.z42"]`
- **THEN** 最终 `[sources] include = ["src/**/*.z42", "extra/**/*.z42"]`（member 整体替换 preset 的数组，不连接）

#### Scenario: 自身覆盖 include 链
- **WHEN** preset 与 member 对同一字段都赋值
- **THEN** member 自身值优先（include 链作为默认）

#### Scenario: 同名字段在多个 preset 中
- **WHEN** include = [preset_a, preset_b]，两个 preset 都写 `[build] mode = ...`
- **THEN** 后写的 preset_b 覆盖 preset_a

---

### Requirement: include 嵌套

#### Scenario: preset 内可再 include
- **WHEN** preset_a 含 `include = ["./common.toml"]`
- **THEN** common.toml 先被合并入 preset_a，再 preset_a 整体被合并入 member

#### Scenario: 嵌套深度上限
- **WHEN** include 链深度超过 8 层（A→B→...→I）
- **THEN** 报 `WS022 IncludeTooDeep`，列出完整链路径

---

### Requirement: 循环 include 检测

#### Scenario: 直接环（A include A）
- **WHEN** preset_a 含 `include = ["./preset_a.toml"]`
- **THEN** 报 `WS020 CircularInclude`

#### Scenario: 间接环（A→B→A）
- **WHEN** preset_a include preset_b，preset_b include preset_a
- **THEN** 报 `WS020 CircularInclude`，列出完整环

#### Scenario: 菱形 include 不报错
- **WHEN** member include preset_a 与 preset_b，两者都 include preset_common
- **THEN** preset_common 仅合并一次，不报错（去重）

---

### Requirement: preset 文件段限制

#### Scenario: preset 含 `[workspace.*]`
- **WHEN** preset 文件含 `[workspace]` 或 `[workspace.project]` 等子段
- **THEN** 报 `WS021 ForbiddenSectionInPreset`，提示 workspace 段只能在 `z42.workspace.toml`

#### Scenario: preset 含 `[policy]`
- **WHEN** preset 含 `[policy]`
- **THEN** 报 `WS021`（治理一致性：策略只能从 workspace 根下发，不能由 preset 注入）

#### Scenario: preset 含 `[profile.*]`
- **WHEN** preset 含 `[profile.debug]` 或 `[profile.release]`
- **THEN** 报 `WS021`

#### Scenario: preset 含 `[project] name`
- **WHEN** preset 写 `[project] name = "..."`
- **THEN** 报 `WS021`，提示身份字段（`name` / `version` / `entry`）不可由 preset 提供

#### Scenario: preset 允许的字段
- **WHEN** preset 含 `[project] kind / license / description / authors`、`[sources]`、`[build]`、`[dependencies]`
- **THEN** 解析通过

---

### Requirement: include 与 workspace 共享继承的合并顺序

#### Scenario: workspace 共享 → include → member
- **WHEN** workspace 根有 `[workspace.project] license = "MIT"`，preset 有 `[project] license = "Apache-2.0"`，member 有 `[project] license.workspace = true`
- **THEN** 最终 license 取决于：
  1. workspace.project 提供 `MIT`
  2. preset 提供 `Apache-2.0`（覆盖默认）
  3. member 写 `license.workspace = true` → 显式回退到 workspace 共享值 `MIT`
  - 最终值：`MIT`

#### Scenario: include 链不能影响 workspace 段
- **WHEN** preset 含 `[workspace.project]`
- **THEN** 报 `WS021`（不允许）；preset 永远不能修改 workspace 共享配置

---

## MODIFIED Requirements

### Requirement: ResolvedManifest.Origins 来源类型

**Before**：C1 中 `OriginKind` 枚举为 `MemberDirect` / `WorkspaceProject` / `WorkspaceDependency`。

**After**：增加 `IncludePreset`，`FieldOrigin.FilePath` 字段在该来源下指向具体 preset 文件（含完整 include 链路径用于诊断）。

---

## 错误码索引（C2 新增）

| 码 | 含义 | 级别 |
|---|---|---|
| WS020 | include 循环（直接或间接） | error |
| WS021 | preset 含禁用段（`[workspace.*]` / `[policy]` / `[profile.*]` / `[project].name`） | error |
| WS022 | include 嵌套深度超过 8 层 | error |
| WS023 | include 路径不存在 | error |
| WS024 | include 路径不允许（绝对系统路径 / URL / glob） | error |

## Pipeline Steps

- [x] Manifest 解析层（接入 IncludeResolver + ManifestMerger）
- [ ] Lexer / Parser / TypeChecker / IR Codegen / VM —— 不动

## IR Mapping

无 IR 变更。
