# Spec: workspace 查询命令 + CliOutputFormatter（C4b）

## ADDED Requirements

### Requirement: z42c info

#### Scenario: 无参数时打印概览
- **WHEN** workspace 内运行 `z42c info`
- **THEN** 输出含：workspace 根路径 / members 列表（带 kind / 路径） / 默认 profile / default-members

#### Scenario: --resolved -p <name>
- **WHEN** `z42c info --resolved -p hello`
- **THEN** 输出 hello 最终生效配置（含所有字段值）
- **AND** 每字段标注来源（MemberDirect / WorkspaceProject / WorkspaceDependency / IncludePreset / PolicyLocked）
- **AND** PolicyLocked 字段后加 🔒 标记
- **AND** Origins.IncludeChain 不为空时显示展开链

#### Scenario: --include-graph -p <name>
- **WHEN** member 含 include 链
- **THEN** ASCII 树显示 preset 展开关系（含路径、深度）

#### Scenario: 不在 workspace 时
- **WHEN** 单工程模式 `z42c info`
- **THEN** 输出该单 manifest 概览（无 members 列表）

---

### Requirement: z42c metadata --format json

#### Scenario: JSON 顶层字段
- **WHEN** workspace 内运行 `z42c metadata --format json`
- **THEN** 输出 JSON 对象含：
  - `"schema_version": "1"`
  - `"workspace_root": "<absolute path>"`
  - `"profile": "debug"`
  - `"members": [...]`：每项含 `name` / `path` / `kind` / `entry?` / `version` / `effective_product_path` / `dependencies[]`
  - `"dependency_graph"`: `[{ "from": "hello", "to": "utils" }, ...]`

#### Scenario: schema_version 字段稳定
- **WHEN** 任意调用
- **THEN** 顶层始终含 `"schema_version": "1"`，任何 breaking 变更必须 bump

#### Scenario: 单工程模式
- **WHEN** 单工程 `z42c metadata --format json`
- **THEN** 输出含 `members` 单元素，`dependency_graph` 为空数组

---

### Requirement: z42c tree

#### Scenario: 基本依赖树
- **WHEN** workspace 内 `z42c tree`，依赖图为 hello → utils → core
- **THEN** ASCII 树形输出（如 `hello` 顶层，`utils` 缩进，`core` 再缩进）

#### Scenario: 多顶层
- **WHEN** workspace 含多个无被引用的顶层 member（apps/* 各自独立）
- **THEN** 每个顶层独立成一棵树

#### Scenario: 循环依赖（不应出现，由 WS006 保护）
- **WHEN** 强制走环（仅在 WS006 错误前已构造图）
- **THEN** 显示 `<cycle>` 标记，避免无限输出

---

### Requirement: z42c lint-manifest

#### Scenario: 全 workspace 静态校验
- **WHEN** workspace 内 `z42c lint-manifest`
- **THEN** 调用 ManifestLoader.LoadWorkspace 完成全部解析（不写产物、不编译源码）
- **AND** 报告所有错误（WS003-039）+ warnings（WS007）

#### Scenario: 全部通过
- **WHEN** workspace 无任何错误
- **THEN** 输出 "manifest OK" + members 数 + warnings 数

---

### Requirement: CliOutputFormatter

#### Scenario: WS010 友好输出
- **WHEN** ManifestException with WS010 抛出
- **THEN** 格式化输出：
  ```
  error[WS010]: PolicyViolation
    --> libs/foo/foo.z42.toml
    field: build.out_dir
    member sets:    custom_dist
    workspace lock: dist (at z42.workspace.toml)
    help: remove this line or align value with workspace policy
  ```

#### Scenario: --no-pretty 输出原始 message
- **WHEN** `z42c build --no-pretty`
- **THEN** 错误以原始 message 输出（无颜色、无对齐），CI 友好

#### Scenario: 编译错误（Z01xx-Z05xx）保持现状
- **WHEN** 抛出 Z02xx parser 错误
- **THEN** 走现有错误输出路径，不被 CliOutputFormatter 改写

---

## MODIFIED Requirements

### Requirement: z42c subcommand 路由

**Before**：C4a 之后路由含 build / check（隐含 build 入口）。

**After**：增加 info / metadata / tree / lint-manifest 各自路由到 Commands/<Name>Command.cs。

---

## Pipeline Steps

- [x] CLI 命令矩阵
- [x] 输出格式化层
- [ ] Lexer / Parser / TypeChecker / IR Codegen / VM —— 不动

## IR Mapping

无 IR 变更。

## 错误码

C4b 不新增错误码；仅消费 C1-C4a 已有错误。
