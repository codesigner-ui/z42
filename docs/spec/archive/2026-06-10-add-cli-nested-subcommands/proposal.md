# Proposal: Std.Cli 嵌套子命令 + 每层 help

## Why

`Std.Cli` 已相当成熟（`ArgParser` 支持 flag/option/required/repeated/env-fallback/互斥组/typed getter，`SubcommandRouter` 支持单层 git/cargo 风格派发），但 **`SubcommandRouter` 明确只支持单层**（源码注释："no nested subcommands"）。

而 xtask 的命令树是 **2~3 层**（`xtask build package`、`xtask test vm`）。正因为库没有"逐层下钻 + 每层各自拦 `-h`"的机制，xtask 才不得不手撸 3 层 `if-else` 解析 —— 这直接导致 `xtask build package -h` 不显示 help 而是直接打包（[xtask_package.z42:20-28](../../../../scripts/xtask_package.z42) 的 `while` 循环静默跳过 `-h`）。

本变更是"xtask/launcher 迁移到 `Std.Cli`"（合流进 migrate-scripts-to-z42，toolchain 子系统）的**前置依赖**：先把库补齐嵌套能力，消费端迁移才有依托。

不做的后果：xtask/launcher 继续各自手写多层解析、help 文本与命令树持续漂移、每加一个子命令都要在多处重复维护 help/未知 flag 拦截。

## What Changes

- `SubcommandRouter` 新增**嵌套**能力：一个 router 节点可注册子 router（`AddRouter(name, desc, childRouter)`），与现有的叶子 `Add(name, desc, ArgParser)` 并存。
- 新增统一的解析入口 `Resolve(argv) → CommandResolution`：递归下钻整棵命令树，一次性给出三种结局之一（命中叶子 / 请求 help / 未知子命令），消费端按 `IsMatch / IsHelp / IsUnknown` 三分支处理。
- **每一层** `-h`/`--help`（及该层无参数）→ `IsHelp`，`HelpText()` 返回**当前层**的帮助（router 层列子命令；叶子层走 `ArgParser.HelpText()`）。
- **任一层**未知子命令 → `IsUnknown`，带一致的错误信息 + 当前层 help。
- 现有单层 `Match()` / `SubcommandMatch` **保留不动**（单层调用方继续可用）。
- 同步 `docs/design/stdlib/cli.md`：嵌套子命令从"不支持"改为"已落地"。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.cli/src/SubcommandRouter.z42` | MODIFY | 新增 `AddRouter` / `Resolve`；保留 `Match` / `SubcommandMatch`；子 router 存储（parallel arrays）|
| `src/libraries/z42.cli/src/CommandResolution.z42` | NEW | `Std.Cli.CommandResolution` 三态结果类（match/help/unknown）；从 SubcommandRouter.z42 拆出以守 300 行软限 |
| `src/libraries/z42.cli/tests/cli_nested_subcommand.z42` | NEW | 嵌套场景 [Test]：2~3 层命中、各层 help、各层未知子命令 |
| `src/libraries/z42.cli/README.md` | MODIFY | 文档化嵌套用法 + 入口示例 |
| `docs/design/stdlib/cli.md` | MODIFY | 特性决策表 row 1（Subcommand）更新为"嵌套已落地"；新增"嵌套子命令"小节 |

> 注：`z42.cli.z42.toml` 自动发现 `src/` + `tests/` 源文件，新增测试无需登记 manifest。
> 新测试若需 golden `.zbc`，由 stdlib 测试构建/`xtask regen` 生成（见 tasks 阶段 3）。

**只读引用**（理解上下文必须读，不修改）：

- `src/libraries/z42.cli/src/ArgParser.z42` — 叶子解析器 + `HelpText()`，嵌套叶子复用它
- `src/libraries/z42.cli/src/ParseResult.z42` — `ShowHelp()` 语义，叶子层 help 判定
- `scripts/xtask.z42` / `scripts/xtask_package.z42` — 消费端形态（验证 API 够用，不在本变更改）

## Out of Scope

- **xtask / launcher 的迁移与命令树重组**（package 提顶层、清别名、每层 help）—— 属 toolchain 子系统，合流进 migrate-scripts-to-z42，依赖本变更落地后实施。
- subcommand 别名（`co` = `commit`）、跨子命令全局 flag、子命令缩写匹配 —— 本期不做（记 Deferred）。
- "did you mean…" Levenshtein 建议在嵌套层的扩展 —— 沿用顶层现有实现即可，不为嵌套额外增强。

## Open Questions

- [ ] 无（设计决策见 design.md：保留 `Match` vs 统一到 `Resolve` —— 决定保留 `Match` 做单层 sugar，`Resolve` 为通用入口）。
