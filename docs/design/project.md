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
| `name` | string | ✅ | kebab-case；作为输出文件基名 |
| `version` | string | ✅ | SemVer，如 `"0.1.0"` |
| `kind` | `"exe"` \| `"lib"` | 单目标必填；多目标用 `[[exe]]` 时省略 | 可执行程序 or 类库 |
| `entry` | string | `kind="exe"` 时必填 | 完全限定入口函数，如 `"Hello.main"` |

**`name` 与命名空间的关系：**

`name = "hello-world"` → 默认根命名空间 `HelloWorld`（kebab 转 PascalCase）。
需要定制时可显式指定：

```toml
[project]
name      = "hello"
namespace = "Demo.Hello"   # 覆盖默认推断
```

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

引用外部 z42 库。

```toml
[project]
name    = "myapp"
version = "0.1.0"
kind    = "exe"
entry   = "MyApp.main"

[dependencies]
z42-std  = { path = "../std" }               # 本地路径依赖
z42-http = { version = ">=0.2, <1.0" }       # 版本约束（未来：注册中心）
```

**依赖解析规则：**

| 字段 | 说明 |
|------|------|
| `path` | 指向含 `z42.toml` 的本地目录，当前阶段支持 |
| `version` | SemVer 约束；需注册中心支持（预留格式，暂不实现）|
| `path` + `version` 同时存在 | `path` 优先，`version` 仅作文档说明 |

**依赖对编译器的影响：**

- TypeChecker：可见依赖库导出的类型和函数签名
- IR Codegen：生成跨库调用的 `call` 指令（含模块引用）
- VM 加载：按 `dependencies` 顺序加载 `.zbin`，合并符号表

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
z42-std = { path = "../z42-std" }

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

管理多工程 monorepo，统一构建和版本。

```toml
# monorepo 根目录的 z42.toml

[workspace]
members = [
    "libs/z42-std",
    "libs/z42-net",
    "apps/hello",
    "apps/demo",
]

# 工作区级共享依赖（成员用 version = "workspace" 引用）
[workspace.dependencies]
z42-std = { path = "libs/z42-std", version = "0.1.0" }

# 工作区级 profile（被所有成员继承，可被成员覆盖）
[profile.release]
mode     = "jit"
optimize = 3
strip    = true
```

成员工程引用工作区共享依赖：

```toml
# apps/hello/z42.toml
[project]
name    = "hello"
version = "0.1.0"
kind    = "exe"
entry   = "Hello.main"

[dependencies]
z42-std = { version = "workspace" }   # 版本由 workspace 统一管理
```

工作区根目录也是工程时，`[workspace]` 与 `[project]` 共存：

```toml
[workspace]
members = ["libs/parser", "libs/vm"]

[project]
name    = "z42c"
version = "0.1.0"
kind    = "exe"
entry   = "z42c.main"
```

**构建命令：**

```bash
z42c build                        # 构建所有 workspace members
z42c build --package apps/hello   # 只构建指定成员
```

**目录结构：**

```
monorepo/
├── z42.workspace.toml    ← [workspace]
├── libs/
│   ├── z42-std/
│   │   ├── z42-std.z42.toml  ← [project] lib
│   │   └── src/
│   └── z42-net/
│       ├── z42-net.z42.toml
│       └── src/
└── apps/
    └── hello/
        ├── hello.z42.toml  ← [project] exe
        └── src/
```

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
namespace   = "MyApp"           # 可选，默认由 name 推断
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
# name = { path = "..." }       # 本地依赖（当前阶段）
# name = { version = "..." }    # 注册中心（预留）

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

[workspace]                     # L6，与 [project] 可共存
members = []
[workspace.dependencies]
# name = { path = "...", version = "..." }
```
