# Spec: workspace 编译核心运行时（C4a）

## ADDED Requirements

### Requirement: workspace 根发现集成进 z42c

#### Scenario: 在 workspace 根运行
- **WHEN** CWD 是 workspace 根（含 z42.workspace.toml），运行 `z42c build`
- **THEN** 进入 workspace 模式
- **AND** 编译 `default-members` 或全部 members（无 default-members 时）

#### Scenario: 在 member 子目录运行
- **WHEN** CWD 是 `libs/foo/` 或更深层，运行 `z42c build`
- **THEN** 向上找到 workspace 根，进入 workspace 模式
- **AND** 编译当前 member（即 foo）及其依赖

#### Scenario: 不在 workspace 内
- **WHEN** CWD 无任何 `z42.workspace.toml` 祖先
- **THEN** 走现有单工程或单文件模式（行为不变）

#### Scenario: --no-workspace 强制单工程
- **WHEN** CWD 在 workspace 内 + `z42c build --no-workspace`
- **THEN** 跳过 workspace 发现，走单工程模式

#### Scenario: 显式 path 参数
- **WHEN** `z42c build path/to/some.z42.toml`
- **THEN** 单工程模式编译指定文件，不走 workspace 发现

---

### Requirement: BuildCommand 成员选择

#### Scenario: --workspace 编译全部
- **WHEN** `z42c build --workspace`
- **THEN** 编译所有 members（按拓扑顺序）

#### Scenario: -p 指定单 member
- **WHEN** `z42c build -p hello`
- **THEN** 编译 hello + 其传递依赖

#### Scenario: -p 多次指定
- **WHEN** `z42c build -p foo -p bar`
- **THEN** 编译 foo / bar 及其依赖闭包

#### Scenario: --exclude
- **WHEN** `z42c build --workspace --exclude experiments`
- **THEN** 编译除 experiments 外所有 members

#### Scenario: -p 和 --exclude 冲突 → WS002
- **WHEN** `z42c build -p foo --exclude foo`
- **THEN** 报 `WS002 ExcludedMemberSelected`

#### Scenario: -p 不存在的 member
- **WHEN** `z42c build -p nonexistent`
- **THEN** 报错 `MemberNotFound`，列出已知 members

---

### Requirement: 拓扑编译顺序

#### Scenario: 正常依赖链
- **WHEN** `core` ← `utils` ← `hello`，运行 `z42c build --workspace`
- **THEN** 编译顺序：core → utils → hello（串行）

#### Scenario: 循环依赖 → WS006
- **WHEN** a 依赖 b，b 依赖 a
- **THEN** 报 `WS006 CircularDependency`，列出完整环

#### Scenario: 上游失败阻塞下游
- **WHEN** core 编译失败
- **THEN** utils / hello 标记为 blocked（不编译）
- **AND** 命令以非 0 退出码结束

#### Scenario: 同名 member 冲突 → WS001
- **WHEN** workspace 含两个 members 都声明 `[project] name = "foo"`
- **THEN** 报 `WS001 DuplicateMemberName`

---

### Requirement: PackageCompiler 接受 ResolvedManifest

#### Scenario: workspace 编译走 ResolvedManifest 入口
- **WHEN** orchestrator 调用 `PackageCompiler.CompileFromResolved(rm, ctx)`
- **THEN** 编译器使用 `rm.EffectiveProductPath` 写产物（C3 已落实）
- **AND** 现有 `PackageCompiler.Compile(ProjectManifest)` 单工程入口行为不变

---

### Requirement: check 模式

#### Scenario: --check-only 不写产物
- **WHEN** `z42c check` 或 `z42c build --check-only`
- **THEN** 仅类型检查 + IR 生成，不写 .zpkg 产物

---

## MODIFIED Requirements

### Requirement: z42c CLI 入口路径解析

**Before**：[Program.cs](src/compiler/z42.Driver/Program.cs) 直接处理 path（路径或单文件）。

**After**：先调用 `ManifestLoader.DiscoverWorkspaceRoot(CWD)`，命中 → workspace 模式分派给 BuildCommand；未命中或显式 `--no-workspace` → 现有单工程 / 单文件模式。

---

## 错误码（C4a 新增）

| 码 | 含义 | 级别 |
|---|---|---|
| WS001 | 重复 member name | error |
| WS002 | -p 与 --exclude 冲突 | error |
| WS006 | Member 间依赖循环 | error |

## Pipeline Steps

- [x] CLI 入口
- [x] 编译器 orchestrator + 拓扑
- [ ] Lexer / Parser / TypeChecker / IR Codegen / VM —— 不动

## IR Mapping

无 IR 变更。
