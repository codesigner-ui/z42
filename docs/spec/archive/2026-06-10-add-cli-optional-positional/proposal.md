# Proposal: Std.Cli 可选 positional

## Why

`Std.Cli.ArgParser` 的 positional 全部**严格必填**（[ArgParser.z42:311](../../../../src/libraries/z42.cli/src/ArgParser.z42) 缺则抛、[:300](../../../../src/libraries/z42.cli/src/ArgParser.z42) 多则抛，`AllowExtras` 不覆盖 positional）。没有"可选 positional"。

xtask/launcher 迁移到 Std.Cli（migrate-xtask-launcher-to-std-cli）时发现：其命令遍地是可选 positional —— `test vm [interp|jit]`、`build stdlib [lib]`、`package [release|debug]`、`clean [tests|bench|all]`、`regen [release]`、`test changed [base]`、`z42 default [version]` 等。用严格 positional 建 leaf ArgParser 会让 `test vm jit --jobs=4` 的 `jit` 抛 "unexpected positional"——而这是 CI 在用的形式，不能改成 flag。

这是 dogfood 暴露的库缺口（[[feedback_dogfood_fill_gaps]]：发现缺口就实现，不 work around）。是 migrate-xtask-launcher-to-std-cli 的**前置依赖**。

## What Changes

- `ArgParser.AddOptionalPositional(name, help)`：声明可选 positional。缺省时 `GetPositional(i)` 返回 `""`；多余仍报错（数量上限 = 声明总数）。
- **排序约束**：可选 positional 必须在所有必填 positional **之后**（argparse 惯例）；违反在声明期抛 `CliException`。
- `Parse` 的"缺 positional"校验只对**必填**触发（缺可选不报错）。
- `ParseResult.GetPositional(i)`：边界从"已提供数"放宽到"声明总数"——已声明但未提供的可选 positional 返回 `""`（`PositionalCount()` 仍返回实际提供数）。
- `HelpText()` usage 行 + ARGS 段：可选标 `[name]`、必填标 `<name>`。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.cli/src/ArgParser.z42` | MODIFY | `_positionalOptional` parallel array + `AddOptionalPositional` + 排序校验 + Parse 必填校验调整 + HelpText `[name]`/`<name>` |
| `src/libraries/z42.cli/src/ParseResult.z42` | MODIFY | `GetPositional` 边界放宽到声明总数（未提供可选 → `""`）|
| `src/libraries/z42.cli/tests/cli_optional_positional.z42` | NEW | [Test]：可选缺/有、必填+可选混合、排序违规、多余报错、help 渲染 |
| `src/libraries/z42.cli/README.md` | MODIFY | 文档化可选 positional |
| `docs/design/stdlib/cli.md` | MODIFY | 决策表 row 6（Positional）+ Deferred ✅ 小节 |

**只读引用**：`tests/cli_required_and_typed.z42`、`tests/cli_help.z42`（对齐既有断言风格）。

## Out of Scope

- 变长 positional（`args...` 收集剩余为 list）—— 另议，记 Deferred。
- xtask/launcher 的实际迁移 —— 属 migrate-xtask-launcher-to-std-cli（本变更解锁它）。

## Open Questions

- [ ] 无（排序约束=必填先于可选，沿用 argparse；变长 positional 明确 Deferred）。
