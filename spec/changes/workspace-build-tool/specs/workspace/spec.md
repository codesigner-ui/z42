# Spec: z42c workspace 构建工具链（C4）

## ADDED Requirements

### Requirement: workspace 根发现（CWD 无关）

#### Scenario: 在 workspace 根运行
- **WHEN** CWD 为 workspace 根，运行 `z42c build`
- **THEN** 命中根 `z42.workspace.toml`，进入 workspace 模式
- **AND** 编译 `default-members`（如声明）或全部 members（如未声明）

#### Scenario: 在 member 子目录运行
- **WHEN** CWD 为 `libs/foo/`，运行 `z42c build`
- **THEN** 向上找到 workspace 根，进入 workspace 模式
- **AND** 等价于 `z42c build -p foo`（仅编译当前 member 与其依赖）

#### Scenario: 在 member 子目录的更深层运行
- **WHEN** CWD 为 `libs/foo/src/`，运行 `z42c build`
- **THEN** 同 member 子目录场景

#### Scenario: 不在任何 workspace 内
- **WHEN** CWD 无任何 `z42.workspace.toml` 祖先
- **AND** CWD 含 `<name>.z42.toml`
- **THEN** 单工程模式编译该 member

#### Scenario: 显式禁用 workspace
- **WHEN** `z42c build --no-workspace`
- **THEN** 强制单工程模式，即使存在 workspace 根

#### Scenario: 显式指定 manifest
- **WHEN** `z42c build path/to/some.z42.toml`
- **THEN** 单工程模式编译指定文件，不走 workspace 发现

---

### Requirement: 构建命令成员选择

#### Scenario: --workspace 编译全部
- **WHEN** `z42c build --workspace`
- **THEN** 编译 workspace 所有 members，按拓扑顺序

#### Scenario: -p 指定单个 member
- **WHEN** `z42c build -p hello`
- **THEN** 编译 hello 与其依赖（不编译姐妹 member）

#### Scenario: -p 多次指定
- **WHEN** `z42c build -p foo -p bar`
- **THEN** 编译 foo / bar 及其各自依赖

#### Scenario: --exclude 排除
- **WHEN** `z42c build --workspace --exclude experiments`
- **THEN** 编译除 `experiments` 外的所有 members

#### Scenario: 默认子集
- **WHEN** workspace 含 `default-members = ["apps/hello"]`，运行 `z42c build`（无 -p / 无 --workspace）
- **THEN** 仅编译 `apps/hello` 与其依赖

#### Scenario: -p 不存在
- **WHEN** `z42c build -p nonexistent`
- **THEN** 报错 `MemberNotFound`，列出 workspace 已知 members

#### Scenario: -p 与 --exclude 同时指定且互相矛盾
- **WHEN** `z42c build -p foo --exclude foo`
- **THEN** 报 `WS002 ExcludedMemberSelected`，列出冲突 member

---

### Requirement: 拓扑编译顺序

#### Scenario: 正常依赖链
- **WHEN** `core` ← `utils` ← `hello`，运行 `z42c build --workspace`
- **THEN** 编译顺序：`core` → `utils` → `hello`
- **AND** 每个 member 编译完成后其产物可供下游使用

#### Scenario: 同 level 并行
- **WHEN** `core` 编译完成，`utils-a` 与 `utils-b` 都依赖 `core` 但互不依赖
- **THEN** `utils-a` 和 `utils-b` 并行编译（受 `--jobs N` 限制）

#### Scenario: 循环依赖
- **WHEN** `a` 依赖 `b`，`b` 依赖 `a`
- **THEN** 报 `WS006 CircularDependency`，列出完整环路径

#### Scenario: 依赖未在 workspace 中
- **WHEN** member `[dependencies]` 引用了不在 workspace 也不在 `[workspace.dependencies]` 的依赖
- **THEN** 报 `DependencyNotFound`（标准 z42 错误，非 WS00x）

---

### Requirement: 失败传播与 fail-fast

#### Scenario: 上游失败阻塞下游
- **WHEN** `core` 编译失败
- **THEN** `utils` / `hello` 跳过，标记为 blocked
- **AND** 与 `core` 同 level 的姐妹 member 仍并行编译完

#### Scenario: --fail-fast 立即终止
- **WHEN** `--fail-fast`，`core` 失败
- **THEN** 立即终止所有正在编译的 task；不再编译姐妹 member

#### Scenario: --jobs 控制并行度
- **WHEN** `--jobs 1`
- **THEN** 串行编译，不并行

---

### Requirement: 增量复用

#### Scenario: 源文件未变 → 跳过
- **WHEN** member `foo` 上次产物已存在，所有 `.z42` 文件 SHA-256 与 zpkg 中 source_hash 一致
- **THEN** 跳过编译，直接复用既有产物
- **AND** CLI 输出 `foo (cached)`

#### Scenario: 源文件变 → 重编
- **WHEN** 任一 `.z42` 文件 hash 变化
- **THEN** 该 member 重编译，cache 中变化文件的 zbc 更新

#### Scenario: manifest 变 → 全量重编
- **WHEN** member 的 `<name>.z42.toml` 或其 include 链中任一文件变化
- **THEN** 该 member 全量重编（保险起见，因为继承字段可能影响 codegen）

#### Scenario: 上游产物变 → 下游重链接
- **WHEN** `core` 重编（产物 hash 变化）
- **THEN** `utils` / `hello` 即使源文件未变也必须重链接

#### Scenario: --no-incremental 强制全量
- **WHEN** `--no-incremental`
- **THEN** 全部 member 重编，忽略所有缓存

