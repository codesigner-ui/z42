# z42 工程文件规范（<name>.z42.toml）

z42 使用 **`<name>.z42.toml`** 作为工程配置文件，格式为 TOML。
一个目录下最多一个 `*.z42.toml`，支持单工程和多工程工作区两种形态。

---

## 层次概览

本文档按复杂度递进，分六个层次描述完整的 manifest 语义：

| 层次 | 内容 | 场景 |
|------|------|------|
| L1 | 包身份 + 入口 | 最小可构建工程 |
| L2 | 源文件配置 | 多文件工程 |
| L3 | 构建产物配置 | 控制输出格式和目录 |
| L4 | 运行时 Profile | debug / release 分离 |
| L5 | 依赖管理 | 引用外部库 |
| L6 | 工作区 | monorepo |

---

## L1 — 包身份（最小工程）

每个工程必须有唯一标识和产物类型。

```toml
[project]
name    = "hello"      # 工程名，kebab-case
version = "0.1.0"      # SemVer
kind    = "exe"        # exe | lib
entry   = "Hello.main" # kind=exe 时必填，完全限定函数名
```

**字段说明：**

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `name` | string | ✅ | kebab-case；作为输出文件基名和依赖引用键 |
| `version` | string | ✅ | SemVer，如 `"0.1.0"` |
| `kind` | `"exe"` \| `"lib"` | 单目标必填；多目标用 `[[exe]]` 时省略 | 可执行程序 or 类库 |
| `entry` | string | `kind="exe"` 时必填 | 完全限定入口函数，如 `"Hello.main"` |

**`name` 与命名空间的关系：**

`name` 是包的文件标识符，与命名空间**无关**。命名空间完全由源文件中的 `namespace xxx;` 声明决定，编译器在构建时从源文件中收集并写入 zpkg 的 `namespaces` 字段。`[dependencies]` 中填写的是包名（用于找文件），`using` 语句中填写的是命名空间（由源文件决定），两者无需一致。

**一个 zpkg 可包含多个命名空间（C# 风格）：**

一个 zpkg 不限定只含单一命名空间。像 C# 程序集一样，一个 lib 包内不同 `.z42` 源文件可以属于不同命名空间，所有命名空间都会被收集到 zpkg 的 `namespaces` 字段中。

```
# 包 my-sdk 的源文件结构：
my-sdk/src/
  Client.z42       → namespace Company.Sdk;
  ClientBuilder.z42 → namespace Company.Sdk;
  Internal.z42     → namespace Company.Sdk.Internal;
  Testing.z42      → namespace Company.Sdk.Testing;

# 构建产物：
dist/my-sdk.zpkg  namespaces = ["Company.Sdk", "Company.Sdk.Internal", "Company.Sdk.Testing"]
```

命名空间解析规则：
- 编译器的依赖加载：读 zpkg 的 `namespaces` 列表，所有列出的命名空间均可见（无需在 `[dependencies]` 中逐一声明）
- VM 的 lazy loader：通过 `namespaces.iter().any(|n| n == requested_ns)` 判断 zpkg 是否提供某命名空间
- 同一命名空间不允许被两个不同 zpkg 同时提供（`AmbiguousNamespaceError`）

**`kind` 决定默认产物：**

| kind | 默认 emit | 说明 |
|------|-----------|------|
| `exe` | `zbc` | 单文件可执行字节码 |
| `lib` | `zbin` | 打包库（含所有模块）|

**多可执行目标（`[[exe]]`）：**

当一个工程需要产出多个可执行文件时，用 `[[exe]]` 数组表代替 `[project] kind = "exe"`：

```toml
[project]
version = "0.1.0"
# kind 省略 — 由 [[exe]] 推断

[sources]
include = ["src/**/*.z42"]   # 所有 exe 默认共享

[[exe]]
name  = "hello"              # 产物：dist/hello.zbc
entry = "Hello.main"

[[exe]]
name  = "tool"               # 产物：dist/tool.zbc
entry = "Tool.main"
src   = ["src/tool/**/*.z42"] # 可选：覆盖共享 sources
```

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `name` | string | ✅ | exe 名，同时作为产物文件基名 |
| `entry` | string | ✅ | 完全限定入口函数 |
| `src` | string[] | ❌ | 独立 glob，覆盖 `[sources]`；省略则继承 `[sources]` |

