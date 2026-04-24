# Spec: project-file

## ADDED Requirements

---

### Requirement: 工程文件格式

z42 工程使用 `<name>.z42.toml` 作为配置文件，格式为 TOML。

#### Scenario: 最小合法工程文件（exe）
- **WHEN** 文件名为 `hello.z42.toml`，内容包含 `[project]` 且 `kind = "exe"`
- **THEN** 编译器能成功解析，`project.name` 默认推断为 `"hello"`
- **AND** `entry` 字段存在且非空

#### Scenario: 最小合法工程文件（lib）
- **WHEN** 文件名为 `mylib.z42.toml`，内容包含 `[project]` 且 `kind = "lib"`
- **THEN** 编译器能成功解析，`entry` 字段可缺失

#### Scenario: name 字段从文件名推断
- **WHEN** `[project]` 中未填写 `name` 字段
- **THEN** `name` 自动取文件名去掉 `.z42.toml` 后缀的部分
- **AND** 命名空间默认由 `name` 推断（kebab-case → PascalCase）

#### Scenario: name 字段显式覆盖
- **WHEN** `[project]` 中显式填写 `name = "MyApp"`
- **THEN** 使用显式值，忽略文件名

#### Scenario: `namespace` 字段显式覆盖
- **WHEN** `[project]` 中填写 `namespace = "Demo.Hello"`
- **THEN** 使用该值作为根命名空间，不再由 `name` 推断

#### Scenario: kind = exe 但缺少 entry
- **WHEN** `kind = "exe"` 且 `entry` 字段缺失
- **THEN** 报错：`error: [project].entry is required when kind = "exe"`

#### Scenario: 包含无法识别的字段
- **WHEN** `z42.toml` 中存在未知字段（如 `foo = "bar"`）
- **THEN** 解析器忽略该字段，不报错（向前兼容）

---

### Requirement: 源文件配置

#### Scenario: 默认源文件发现
- **WHEN** `[sources]` 节缺失
- **THEN** 默认 include `src/**/*.z42`，exclude 为空

#### Scenario: 自定义 include glob
- **WHEN** `[sources] include = ["lib/**/*.z42", "core.z42"]`
- **THEN** 编译器展开 glob，收集所有匹配文件，去重排序

#### Scenario: exclude 优先于 include
- **WHEN** 同一文件同时匹配 include 和 exclude
- **THEN** 该文件被排除

#### Scenario: glob 展开后无文件
- **WHEN** include glob 未匹配到任何 `.z42` 文件
- **THEN** 报错：`error: no source files found`

---

### Requirement: 构建配置

#### Scenario: 默认构建配置
- **WHEN** `[build]` 节缺失
- **THEN** `out_dir = "dist"`，`incremental = true`，`emit` 按 kind 推断

#### Scenario: emit 按 kind 推断
- **WHEN** `[build].emit` 未填写，`kind = "exe"`
- **THEN** emit = `"zbc"`
- **WHEN** `[build].emit` 未填写，`kind = "lib"`
- **THEN** emit = `"zlib"`

#### Scenario: 显式指定 out_dir
- **WHEN** `[build] out_dir = "build/output"`
- **THEN** 产物写入该目录，目录不存在时自动创建

---

### Requirement: Profile 选择

#### Scenario: 默认使用 debug profile
- **WHEN** 执行 `z42c build`，未带任何 profile flag
- **THEN** 使用 `[profile.debug]` 配置；若该节缺失，使用内置 debug 默认值

#### Scenario: --release 切换到 release profile
- **WHEN** 执行 `z42c build --release`
- **THEN** 使用 `[profile.release]` 配置；若该节缺失，使用内置 release 默认值

#### Scenario: profile 字段覆盖 build 全局配置
- **WHEN** `[build] mode = "interp"` 且 `[profile.release] mode = "jit"`，执行 `--release`
- **THEN** 实际使用 `mode = "jit"`（profile 优先）

#### Scenario: 内置 profile 默认值
- **WHEN** `[profile.debug]` 节缺失
- **THEN** 等价于：`mode = "interp"`, `optimize = 0`, `debug = true`
- **WHEN** `[profile.release]` 节缺失
- **THEN** 等价于：`mode = "jit"`, `optimize = 3`, `strip = true`

---

### Requirement: `z42c build` 子命令 — 项目模式

#### Scenario: 自动发现唯一工程文件
- **WHEN** 当前目录恰好有一个 `*.z42.toml` 文件，执行 `z42c build`
- **THEN** 自动读取该文件并构建

#### Scenario: 多个工程文件需显式指定
- **WHEN** 当前目录有多个 `*.z42.toml` 文件，执行 `z42c build`
- **THEN** 报错：`error: multiple .z42.toml files found, please specify one: z42c build <name>.z42.toml`

#### Scenario: 显式指定工程文件
- **WHEN** 执行 `z42c build hello.z42.toml`
- **THEN** 读取指定文件，忽略目录中其他 `.z42.toml`

#### Scenario: 当前目录无工程文件
- **WHEN** 当前目录没有任何 `*.z42.toml` 文件，执行 `z42c build`
- **THEN** 报错：`error: no .z42.toml found in current directory`

#### Scenario: CLI flag 覆盖工程文件配置
- **WHEN** 执行 `z42c build --emit zbc`
- **THEN** emit 使用 `zbc`，覆盖工程文件中的 `[build].emit`

---

### Requirement: 单文件模式完整保留

#### Scenario: 单文件模式不读取工程文件
- **WHEN** 当前目录存在 `hello.z42.toml`，执行 `z42c hello.z42`
- **THEN** 完全忽略 `.z42.toml`，按单文件模式编译

#### Scenario: 单文件模式保留所有调试 flags
- **WHEN** 执行 `z42c file.z42 --dump-tokens / --dump-ast / --dump-ir`
- **THEN** 正常输出，行为与之前一致

---

### Requirement: help 文本清晰展示两种模式

#### Scenario: 无参数执行 z42c
- **WHEN** 执行 `z42c`（无参数）
- **THEN** 输出 help，明确分两块展示：
  - **Project mode**（`z42c build`）：构建工程，列出 build 相关 flags
  - **Single-file mode**（`z42c <file.z42>`）：快速编译，列出 --emit / --dump-* flags
  - 两块之间有明确分隔，说明各自适用场景
