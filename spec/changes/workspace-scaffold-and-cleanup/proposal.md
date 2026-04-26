# Proposal: workspace 脚手架 + 清理 + WS004 移除（C4c）

## Why

C4a/C4b 完成后，workspace 工具链编译 + 查询能力齐备，但仍缺：

1. **快速搭建新 workspace / member**：用户每次都要手写完整 `z42.workspace.toml` + `<name>.z42.toml` + 目录结构
2. **清理产物**：用户需要手动 `rm -rf dist/ .cache/`
3. **manifest 格式化**：手写 toml 风格不一致
4. **WS004 残留**：C3 标记 `[Obsolete]`，C4c 阶段彻底移除

## What Changes

| 命令 | 行为 |
|---|---|
| `z42c clean` | 删除 `<workspace_root>/<out_dir>` + `<workspace_root>/<cache_dir>` |
| `z42c clean -p <name>` | 仅删单 member 产物 + cache 子目录 |
| `z42c new --workspace <dir>` | 脚手架：生成 z42.workspace.toml + .gitignore + presets/ + libs/ + apps/ 空目录 |
| `z42c new -p <name> --kind <lib\|exe>` | workspace 内新增 member（生成 manifest + src/） |
| `z42c init` | 把当前单 manifest 升级为 workspace（创建 z42.workspace.toml，原 manifest 作为单一 member） |
| `z42c fmt` | 格式化所有 *.z42.toml（字段排序 + 缩进规范） |
| **WS004 删除** | ManifestErrors.cs 移除 WS004 常量；docs/design/error-codes.md 同步删除 |

## Scope（允许改动的文件）

### 新增（NEW）

| 文件路径 | 说明 |
|---------|------|
| `src/compiler/z42.Driver/Commands/CleanCommand.cs` | clean / clean -p |
| `src/compiler/z42.Driver/Commands/NewCommand.cs` | new --workspace / new -p / init |
| `src/compiler/z42.Driver/Commands/FmtCommand.cs` | fmt manifest |
| `src/compiler/z42.Tests/CleanCommandTests.cs` | 集中清理 / per-member 清理 |
| `src/compiler/z42.Tests/NewCommandTests.cs` | 脚手架生成结构 |
| `src/compiler/z42.Tests/FmtCommandTests.cs` | 字段排序 + 缩进 |

### 修改（MODIFY）

| 文件路径 | 说明 |
|---------|------|
| `src/compiler/z42.Driver/Program.cs` | 路由 clean / new / init / fmt |
| `src/compiler/z42.Project/ManifestErrors.cs` | 移除 WS004 常量（含 [Obsolete]） |
| `docs/design/project.md` | L7 章节追加脚手架 / clean / fmt 子节 |
| `docs/design/compiler-architecture.md` | 标注 C4c 完成 |
| `docs/design/error-codes.md` | 删除 WS004 残留引用 |
| `docs/dev.md` | 脚手架 + clean 使用示例 |
| `docs/roadmap.md` | C4c 进度 + L2 阶段总进度 |

### 只读引用

| 文件路径 | 用途 |
|---------|------|
| `src/compiler/z42.Project/CentralizedBuildLayout.cs` | clean 用 EffectiveOutDir / EffectiveCacheDir |

## Out of Scope

- **`z42c add` / `z42c remove`** 操作 dependencies → future
- **`z42c update`** 类似 cargo update → 未引入版本管理前不实现
- **`z42c publish`** 远端发布 → future
- **完整 z42c 自身重写为 z42** → 自举完成前不做

## 决策记录

| # | 决策 | 选择 |
|---|---|---|
| D4c.1 | new --workspace 默认 presets 目录 | 生成 `presets/lib-defaults.toml` + `presets/exe-defaults.toml`，引导用户使用 include 机制 |
| D4c.2 | fmt 是否破坏注释 | 用 Tomlyn round-trip API 保留注释 |
| D4c.3 | init 命令的现有 manifest 处理 | 保留原 manifest 不动，仅在父目录新建 z42.workspace.toml + members 列表 |
| D4c.4 | WS004 移除时机 | C4c 阶段彻底删；提前到 C4a/b 风险增加；推迟会让 [Obsolete] 长期挂着 |