---

### Requirement: 查询命令

#### Scenario: z42c info（无参数）
- **WHEN** 在 workspace 内运行 `z42c info`
- **THEN** 输出：workspace 根路径 / members 列表 / 当前 profile / 默认编译目标

#### Scenario: z42c info --resolved -p hello
- **WHEN** workspace 内运行
- **THEN** 输出 hello 的最终配置树
- **AND** 每字段标注来源（MemberDirect / WorkspaceProject / WorkspaceDependency / IncludePreset / PolicyLocked）
- **AND** PolicyLocked 字段后加 🔒 标记

#### Scenario: z42c info --include-graph -p hello
- **WHEN** hello 含 include 链
- **THEN** ASCII 树显示 include 展开关系，含路径与深度

#### Scenario: z42c metadata --format json
- **WHEN** 运行
- **THEN** 输出 JSON 含：
  - `schema_version: 1`
  - `workspace_root`
  - `members[]`：每项含 `name` / `path` / `kind` / `dependencies` / `effective_product_path`
  - `dependency_graph`：member 间依赖边数组
- **AND** 输出 stable，便于 IDE / 工具消费

#### Scenario: z42c tree
- **WHEN** 运行
- **THEN** ASCII 树显示 member 间依赖关系

#### Scenario: z42c lint-manifest
- **WHEN** 运行
- **THEN** 全 workspace 静态校验：循环 include / orphan member / 不存在的依赖 / policy 字段路径错误等
- **AND** 不实际编译

---

### Requirement: 清理命令

#### Scenario: z42c clean
- **WHEN** 在 workspace 内运行
- **THEN** 删除 `<workspace_root>/<out_dir>` 与 `<workspace_root>/<cache_dir>` 整棵树

#### Scenario: z42c clean -p foo
- **WHEN** 运行
- **THEN** 仅删除 foo 的产物（`<out_dir>/foo.zpkg`）与 cache 子目录（`<cache_dir>/foo/`）

#### Scenario: 单工程模式 clean
- **WHEN** 单工程模式 `z42c clean`
- **THEN** 删除 member-local `dist/` + `.cache/`

---

### Requirement: 脚手架命令

#### Scenario: z42c new --workspace mymonorepo
- **WHEN** 运行
- **THEN** 创建目录 `mymonorepo/`，含：
  - `z42.workspace.toml`（含 default `[workspace.project]` / `[workspace.build]`）
  - `libs/` / `apps/` 空目录
  - `.gitignore`（含 `dist/` / `.cache/`）
  - `presets/`（含示例 lib-defaults.toml / exe-defaults.toml）

#### Scenario: z42c new -p foo --kind lib
- **WHEN** 在 workspace 内运行
- **THEN** 创建 `libs/foo/foo.z42.toml` + `libs/foo/src/Foo.z42`
- **AND** 自动加入 workspace `members`（如 glob 已覆盖则不需要修改根）

#### Scenario: z42c init
- **WHEN** 在含单 manifest 的目录运行
- **THEN** 升级为 workspace：创建 `z42.workspace.toml`，将原 manifest 包装为单一 member

#### Scenario: z42c fmt
- **WHEN** 运行
- **THEN** 格式化所有 `*.z42.toml`：字段按规范顺序、统一缩进、对齐表

---

### Requirement: 错误码 WS001-007 集成

#### Scenario: WS001 重复 member name
- **WHEN** 两个 members 都声明 `name = "foo"`
- **THEN** workspace 加载时报 WS001，列出冲突文件

#### Scenario: WS002 -p 与 --exclude 冲突
- 见 "构建命令成员选择" Requirement

#### Scenario: WS005 同目录两份 manifest
- 由 C1 manifest 层报告；C4 在 CLI 层友好化输出

#### Scenario: WS006 循环依赖
- 见 "拓扑编译顺序" Requirement

#### Scenario: WS007 orphan member
- **WHEN** 子目录有 `<name>.z42.toml` 但未被 members glob 命中
- **THEN** workspace 加载时输出 warning（不阻塞编译）
- **AND** CLI 输出明确提示"未被 workspace 包含；如需纳入请加入 members 或 exclude"

---

## MODIFIED Requirements

### Requirement: WS004 BuildSettingOverridden

**Before**：C1 占位为 warning，C3 标记 `[Obsolete]`。

**After**：C4 归档时**彻底删除**常量与文档引用；任何引用统一改 WS010。

### Requirement: z42c 命令入口

**Before**：[Program.cs](src/compiler/z42.Driver/Program.cs) 直接处理 build 与单文件模式。

**After**：subcommand 路由分派到 `Commands/` 目录下各 Command 类；单工程模式作为 `BuildCommand` 的 fallback 路径保留。

---

## 错误码索引（C4 新增 / 集成）

| 码 | 含义 | 级别 |
|---|---|---|
| WS001 | 重复 member name | error |
| WS002 | -p 与 --exclude 冲突 | error |
| WS005 | 同目录两份 manifest（C1 已定义，C4 集成 CLI 友好输出） | error |
| WS006 | Member 间依赖循环 | error |
| WS007 | Orphan member（C1 已定义，C4 集成 CLI 友好输出） | warning |
| WS004 | （删除）C3 标记废弃，C4 移除 | — |

## Pipeline Steps

- [x] CLI 命令矩阵
- [x] 编译器入口（PackageCompiler 增加 member 入口；orchestrator）
- [x] 文档同步
- [ ] Lexer / Parser / TypeChecker / IR Codegen / VM —— 不动

## IR Mapping

无 IR 变更。