```bash
z42c build               # 构建所有 [[exe]]
z42c build --exe hello   # 只构建名为 hello 的目标
```

> `[[exe]]` 与 `[project] kind = "exe"` 不能共存，二选一。

**两种编译模式（职责分离）：**

```bash
# 项目模式 — 读取 <name>.z42.toml，用于正式构建和交付
z42c build                      # 自动发现 *.z42.toml，profile.debug
z42c build --release            # profile.release
z42c build hello.z42.toml       # 显式指定工程文件

# 单文件模式 — 不读取任何 .z42.toml，用于快速编译和调试
z42c hello.z42                  # 编译单文件，默认 --emit ir
z42c hello.z42 --emit zbc       # 指定产物格式
z42c hello.z42 --dump-ast       # 调试：查看 AST
```

---

## L2 — 源文件配置

控制哪些 `.z42` 文件参与编译。

```toml
[project]
name    = "mylib"
version = "0.1.0"
kind    = "lib"

[sources]
include = ["src/**/*.z42"]           # glob，相对于 z42.toml
exclude = ["src/**/*_test.z42"]      # 排除测试文件
```

**字段说明：**

| 字段 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `include` | string[] | `["src/**/*.z42"]` | glob 模式列表 |
| `exclude` | string[] | `[]` | 排除模式；优先于 include |

**目录布局约定（无 `[sources]` 时的默认行为）：**

```
my-app/
├── z42.toml
└── src/
    ├── main.z42      ← exe 默认入口文件
    └── lib.z42       ← lib 默认根文件
```

---

## L3 — 构建产物配置

控制产物格式、输出目录和增量编译。

```toml
[project]
name = "myapp"
version = "0.1.0"
kind = "exe"
entry = "MyApp.main"
pack = false           # 工程级 pack 默认值（最低优先级）

[build]
out_dir     = "dist"   # 产物目录，默认 "dist/"
incremental = true     # 启用增量编译，默认 true

[profile.debug]
pack = false           # debug build → indexed zpkg（.cache/*.zbc + dist/<name>.zpkg）

[profile.release]
pack = true            # release build → packed zpkg（单文件，dist/<name>.zpkg）
```

**`[build]` 字段说明：**

| 字段 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `out_dir` | string | `"dist"` | 产物输出目录 |
| `incremental` | bool | `true` | 基于 source hash 跳过未改动文件 |

**`pack` 字段说明（三层优先级，高→低）：**

| 层次 | 位置 | 说明 |
|------|------|------|
| 最高 | `[profile.debug/release].pack` | 当前 profile 覆盖 |
| 中 | `[[exe]].pack` | 单个可执行目标覆盖 |
| 最低 | `[project].pack` | 工程级全局默认 |

**内置默认值（未显式配置时）：** `debug` → `false`（indexed），`release` → `true`（packed）

**产物输出：**

| pack 值 | 产物 | 说明 |
|---------|------|------|
| `false` | `dist/<name>.zpkg` (`mode=indexed`) + `.cache/*.zbc` | 开发态，支持增量更新 |
| `true`  | `dist/<name>.zpkg` (`mode=packed`) | 发布态，单文件自包含 |

**增量编译工作方式（pack=false）：**

```
z42c build
  ├─ 读取 dist/<name>.zpkg（若存在）
  ├─ 对比每个 .z42 文件的 SHA-256 与记录值
  │    ├─ 相同 → 跳过重编译
  │    └─ 不同 → 重编译该文件，更新 .cache/*.zbc 和 source_hash
  └─ 更新 dist/<name>.zpkg 中变化项的 source_hash
```

**目录结构（含产物）：**

```
my-app/
├── z42.toml
├── src/
│   └── main.z42
├── dist/               ← out_dir（加入 .gitignore）
│   └── my-app.zpkg     ← indexed 或 packed，取决于 pack 设置
└── .cache/             ← 增量中间产物（加入 .gitignore）
    └── src/main.zbc    ← 仅 pack=false 时生成
```

