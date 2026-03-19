# z42 工程文件规范

## 概述

z42 使用 **`z42.toml`** 作为统一的工程配置文件，格式为 TOML。
一个目录下只能有一个 `z42.toml`，它通过顶层 table 的存在与否区分身份：

| 顶层 table | 含义 |
|-----------|------|
| `[project]` | 单工程（库或可执行程序）|
| `[workspace]` | 多工程工作区，聚合多个成员目录 |
| 两者共存 | 工作区根目录同时也是一个工程 |

```
开发者编写        机器生成（勿手改）
─────────────    ──────────────────
z42.toml         .zmod   ← 增量索引
                 .zbc    ← 单文件字节码
                 .zlib   ← 程序集包
```

---

## 文件后缀汇总

| 后缀 | 含义 | 类比 | 格式 | 谁写 |
|------|------|------|------|------|
| `z42.toml` | 工程 / 工作区配置 | `Cargo.toml` / `pyproject.toml` | TOML | 开发者 |
| `.zmod` | 构建增量索引 | `project.assets.json` | JSON | 编译器生成 |
| `.zbc` | 单文件字节码 | `.pyc` | 二进制/JSON | 编译器生成 |
| `.zlib` | 程序集包 | `.dll` / `.jar` | 二进制/JSON | 编译器生成 |

---

## 工程文件（`[project]`）

### 最小示例

```toml
[project]
name    = "Hello"
version = "0.1.0"
kind    = "exe"
entry   = "Hello.Main"
```

### 完整示例

```toml
# z42.toml — Hello World 工程

# ── 工程元数据 ──────────────────────────────────────────────────────────────
[project]
name        = "Hello"
version     = "0.1.0"
description = "Hello World demo"
authors     = ["Alice <alice@example.com>"]
license     = "MIT"
namespace   = "Hello"       # 可选；默认取 name
kind        = "exe"         # exe | lib
entry       = "Hello.Main"  # kind=exe 时必填

# ── 源文件 ──────────────────────────────────────────────────────────────────
[sources]
include = ["src/**/*.z42"]
exclude = ["src/**/*_test.z42"]

# ── 构建选项 ────────────────────────────────────────────────────────────────
[build]
emit        = "zlib"        # 默认输出粒度: ir | zbc | zmod | zlib
mode        = "interp"      # 默认执行模式: interp | jit | aot
incremental = true          # 启用增量编译（跳过 hash 未变文件）
out_dir     = "dist"        # 输出目录（默认: dist/）

# ── 外部依赖 ────────────────────────────────────────────────────────────────
[[dependency]]
name    = "z42.stdlib"
version = "0.1.0"
path    = "../stdlib"       # 本地 z42.toml 所在目录

[[dependency]]
name    = "SomeLib"
version = ">=1.2, <2.0"    # SemVer 范围（未来: registry 拉取）

# ── 编译配置（Profile）──────────────────────────────────────────────────────
[profile.debug]             # z42c build（默认）
mode     = "interp"
optimize = 0
debug    = true

[profile.release]           # z42c build --release
mode     = "jit"
optimize = 3
strip    = true             # 剥除 debug info
```

### 字段说明

#### `[project]`

| 字段 | 类型 | 必填 | 默认 | 说明 |
|------|------|------|------|------|
| `name` | string | ✅ | — | 工程名；同时作为输出文件基名 |
| `version` | string | ✅ | — | SemVer，如 `"0.1.0"` |
| `kind` | `"exe"` \| `"lib"` | ✅ | — | 可执行程序 or 类库 |
| `entry` | string | exe 必填 | — | 完整限定入口，如 `"Hello.Main"` |
| `namespace` | string | ❌ | 取 `name` | 根命名空间 |
| `description` | string | ❌ | — | 简短描述 |
| `authors` | string[] | ❌ | — | 作者列表 |
| `license` | string | ❌ | — | SPDX 许可证标识符 |

#### `[sources]`

| 字段 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `include` | string[] | `["src/**/*.z42"]` | Glob 模式，相对于 `z42.toml` |
| `exclude` | string[] | `[]` | 排除模式 |

