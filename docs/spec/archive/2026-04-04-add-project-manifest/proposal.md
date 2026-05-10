# Proposal: add-project-manifest

## Why

当前编译器只支持单文件模式（`z42c file.z42 --emit ...`），没有工程概念：
- 无法一条命令编译多个 `.z42` 文件
- 构建参数（输出目录、执行模式、优化级别）只能通过 CLI flag 临时指定，无法持久化
- 没有依赖声明机制，第三方库引用无从表达
- 没有 debug / release 分离的构建配置

需要一个工程文件作为构建入口，让 `z42c build` 能驱动完整的多文件构建流程。

## What Changes

- 定义 `<name>.z42.toml` 文件格式（TOML），作为 z42 工程的统一配置入口
- 编译器 Driver 新增 `build` 子命令，读取工程文件驱动构建
- 明确区分两种编译模式：**项目模式** 和 **单文件模式**，职责不同，互不干扰

## CLI 设计：两种模式

### 项目模式（Project Mode）

**目的**：构建完整工程，用于正式开发和交付。

```bash
# 在含 <name>.z42.toml 的目录下执行
z42c build                          # 读取工程文件，使用 profile.debug
z42c build --release                # 使用 profile.release
z42c build --profile staging        # 使用自定义 profile
z42c build hello.z42.toml           # 显式指定工程文件
z42c build --emit zbc               # 覆盖工程文件中的 emit 设置
```

**行为规则：**
- 当前目录只有一个 `.z42.toml` 文件 → 自动选中
- 当前目录有多个 `.z42.toml` 文件 → 报错，要求显式指定：`z42c build <name>.z42.toml`
- 当前目录没有 `.z42.toml` 文件 → 报错：`error: no .z42.toml found in current directory`

**输入**：`<name>.z42.toml` 工程文件
**输出**：产物写入 `[build].out_dir`（默认 `dist/`），中间文件写入 `.cache/`

---

### 单文件模式（Single-file Mode）

**目的**：快速编译 / 调试单个文件，用于探索、原型和工具链开发，不需要工程文件。

```bash
# 当前行为，完整保留
z42c <file.z42>                     # 编译单文件，默认 --emit ir
z42c <file.z42> --emit zbc          # 指定产物格式
z42c <file.z42> --dump-tokens       # 调试：查看 token 流
z42c <file.z42> --dump-ast          # 调试：查看 AST
z42c <file.z42> --dump-ir           # 调试：查看 IR
z42c <file.z42> --out <path>        # 指定输出路径
```

**行为规则：**
- 不读取任何 `.z42.toml` 文件，即使当前目录有也忽略
- 产物默认输出到与源文件同目录

---

### 两种模式对比

| 维度 | 项目模式 `z42c build` | 单文件模式 `z42c <file>` |
|------|----------------------|------------------------|
| 目的 | 正式构建 / 交付 | 快速编译 / 调试探索 |
| 配置来源 | `<name>.z42.toml` | CLI flags |
| 多文件支持 | ✅ glob include/exclude | ❌ 仅单文件 |
| 增量编译 | ✅ | ❌ |
| Profile | ✅ debug / release | ❌ |
| 输出目录 | `dist/`（可配置）| 源文件同目录 |
| 调试 flags | ❌（用单文件模式）| ✅ --dump-* |
| 工程文件 | 必须 | 不需要也不读取 |

---

## Scope（允许改动的文件/模块）

| 文件 / 模块 | 变更类型 | 说明 |
|------------|---------|------|
| `docs/design/project.md` | 更新 | 补充 CLI 设计，更新文件名为 `<name>.z42.toml` |
| `src/compiler/z42.Driver/Program.cs` | 修改 | 新增 `build` 子命令入口，更新 help 文本 |
| `src/compiler/z42.Driver/ProjectManifest.cs` | 新增 | `<name>.z42.toml` 读取与反序列化 |
| `src/compiler/z42.Driver/BuildCommand.cs` | 新增 | `build` 子命令实现（多文件编译、profile 选择）|
| `examples/hello.z42.toml` | 新增/更新 | 对齐最终 schema（原 `z42.toml` 重命名）|
| `src/compiler/z42.Tests/` | 新增 | ProjectManifest 解析测试 |

## Out of Scope

- L5 依赖解析（`[dependencies]`）：本次只解析字段，不实现依赖加载
- L6 工作区（`[workspace]`）：本次只解析字段，不实现多成员构建
- 注册中心 / 包下载：不在本阶段
- `z42c new` / `z42c init` 脚手架命令：不在本阶段

## 已确认决策

| 问题 | 决定 |
|------|------|
| `[project]` vs `[package]` | 保留 `[project]`；`package` 留给未来包体概念 |
| 文件命名 | `<name>.z42.toml`，`.z42.toml` 为后缀，项目名为前缀 |
| 默认 profile | `z42c build` = debug；`z42c build --release` = release |
| 多个 `.z42.toml` 歧义 | 报错，要求显式指定：`z42c build <name>.z42.toml` |
| 找不到工程文件 | 报错退出；单文件模式完整保留，两种模式职责分离 |
| `[project].name` | 可从文件名推断（`hello.z42.toml` → name = `hello`），字段变可选 |
| help 文本 | 明确展示两种模式的分工和各自支持的 flags |