---

## L4 — 运行时 Profile

区分开发和发布构建，覆盖执行模式和优化级别。

```toml
[project]
name  = "myapp"
version = "0.1.0"
kind  = "exe"
entry = "MyApp.main"

[build]
mode = "interp"        # 全局默认执行模式

[profile.debug]        # z42c build（默认）
mode     = "interp"
optimize = 0
debug    = true        # 保留调试信息（zbc META section）

[profile.release]      # z42c build --release
mode     = "jit"
optimize = 3
strip    = true        # 剥除 META section
```

**Profile 字段说明：**

| 字段 | 类型 | 说明 |
|------|------|------|
| `mode` | `interp\|jit\|aot` | 执行模式，覆盖 `[build].mode` |
| `optimize` | 0–3 | 优化级别（0=无，3=最激进）|
| `debug` | bool | 生成调试信息，默认 debug=true / release=false |
| `strip` | bool | 剥除调试信息，默认 false |
| `pack` | bool | 覆盖 `[[exe]]` 和 `[project].pack`；null = 不覆盖 |

**执行模式三级优先级（高→低）：**

```
源码注解 @interp / @jit / @aot   ← 最高，作用于单个命名空间
  ↓
profile 中的 mode 字段
  ↓
[build].mode 全局默认             ← 最低
```

**构建命令：**

```bash
z42c build              # 使用 profile.debug
z42c build --release    # 使用 profile.release
z42c build --profile staging   # 使用自定义 profile（如有）
```

---

## L5 — 依赖管理

声明项目依赖的外部 `.zpkg` 库。

```toml
[project]
name    = "myapp"
version = "0.1.0"
kind    = "exe"
entry   = "MyApp.main"

[dependencies]
"my-utils" = "*"         # 在 libs/ 中找 name="my-utils" 的 .zpkg
"my-http"  = "*"         # 版本约束目前只做存在性校验，不做 semver 比较
```

**设计原则：命名空间与包名解耦**

`[dependencies]` 中填写的是 **zpkg 的 `[project] name` 字段**，而非命名空间名称。编译器在 libs/ 搜索路径中找到对应 zpkg 后，读取其 `namespaces` 字段，将导出的命名空间注册为可用。

```
[dependencies] "my-http" = "*"
  → 编译器在 libs/ 找 name="my-http" 的 .zpkg
  → 读该 zpkg 的 namespaces: ["Http", "Http.Client"]
  → 源码中 using Http; / using Http.Client; 均可解析
```

这意味着 `using` 语句中的命名空间名称与 `[dependencies]` 中的包名**无需一致**，由 zpkg 自身的 manifest 决定。

**stdlib 无需声明：** `z42.core` 由 VM 启动时自动注入，其他 stdlib 模块（`z42.io`、`z42.math` 等）按需从 libs/ 自动加载，均不需要出现在 `[dependencies]` 中。

**有 `[dependencies]` vs 无 `[dependencies]`：**

| 情况 | 编译器行为 |
|------|-----------|
| 有 `[dependencies]` | 只扫描声明的包，libs/ 中其他 zpkg 不参与 `using` 解析 |
| 无 `[dependencies]`（脚本/单文件模式）| 自动扫描 libs/ 全部 zpkg 和 Z42_PATH 全部 zbc |

**依赖解析后的编译器行为：**

- TypeChecker：可见依赖库导出的类型和函数签名
- IR Codegen：生成跨库调用的 `call` 指令（含模块引用）
- 输出 zpkg 的 `dependencies` 字段：记录编译期实际解析到的文件名和命名空间（不是 `[dependencies]` 的原样复制）

**版本管理：** 目前不做 semver 比较，也不生成 lockfile。用户通过控制 libs/ 中实际存放的文件版本来锁定依赖。

**完整示例（含依赖）：**

```toml
[project]
name    = "hello"
version = "0.1.0"
kind    = "exe"
entry   = "Hello.main"

[sources]
include = ["src/**/*.z42"]

[build]
out_dir = "dist"

[dependencies]
"my-utils" = "*"

[profile.debug]
mode  = "interp"
debug = true

[profile.release]
mode     = "jit"
optimize = 3
strip    = true
```

