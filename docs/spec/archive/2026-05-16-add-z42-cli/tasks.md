# Tasks: add z42.cli

> 状态：🟢 已完成 | 创建：2026-05-16 | 完成：2026-05-16 | 类型：feat（纯脚本 stdlib，无新 VM/IR）
> Spec 类型：minimal mode

## 背景

Phase 0 of "shell scripts → z42 自举"：scripts/ 目录下所有 `.sh` 现在手写 `case`
+ `getopts`，要迁移成 `.z42` 必须有统一的 CLI flag parser。对标 Python `argparse` +
Rust `clap`（最小子集）+ Go `flag`。

v0 最小：boolean flag、string option（`--name value` / `--name=value`）、positional
args、`-h/--help` 自动生成、subcommand 支持留 follow-up（多数脚本无 subcommand）。

## API Surface (v0)

```z42
namespace Std.Cli;

public class ArgParser {
    public ArgParser(string programName, string description);

    // 定义参数
    public void AddFlag(string longName, string shortName, string help);
    public void AddOption(string longName, string shortName, string help, string defaultValue);
    public void AddPositional(string name, string help);

    // 解析 argv（典型来自 Std.IO.Environment.GetCommandLineArgs()）
    public ParseResult Parse(string[] argv);

    // Help 文本（auto-generated）
    public string HelpText();
}

public class ParseResult {
    public bool   GetFlag(string longName);
    public string GetOption(string longName);
    public string GetPositional(int index);
    public int    PositionalCount();
    public bool   ShowHelp();                 // -h / --help 被传入
}

public class CliException : Exception { }     // 未知 flag / 缺 option value / positional 不足
```

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. Subcommand | (a) 支持 / (b) 不支持 | (b) | v0 简单；多数脚本无；带 subcommand 留 add-z42-cli-subcmd follow-up |
| 2. Short / long | (a) 两种都支持 / (b) 只 long | (a) | 标准 UNIX 惯例（`-v` / `--verbose`）；shortName 可空 |
| 3. Value 形式 | `--name value` / `--name=value` / `-nvalue` | 前两种 | `-nvalue` 易歧义（v + alue？）；留 follow-up |
| 4. 类型 | (a) 仅 string + bool / (b) int / float / etc. | (a) | string 通用；调用方 `int.Parse(result.GetOption("port"))` |
| 5. 必填 vs 可选 option | (a) 默认值即"有"option / (b) Required 标记 | (a) | 简单；调用方 check `result.GetOption("x") == ""` 判断 |
| 6. Positional 数量校验 | (a) 严格匹配 declared 数 / (b) 宽松（额外的进 Extras）| (a) | declared positional 个数 ≠ argv positional 个数 → CliException |
| 7. Unknown long flag | 抛 CliException | yes | fail-fast |
| 8. Auto -h/--help | (a) 自动生成 / (b) 调用方手定义 | (a) | 始终 register `-h/--help` 触发 ShowHelp() |
| 9. 异常类 | CliException 在 `Std` namespace | yes | 同 UriException / RegexException 模式 |
| 10. argv[0] | (a) 解析为 program name override / (b) skip | (b) | program name 由 ArgParser 构造时给定，argv[0] 不参与 parse |

## 阶段 1: 包骨架

- [x] 1.1 NEW `src/libraries/z42.cli/z42.cli.z42.toml` — manifest（dep on z42.core + z42.text 用于 StringBuilder 拼 help 文本）
- [x] 1.2 NEW `src/libraries/z42.cli/src/CliException.z42` — in `Std` namespace
- [x] 1.3 NEW `src/libraries/z42.cli/src/ArgParser.z42` — 定义 + Parse 实现
- [x] 1.4 NEW `src/libraries/z42.cli/src/ParseResult.z42` — getter API

## 阶段 2: 测试

- [x] 2.1 NEW `tests/cli_basic.z42` — flag + option + positional 基础
- [x] 2.2 NEW `tests/cli_help.z42` — -h / --help / HelpText 自动生成
- [x] 2.3 NEW `tests/cli_errors.z42` — unknown flag / 缺 value / 缺 positional 抛 CliException

## 阶段 3: Wiring + docs

- [x] 3.1 MODIFY `src/libraries/z42.workspace.toml` 加 `"z42.cli"`
- [x] 3.2 MODIFY `scripts/build-stdlib.sh` 加 LIBS + index.json `Std.Cli`
- [x] 3.3 NEW `src/libraries/z42.cli/README.md`
- [x] 3.4 NEW `docs/design/stdlib/cli.md`
- [x] 3.5 MODIFY `docs/design/stdlib/roadmap.md` + `organization.md` + `src/libraries/README.md`

## 阶段 4: GREEN + 归档

- [x] 4.1 `./scripts/build-stdlib.sh` 全绿
- [x] 4.2 `./scripts/test-stdlib.sh z42.cli` 全绿
- [x] 4.3 `./scripts/test-stdlib.sh` 整体不回归
- [x] 4.4 mv → `docs/spec/archive/2026-05-16-add-z42-cli/`
- [x] 4.5 commit + push