#### `[build]`

| 字段 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `emit` | `ir\|zbc\|zmod\|zlib` | `"zlib"` | 默认输出格式 |
| `mode` | `interp\|jit\|aot` | `"interp"` | 默认 VM 执行模式 |
| `incremental` | bool | `true` | 启用 source hash 增量检查 |
| `out_dir` | string | `"dist"` | 产物目录 |

#### `[[dependency]]`

| 字段 | 类型 | 说明 |
|------|------|------|
| `name` | string | 依赖名称 |
| `version` | string | SemVer 约束，如 `">=0.1"` |
| `path` | string | 本地依赖目录（含 `z42.toml`）；与 version 互斥时优先 |

#### `[profile.<name>]`

| 字段 | 类型 | 说明 |
|------|------|------|
| `mode` | `interp\|jit\|aot` | 覆盖全局 exec mode |
| `optimize` | 0–3 | 优化级别（0=无，3=最激进）|
| `debug` | bool | 生成 debug info（默认 debug=true, release=false）|
| `strip` | bool | 剥除 debug info（默认 false）|

---

## 工作区文件（`[workspace]`）

管理多工程仓库，类似 Cargo workspace。

```toml
# z42.toml（仓库根目录）

[workspace]
members = [
    "libs/z42.stdlib",
    "libs/z42.net",
    "apps/hello",
    "apps/demo",
]

# 工作区级共享依赖版本（各工程引用时写 version = "workspace"）
[workspace.dependencies]
"z42.stdlib" = { path = "libs/z42.stdlib", version = "0.1.0" }

# 工作区级 profile 覆盖（被所有 member 继承）
[profile.release]
mode     = "jit"
optimize = 3
strip    = true
```

工作区根目录同时也是一个工程时，`[project]` 与 `[workspace]` 共存：

```toml
[workspace]
members = ["crates/parser", "crates/vm"]

[project]
name    = "z42c"
version = "0.1.0"
kind    = "exe"
entry   = "z42c.Main"
```

### `[workspace]` 字段说明

| 字段 | 说明 |
|------|------|
| `workspace.members` | 相对路径列表，指向各成员目录（每个目录含 `z42.toml`）|
| `workspace.dependencies` | 共享依赖；成员用 `version = "workspace"` 引用 |

---

## 构建命令

```bash
# 开发构建（profile.debug）
z42c build

# 发布构建（profile.release）
z42c build --release

# 指定输出格式
z42c build --emit zbc

# 增量构建
z42c build --incremental

# 工作区：构建所有成员
z42c build                    # 在工作区根运行

# 工作区：只构建指定成员
z42c build --project apps/hello
```

---

## 目录布局约定

```
my-app/
├── z42.toml          ← 工程配置（开发者维护）
├── src/
│   ├── main.z42
│   └── lib.z42
├── dist/             ← 编译产物（.gitignore）
│   └── my-app.zlib
└── .cache/           ← 增量中间产物（.gitignore）
    ├── src/main.zbc
    └── src/lib.zbc
```

多工程工作区：

```
monorepo/
├── z42.toml          ← [workspace] 配置
├── libs/
│   └── z42.stdlib/
│       ├── z42.toml  ← [project] lib
│       └── src/
└── apps/
    └── hello/
        ├── z42.toml  ← [project] exe
        └── src/
```

---

## `z42.toml` vs `.zmod` 对比

| 维度 | `z42.toml` | `.zmod` |
|------|-----------|---------|
| 格式 | TOML | JSON |
| 谁写 | 开发者手写 | 编译器生成 |
| 纳入 VCS | ✅ 是 | ❌ 加入 .gitignore |
| 源文件描述 | Glob 模式 | 已展开路径 + hash |
| 依赖描述 | 版本约束 | 已解析的具体路径 |
| 用途 | 构建入口 | 增量编译索引 |

---

## 约定

- 每个目录最多一个 `z42.toml`
- `.cache/` 和 `dist/` 加入 `.gitignore`
- `z42.toml` 中的路径均相对于该文件所在目录