---

## L6 — 工作区（Workspace）

管理多工程 monorepo，统一构建、版本与共享元数据。

> **C1 落地范围（2026-04-26）**：本节描述 schema 与共享继承的最终形态。
> include 机制（C2）、policy 强制（C3）、CLI 工具链（C4）见后续章节。

### L6.1 文件名与角色

| 文件 | 数量 | 角色 |
|---|---|---|
| `z42.workspace.toml` | workspace 根目录唯一一份 | virtual manifest：协调 + 共享配置 |
| `<name>.z42.toml` | 每个 member 一份 | member 自身配置 |

`z42.workspace.toml` 是 **virtual manifest**：仅含 `[workspace.*]` / `[profile.*]` /
`[policy]` 段，**不允许**有 `[project]` 段（违反报 `WS036`）。`[workspace]` 段也只能
出现在文件名为 `z42.workspace.toml` 的文件中（违反报 `WS030`）。

### L6.2 顶层结构

```toml
# z42.workspace.toml — virtual manifest

[workspace]
members         = ["libs/*", "apps/*"]   # glob 与显式路径混用
exclude         = ["libs/sandbox-*"]     # 从 glob 结果排除
default-members = ["apps/hello"]         # 不带 -p 时的默认编译子集
resolver        = "1"

[workspace.project]                      # 共享元数据（D5：仅以下 4 个字段可共享）
version     = "0.1.0"
authors     = ["z42 team"]
license     = "MIT"
description = "..."

[workspace.dependencies]                 # 中央版本声明
"my-utils" = { path = "libs/my-utils", version = "0.1.0" }

[workspace.build]                        # 集中产物（C3 落地实际行为）
out_dir   = "dist"
cache_dir = ".cache"

[policy]                                 # 强制策略（C3 落地）
"build.out_dir" = "dist"

[profile.debug]                          # 集中 profile（成员不可覆盖）
mode  = "interp"
debug = true

[profile.release]
mode     = "jit"
optimize = 3
strip    = true
```

### L6.3 Member 引用 workspace 共享

```toml
# apps/hello/hello.z42.toml

[project]
name              = "hello"              # 身份字段必须 member 自己声明
kind              = "exe"
entry             = "Hello.main"
version.workspace = true                 # ← Cargo 风格：引用 workspace.project.version
license.workspace = true

[sources]
include = ["src/**/*.z42"]

[dependencies]
"my-utils".workspace = true              # ← 引用 workspace.dependencies."my-utils"
"my-http"  = { workspace = true, optional = true }   # 局部修饰
```

**身份字段 vs 共享字段（D5）：**

| 字段 | 共享 | 备注 |
|---|---|---|
| `name` / `kind` / `entry` | ❌ | member 必须自己声明（防止意外冲突） |
| `version` / `authors` / `license` / `description` | ✅ | 可用 `xxx.workspace = true` 引用 |

**禁用旧语法：** `version = "workspace"` 不再支持（报 `WS035`），改为 key 层面
`xxx.workspace = true` 或 `{ workspace = true }`。

### L6.4 Members 展开

```toml
members = ["libs/*", "apps/main", "experiments/foo"]
exclude = ["libs/sandbox-*"]
```

- glob 仅匹配**目录**，目录内必须恰好一份 `*.z42.toml`（多份 → `WS005`）
- 显式路径与 glob 可混用
- exclude 优先于 members
- `default-members` 必须是展开结果的子集（否则 `WS031`）

orphan member（子树有 manifest 但未被 `members` 命中）→ `WS007`（warning，不阻塞构建）。

### L6.5 include 机制（C2，2026-04-26）

Member 与 preset 文件可通过 `include` 字段拉入**子树/分组共享配置**（如 "libs/* 都用 lib defaults"），位置自由、不依赖目录层级。

```toml
# libs/foo/foo.z42.toml
include = [
    "${workspace_dir}/presets/lib-defaults.toml",
    "${workspace_dir}/presets/strict-lints.toml",
]

[project]
name              = "foo"
version.workspace = true
```

#### 合并语义

