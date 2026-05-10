# Proposal: add-multi-exe-target

## Why

当前工程文件只支持单个可执行目标（`[project] kind = "exe" + entry`）。
一个工程中有多个可执行入口的场景很常见（如编译器 + 工具链辅助程序），
无法在一个工程文件中统一管理。

## What Changes

- `<name>.z42.toml` 新增 `[[exe]]` TOML 数组表，支持声明多个可执行目标
- 每个 `[[exe]]` 有独立的 `name`、`entry`，可选独立 `src` glob 覆盖共享 `[sources]`
- `[project] kind = "exe"` 的单目标写法**完整保留**，向后兼容
- `z42c build` 默认构建所有 `[[exe]]` 目标；`--exe <name>` 只构建指定目标
- 产物：每个 exe 独立输出到 `dist/<name>.zbc`

## Scope

| 文件 / 模块 | 变更类型 | 说明 |
|------------|---------|------|
| `docs/design/project.md` | 更新 | 新增 `[[exe]]` 语法说明 |
| `src/compiler/z42.Build/ProjectManifest.cs` | 修改 | 新增 `ExeTarget` record + 解析 `[[exe]]` |
| `src/compiler/z42.Driver/BuildCommand.cs` | 修改 | 支持多目标构建 + `--exe` flag |
| `examples/hello.z42.toml` | 更新 | 演示 `[[exe]]` 写法 |
| `src/compiler/z42.Tests/ProjectManifestTests.cs` | 新增 | 多 exe 解析测试 |

## Out of Scope

- `[[lib]]` 多库目标：结构相同，后续独立变更
- `--exe` 多目标并行构建：本次串行即可
- workspace 级跨工程目标引用：不在本阶段

## 已确认决策

| 问题 | 决定 |
|------|------|
| schema 风格 | `[[exe]]` 数组表，Cargo `[[bin]]` 风格 |
| 源文件归属 | 每个 exe 可选独立 `src` glob，未指定时继承 `[sources]` |
| 向后兼容 | `[project] kind="exe" + entry` 单目标写法完整保留 |
| 产物路径 | `dist/<exe-name>.zbc` |
| CLI | `z42c build` 构建全部；`z42c build --exe <name>` 构建指定 |
| `[[exe]]` 与 `kind="exe"` 共存 | 报错，不允许混用 |
