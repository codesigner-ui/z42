# Proposal: stdlib-package-path

## Why

stdlib 源文件已在 `src/libraries/` 下存在，但 VM 启动时完全不知道 stdlib
位置，也没有打包机制将 VM binary + stdlib 产物输出到统一目录。
不解决这个问题，M7 的 stdlib 自动加载无从落地。

## What Changes

- VM 启动时实现 `libs/` 搜索路径探测（3 条优先级规则）
- 新增 `scripts/package.sh`：将 VM binary + stdlib 占位产物打包到 `artifacts/z42/`
- stdlib 输出目录统一命名为 `libs/`（原规范写的是 `stdlib/`，此处更新）
- 每个 stdlib 模块同时提供 `.zbc` 和 `.zpkg` 两种格式（占位，M7 填充内容）

## Scope（允许改动的文件/模块）

| 文件/模块 | 变更类型 | 说明 |
|-----------|---------|------|
| `src/runtime/src/main.rs` | modify | 添加 libs 搜索路径探测 + log |
| `scripts/package.sh` | new | build + 打包到 artifacts/z42/ |
| `docs/design/stdlib.md` | modify | 更新搜索路径规范（stdlib/ → libs/） |
| `.claude/CLAUDE.md` | modify | 新增 package.sh 构建说明 |

## Out of Scope

- 实际加载 stdlib module 并 merge 进 VM（依赖 M7 `[Native]` 实现）
- 编译 `.z42` → `.zbc` / `.zpkg`（依赖 M7 `[Native]` 实现）
- `build.rs` Cargo 构建脚本（当前 stdlib 无编译产物，暂不需要）
- `~/.z42/` 用户目录安装（deferred，当前仅 `artifacts/z42/`）

## Open Questions

- [x] stdlib 目录名：`libs/`（而非原规范的 `stdlib/`）——已确认
- [x] 输出根：`artifacts/z42/`（而非 `~/.z42/`）——已确认
- [x] 格式：`.zbc` + `.zpkg` 两种——已确认