| 字段类型 | 合并规则 |
|---|---|
| 标量（`kind` / `license` / `version`） | 后写者覆盖前写者；自身（声明 include 的文件）覆盖所有 preset |
| 表（如 `[project]`） | 字段级合并；同名字段后者覆盖 |
| 数组（如 `[sources].include`） | **整体覆盖**（不连接，避免菱形依赖时列表难推测） |
| `[dependencies]` | 按 `name` 字典合并；同名后者整体替换 |

#### 路径规则

- 路径相对于声明 include 的文件
- 支持 `${workspace_dir}` / `${member_dir}` 模板变量
- **不允许**：绝对系统路径 / URL / glob 模式（违反报 `WS024`）

#### Preset 文件段限制（WS021）

Preset 不允许：
- `[workspace.*]`、`[policy]`、`[profile.*]` —— 治理一致性，必须从 workspace 根下发
- `[project].name`、`[project].entry` —— 身份字段，member 必须自己声明

Preset **允许**：`[project] kind / license / authors / description / pack`、`[sources]`、`[build]`、`[dependencies]`。

#### 嵌套 / 循环 / 菱形

- preset 内可再写 `include` 拉入其他 preset
- 嵌套深度上限 8 层（超过 → `WS022`）
- 循环 include（A→A 或 A→B→A）→ `WS020`，错误信息列完整环
- 菱形 include（同一文件被多次拉入）→ 去重，仅合并一次（不报错）

#### 配置生效顺序（C2 完整）

```
1. workspace 根 [workspace.project] / [workspace.dependencies]   （C1 默认）
2. member 的 include 链按声明顺序展开 + 合并                     （C2 新增）
3. member 自身字段                                              （member 覆盖）
4. workspace 根 [policy]                                        （C3 强制）
5. CLI flag                                                     （C4 最终）
```

#### 错误码（C2 新增）

| 码 | 含义 | 级别 |
|---|---|---|
| WS020 | 循环 include | error |
| WS021 | preset 含禁用段 | error |
| WS022 | include 嵌套深度超过 8 层 | error |
| WS023 | include 路径不存在 | error |
| WS024 | include 路径不允许（绝对路径 / URL / glob） | error |

#### 示例

完整可解析示例见 `examples/workspace-with-presets/`：
- `presets/lib-defaults.toml` 提供 `kind=lib` + `[sources]` 默认
- `presets/strict-lints.toml` 提供 `[build].mode = "interp"`
- `libs/foo/` include lib-defaults
- `libs/bar/` include lib-defaults + strict-lints（后者覆盖前者重叠字段）

---

### L6.6 Policy 与集中产物（C3，2026-04-26）

#### `[workspace.build]` 集中产物布局

workspace 模式下，所有 member 产物**集中**到 workspace 根：

```
<workspace_root>/
├── dist/                     ← out_dir
│   ├── foo.zpkg              （每 member 一份）
│   ├── bar.zpkg
│   └── hello.zpkg
└── .cache/                   ← cache_dir
    ├── foo/                  （按 member 分子目录，避免同名源文件冲突）
    │   └── src/Foo.zbc
    ├── bar/
    └── hello/
```

```toml
# z42.workspace.toml
[workspace.build]
out_dir   = "dist/${profile}"   # ${profile} 模板：debug/release 各自分流
cache_dir = ".cache"
```

#### `[policy]` 强制策略

`[policy]` 段锁定字段值，member 不可覆盖（违反报 `WS010`）：

```toml
[policy]
"profile.release.strip" = true        # release 产物必须 strip
"build.mode"            = "interp"    # 全 workspace 仅用 interp 模式
```

**字段路径表达式**（D3.1）：用点分隔的扁平字符串 key。

**默认锁定字段**（D3.2）：

| 字段路径 | 默认锁定值 |
|---|---|
| `build.out_dir` | `[workspace.build].out_dir` |
| `build.cache_dir` | `[workspace.build].cache_dir` |

无需在 `[policy]` 显式声明；自动生效。member 若试图覆盖产物路径 → `WS010`。

#### Policy 检测语义

`PolicyEnforcer` 仅检查 member **显式声明**的字段。例：

