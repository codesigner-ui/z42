# z42 工程文件规范

## 概述

z42 使用 TOML 格式的工程文件管理编译单元，设计参考 Cargo.toml 的简洁性和 .csproj 的工程组织方式。

```
开发者编写        机器生成（勿手改）
─────────────    ──────────────────
.z42proj         .zmod   ← 增量索引
.z42sln          .zbc    ← 单文件字节码
                 .zlib   ← 程序集包
```

---

## 文件后缀

| 后缀 | 全称 | 类比 | 格式 | 用途 |
|------|------|------|------|------|
| `.z42proj` | z42 Project | `.csproj` | TOML | 单工程配置（开发者手写）|
| `.z42sln`  | z42 Solution | `.slnx` / `Cargo workspace` | TOML | 多工程聚合（开发者手写）|
| `.zmod`    | z42 Module Manifest | `project.assets.json` | JSON | 构建系统生成的增量索引 |
| `.zbc`     | z42 Bytecode | `.pyc` | 二进制/JSON | 单文件编译产物 |
| `.zlib`    | z42 Library | `.dll` | 二进制/JSON | 程序集产物 |

---

## `.z42proj` — 工程文件

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
# hello.z42proj

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
path    = "../stdlib/z42.stdlib.z42proj"   # 本地路径

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
| `name` | string | ✅ | — | 工程名，同时作为输出文件基名 |
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
| `include` | string[] | `["src/**/*.z42"]` | Glob 模式，相对于 `.z42proj` |
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
| `path` | string | 本地 `.z42proj` 路径（与 version 互斥时优先）|

#### `[profile.<name>]`

| 字段 | 类型 | 说明 |
|------|------|------|
| `mode` | `interp\|jit\|aot` | 覆盖全局 exec mode |
| `optimize` | 0–3 | 优化级别（0=无，3=最激进）|
| `debug` | bool | 生成 debug info（默认 debug=true, release=false）|
| `strip` | bool | 剥除 debug info（默认 false）|

---

## `.z42sln` — 工作区文件

管理多工程仓库，类似 Cargo workspace 或 .NET `.slnx`。

```toml
# z42.z42sln

[workspace]
members = [
    "libs/z42.stdlib",
    "libs/z42.net",
    "apps/hello",
    "apps/demo",
]

# 工作区级共享依赖版本（各工程仍可覆盖）
[workspace.dependencies]
"z42.stdlib" = { path = "libs/z42.stdlib", version = "0.1.0" }

# 工作区级 profile 覆盖
[profile.release]
mode     = "jit"
optimize = 3
strip    = true
```

### 字段说明

| 字段 | 说明 |
|------|------|
| `workspace.members` | 相对路径列表，指向各 `.z42proj` 所在目录 |
| `workspace.dependencies` | 共享依赖，工程里用 `version = "workspace"` 引用 |

---

## 构建工作流

### 单工程构建

```bash
# 开发构建（profile.debug）
z42c build hello.z42proj

# 发布构建（profile.release）
z42c build hello.z42proj --release

# 指定输出格式
z42c build hello.z42proj --emit zbc

# 增量构建（跳过 hash 不变的文件）
z42c build hello.z42proj --incremental
```

### 工作区构建

```bash
# 构建所有 member
z42c build z42.z42sln

# 只构建指定 member
z42c build z42.z42sln --project apps/hello
```

### `.z42proj` → 构建产物关系

```
hello.z42proj
   │  z42c build
   ▼
   ├─ dist/
   │    ├─ Hello.zlib          ← --emit zlib（默认）
   │    └─ Hello.zmod          ← --emit zmod 时额外生成
   └─ .cache/                  ← 增量中间产物（.gitignore）
        ├─ src/greet.zbc
        └─ src/main.zbc
```

---

## 关系：`.z42proj` vs `.zmod`

| 维度 | `.z42proj` | `.zmod` |
|------|------------|---------|
| 格式 | TOML | JSON |
| 谁写 | 开发者 | 编译器生成 |
| 纳入 VCS | ✅ 是 | ❌ 通常加入 .gitignore |
| 源文件描述 | Glob 模式 | 已展开的具体路径 + hash |
| 依赖描述 | 版本约束 | 已解析的具体路径 |
| 用途 | 构建入口 | 增量编译索引 |

---

## 约定

- 每个目录最多一个 `.z42proj`
- `.z42sln` 通常放在仓库根目录
- `.cache/` 目录应加入 `.gitignore`
- `dist/` 目录应加入 `.gitignore`（CI artifacts 除外）
