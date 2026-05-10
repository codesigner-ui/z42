# Spec: .zpkg Unified Package Format

## ADDED Requirements

---

### Requirement: .zpkg 文件格式

#### Scenario: indexed 模式输出
- **WHEN** `z42c build`（debug profile，`pack` 默认 false）
- **THEN** 产出 `dist/<name>.zpkg`，内容满足：
  - `mode = "indexed"`
  - `files[]` 非空，每项含 `source`、`bytecode`（`.cache/` 路径）、`source_hash`、`exports`
  - `modules = []`

#### Scenario: packed 模式输出
- **WHEN** `z42c build --release`（release profile，`pack` 默认 true）
- **THEN** 产出 `dist/<name>.zpkg`，内容满足：
  - `mode = "packed"`
  - `modules[]` 非空，每项为完整 `ZbcFile` 内容
  - `files = []`

#### Scenario: kind=exe 包含 entry
- **WHEN** `[project] kind = "exe"` 且 `entry = "Hello.main"`
- **THEN** `.zpkg` 顶层 `entry = "Hello.main"`，`kind = "exe"`

#### Scenario: kind=lib 无 entry
- **WHEN** `[project] kind = "lib"`
- **THEN** `.zpkg` 顶层 `entry` 字段缺失（null），`kind = "lib"`

---

### Requirement: pack 字段优先级链

#### Scenario: project 级默认，profile 覆盖
- **WHEN** `[project].pack = false`，`[profile.release].pack = true`
- **THEN** debug build → indexed zpkg；release build → packed zpkg

#### Scenario: exe 目标级覆盖 project
- **WHEN** `[project].pack = false`，`[[exe]] name="tool" pack=true`（无 profile 配置）
- **THEN** 构建 `tool` 时 → packed zpkg（无论 debug/release）

#### Scenario: profile 覆盖 exe 目标
- **WHEN** `[[exe]] pack=true`，`[profile.debug].pack = false`
- **THEN** debug build 时 → indexed zpkg（profile 优先级最高）

#### Scenario: 未配置 pack 时的默认值
- **WHEN** `z42.toml` 中完全没有 `pack` 字段
- **THEN** debug build → indexed zpkg；release build → packed zpkg

---

### Requirement: VM 加载 .zpkg

#### Scenario: 加载 indexed zpkg
- **WHEN** VM 加载 `dist/hello.zpkg`，`mode = "indexed"`
- **THEN** VM 按 `files[].bytecode` 路径依次读取 `.zbc`，合并符号表后执行

#### Scenario: 加载 packed zpkg
- **WHEN** VM 加载 `dist/hello.zpkg`，`mode = "packed"`
- **THEN** VM 解包 `modules[]` 中内联的 ZbcFile，合并符号表后执行

#### Scenario: VM 加载 .zbc（单文件，不受影响）
- **WHEN** VM 加载 `hello.zbc`
- **THEN** 行为与变更前完全相同，不受 .zpkg 格式变更影响

#### Scenario: 不识别的扩展名
- **WHEN** VM 尝试加载 `.zmod` 或 `.zbin` 文件
- **THEN** 返回错误：`unrecognised artifact extension ".zmod"`（或 ".zbin"）

---

### Requirement: z42.toml [project] section 名

#### Scenario: TOML 解析使用 [project]
- **WHEN** `z42.toml` 中写 `[project]`
- **THEN** 编译器正确解析 `name`、`version`、`kind`、`entry`、`pack` 字段

#### Scenario: 文档与代码一致
- **WHEN** 查看 `docs/design/project.md`
- **THEN** 所有示例 TOML 使用 `[project]`（不再出现 `[package]`）

---

## MODIFIED Requirements

### BuildConfig.Emit 字段移除

**Before:** `[build]` section 含 `emit = "zbc" | "zmod" | "zbin"` 字段，控制工程构建产物格式。

**After:** `[build]` section 不再有 `emit` 字段。工程构建唯一产物为 `.zpkg`，输出模式由 `pack` 字段控制。CLI 单文件模式的 `--emit` flag 不受影响。

### .zmod / .zbin 废除

**Before:** VM 加载器支持 `.zmod`（indexed）和 `.zbin`（packed）两种扩展名。

**After:** 两种扩展名不再受支持，统一用 `.zpkg`。尝试加载 `.zmod` / `.zbin` 返回"不识别扩展名"错误。

---

## Pipeline Steps

受影响的 pipeline 阶段：

- [ ] Lexer — 无变化
- [ ] Parser / AST — 无变化
- [ ] TypeChecker — 无变化
- [ ] IR Codegen — 无变化
- [x] Build Driver（BuildCommand.cs）— 输出 .zpkg
- [x] Project Manifest Parser — 新增 pack 字段解析
- [x] VM Loader — 统一 .zpkg 加载路径