- Member 不写 `[build]` → 无冲突，使用 workspace 默认 `dist`
- Member 写 `[build] out_dir = "dist"`（与 workspace 锁定值相同）→ 不冲突，origin 标 PolicyLocked
- Member 写 `[build] out_dir = "custom"` → `WS010`

#### 字段路径不存在（WS011）

```toml
[policy]
"build.outdir" = "dist"   # 拼写错误（应为 build.out_dir）
```

报 `WS011 PolicyFieldPathNotFound`，附 fuzzy 建议（编辑距离 ≤ 3）：`did you mean 'build.out_dir'?`

#### ResolvedManifest 集中产物字段

C3 在 `ResolvedManifest` 上新增（workspace 模式才有意义）：

| 字段 | 含义 |
|---|---|
| `IsCentralized` | true = workspace 集中布局；false = 单工程 |
| `EffectiveOutDir` | 集中产物目录绝对路径（已应用 `${profile}` 等模板） |
| `EffectiveCacheDir` | 该 member 的 cache 目录绝对路径（含 member 子目录） |
| `EffectiveProductPath` | 该 member 产物完整路径 (`<EffectiveOutDir>/<name>.zpkg`) |

C4 的 `WorkspaceBuildOrchestrator` 直接消费 `EffectiveProductPath` 写产物。

#### 错误码（C3 新增）

| 码 | 含义 | 级别 |
|---|---|---|
| WS010 | Policy 冲突：member 字段值与 workspace 锁定值不一致 | error |
| WS011 | Policy 字段路径不存在（含 fuzzy 建议） | error |

> WS004（C1 占位）在 C3 标记 `[Obsolete]`，C4 归档时彻底移除（归并入 WS010）。

#### 示例

完整可解析示例见 `examples/workspace-with-policy/`：含 `[workspace.build] out_dir = "dist/${profile}"` + `[policy] "profile.release.strip" = true`。

---

### L6.7 Member 段限制

Member `<name>.z42.toml` **不允许**以下段（违反报 `WS003`）：

- `[workspace]` / `[workspace.*]` —— 全仓共享必须从根下发
- `[policy]` —— 治理一致性
- `[profile.*]` —— profile 集中在 workspace 根

### L6.8 路径模板变量（D8）

路径字段允许 4 个内置只读变量：

| 变量 | 含义 |
|---|---|
| `${workspace_dir}` | workspace 根绝对路径 |
| `${member_dir}` | 当前 member 目录绝对路径 |
| `${member_name}` | 当前 member `[project].name` |
| `${profile}` | 当前激活 profile 名 |

**语法**：
- 占位 `${name}`；字面量 `$` 写 `$$`
- 嵌套 / 未闭合 / 非法字符 → `WS038`
- 未知变量（含 `${env:NAME}` 暂不支持）→ `WS037`

**允许字段白名单**（其他字段出现 `${...}` 报 `WS039`）：

- `include` 数组各元素（C2 用）
- `[workspace.build] out_dir` / `cache_dir`（C3 用）
- `[workspace.dependencies] xxx.path` / `[dependencies] xxx.path`
- `[sources] include / exclude`

**禁止字段**：标量元数据（`version` / `name` / `kind` / `entry` / `description` /
`license` / `authors`）以及 `members` glob 模式。

```toml
# 合法
[workspace.build]
out_dir = "dist/${profile}"               # 展开 → dist/release

# 非法（WS039）
[project]
version = "${profile}"                    # 标量字段不允许变量
```

### L6.9 配置生效顺序

```
最终 member 配置 = 以下层按顺序合并：

1. workspace 根 [workspace.project] / [workspace.build] / [workspace.dependencies]   (C1 默认)
2. member 的 include 链按声明顺序展开 + 合并                                          (C2)
3. member 自身 *.z42.toml 字段                                                       (C1 member 覆盖)
4. workspace 根 [policy] 段                                                          (C3 强制覆盖)
5. CLI flag（--release / --profile X / --no-incremental 等）                          (C4 最终覆盖)
```

C1 仅落实步骤 1 + 3（含路径模板展开）；C2 加 2；C3 加 4；C4 加 5。

### L6.10 错误码索引（C1+C2+C3 范围）

| 码 | 含义 | 级别 |
|---|---|---|
| WS003 | Member 内出现禁用段（`[workspace.*]` / `[policy]` / `[profile.*]`） | error |
| WS005 | 同目录两份 `*.z42.toml` 引发歧义 | error |
| WS007 | Manifest 在 workspace 子树内但未被 members 命中 | warning |
| WS030 | `[workspace]` 段出现在非 `z42.workspace.toml` 文件 | error |
| WS031 | `default-members` 含未匹配项 | error |
| WS032 | Member 引用 workspace 共享字段，但根未声明 | error |
| WS033 | `[workspace.project]` 字段类型错误 / 不可共享字段被声明 | error |
| WS034 | Member 引用未声明的 workspace 依赖 | error |
| WS035 | 出现已废弃的 `version = "workspace"` 语法 | error |
| WS036 | Workspace 根 manifest 同时含 `[workspace]` 与 `[project]` | error |
| WS037 | 路径模板含未知变量 | error |
| WS038 | 路径模板语法非法 | error |
| WS039 | 模板变量出现在不允许的字段 | error |

> WS020-024 为 C2 启用（include）。WS010/011 为 C3 启用（policy）。WS001/002/006 为 C4 范围。WS004 在 C3 标记废弃。

### L6.11 目录结构样板

```
monorepo/
├── z42.workspace.toml
├── libs/
│   ├── greeter/
│   │   ├── greeter.z42.toml          ← member（kind=lib）
│   │   └── src/
│   └── ...
└── apps/
    └── hello/
        ├── hello.z42.toml            ← member（kind=exe）
        └── src/
```

**完整可解析示例**：见 `examples/workspace-basic/`。

---

## 产物文件汇总

| 文件 | 含义 | 谁写 | 纳入 VCS |
|------|------|------|---------|
| `<name>.z42.toml` | 工程 / 工作区配置 | 开发者 | ✅ |
| `.cache/*.zbc` | 单文件增量字节码（pack=false 时生成）| 编译器 | ❌ |
| `dist/*.zpkg` | 工程包（indexed 或 packed）| 编译器 | ❌ |

`.cache/` 和 `dist/` 加入 `.gitignore`。

---

## 完整字段速查

```toml
[project]
name        = "my-app"          # 必填
version     = "0.1.0"           # 必填，SemVer
kind        = "exe"             # 必填，exe | lib
entry       = "MyApp.main"      # exe 必填
description = ""                # 可选
authors     = []                # 可选
license     = "MIT"             # 可选，SPDX
pack        = false             # 可选，工程级 pack 默认值

[sources]
include = ["src/**/*.z42"]      # 默认值
exclude = []                    # 默认值

[build]
out_dir     = "dist"            # 默认值
incremental = true              # 默认 true
mode        = "interp"          # 全局默认执行模式

[dependencies]
# "pkg-name" = "*"              # zpkg 包名（匹配 zpkg manifest 的 [project] name）
# "pkg-name" = "1.2.0"         # 版本约束（目前仅做存在性校验）
# stdlib 无需声明，由 VM 自动加载

[profile.debug]
mode     = "interp"
optimize = 0
debug    = true
pack     = false                # → indexed zpkg（默认）

[profile.release]
mode     = "jit"
optimize = 3
strip    = true
pack     = true                 # → packed zpkg（默认）

# ── workspace 模式（仅 z42.workspace.toml 中合法） ─────────────────────────
[workspace]
members         = ["libs/*", "apps/*"]
exclude         = []
default-members = []
resolver        = "1"

[workspace.project]              # 共享元数据；成员用 xxx.workspace = true 引用
version     = "0.1.0"
authors     = []
license     = "MIT"
description = ""

[workspace.dependencies]         # 中央版本声明；成员用 dep.workspace = true 引用
# "pkg-name" = { path = "...", version = "0.1.0" }

[workspace.build]                # 集中产物（C3 实施实际行为）
out_dir   = "dist"
cache_dir = ".cache"

[policy]                         # 强制策略（C3 实施）
# "build.out_dir" = "dist"

[workspace]                     # L6，与 [project] 可共存
members = []
[workspace.dependencies]
# name = { path = "...", version = "..." }
```
