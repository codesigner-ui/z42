# z42.cli — CLI argv parser

> 落地版本：2026-05-16（add-z42-cli）
> 包路径：`src/libraries/z42.cli/`
> 命名空间：`Std.Cli`（`CliException` 在 `Std`）

## 职责

`ArgParser` + `ParseResult` + 自动 `-h/--help` 文本生成。最小子集覆盖
flag / option / positional；为 `scripts/*.sh → *.z42` 迁移提供基础。

**对标**：Python `argparse`（最小核心）+ Rust `clap` (builder API) +
Go `flag`（命名风格）。

## API surface

```z42
class ArgParser {
    ArgParser(string programName, string description)
    void AddFlag(string longName, string shortName, string help)
    void AddOption(string longName, string shortName, string help, string defaultValue)
    void AddPositional(string name, string help)
    ParseResult Parse(string[] argv)
    string HelpText()
}

class ParseResult {
    bool   GetFlag(string longName)
    string GetOption(string longName)
    string GetPositional(int index)
    int    PositionalCount()
    bool   ShowHelp()
}

class CliException : Exception     // in Std namespace
```

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. Subcommand | 支持 / 不支持 | 不支持 | v0 简单；大多脚本无；留 follow-up |
| 2. Short + long | 都支持 / 只 long | 都支持 | UNIX 惯例；shortName 可空字符串 |
| 3. Value 形式 | `--name v` `--name=v` `-nv` | 前两种 | `-nv` 易歧义 |
| 4. 类型转换 | string + bool only / int / float | string + bool only | 通用；caller 自行 Parse |
| 5. Required mark | required / 默认值即"有" | 默认值 | 简单；caller check `== ""` |
| 6. Positional 数量 | 严格 / 宽松 | 严格 | 不匹配抛 CliException |
| 7. Unknown flag | 抛异常 / 忽略 | 抛 | fail-fast |
| 8. -h/--help | auto / 手定义 | auto | 始终 register |
| 9. 异常类 | CliException 在 Std namespace | yes | 同 UriException / RegexException 模式 |
| 10. argv[0] | program-name override / skip | skip | program name 由构造时给定 |

## 实现结构

```
src/CliException.z42  (~10 行)
└── class CliException : Exception   in Std namespace

src/ParseResult.z42   (~75 行)
└── class ParseResult — getter API + parallel arrays（与 ArgParser 共享）

src/ArgParser.z42     (~340 行)
├── declaration API   — AddFlag / AddOption / AddPositional
├── Parse(argv)       — 扫描 + 分发到 _parseLong / _parseShort
├── HelpText()        — auto-generated Usage / FLAGS / OPTIONS / ARGS
└── _ensureXxxCap     — 2x grow helpers（同 stdlib 模式）
```

## 不支持（Deferred）

### cli-future-subcommand

- **来源**：`prog sub --flag arg`（git 风格 subcommand 路由）
- **触发原因**：v0 单命令；subcommand 需要 sub-parser 注册 + dispatch
- **触发条件**：自举工具（z42c / z42vm）入口要 z42 实现时 — 它们都有 subcommand
- **当前 workaround**：调用方手动 `if argv[0] == "build"` 分支再 new sub-parser

### cli-future-required-option

- **来源**：明确标记 `--input` 必填，缺时报错
- **触发原因**：v0 用 default value="" + caller check 等价
- **当前 workaround**：检查 `r.GetOption("input") == ""` 后手抛

### cli-future-type-conversion

- **来源**：`AddIntOption / AddFloatOption / AddListOption`
- **触发原因**：v0 全 string；调用方 `int.Parse(r.GetOption("port"))` 也够
- **当前 workaround**：caller cast

### cli-future-env-fallback

- **来源**：`AddOptionWithEnv("api-key", "K", "...", "default", "API_KEY")`
- **触发原因**：常见模式（CI 环境变量传入）；v0 caller 手 `Environment.GetEnvironmentVariable`
- **当前 workaround**：caller 显式

### cli-future-mutually-exclusive

- **来源**：`--release` xor `--debug`
- **触发原因**：v0 caller 自行检查

### cli-future-repeated-option

- **来源**：`-D NAME=VALUE -D OTHER=...` 收集成 list
- **触发原因**：v0 后值覆盖前值
- **当前 workaround**：用 `,` 分隔的单 option + caller split

### cli-future-short-flag-cluster

- **来源**：`-vxf` 等价 `-v -x -f`
- **触发原因**：v0 `-vxf` 报 unknown short flag
- **当前 workaround**：分开传

### cli-future-strict-vs-extras

- **来源**：unknown flag 不抛而进 Extras 列表，让 caller pass-through
- **触发原因**：v0 严格 — fail-fast
- **当前 workaround**：caller 提前 sanitize argv

## 跨 stdlib 交互

- 依赖 `z42.core`（基础类型 + Exception）
- 依赖 `z42.text`（StringBuilder for HelpText 拼接）
- 与 `z42.io.Environment`（GetCommandLineArgs / Exit）配合：标准 main 流程
- 与 `z42.diagnostics.Log` 配合：典型 main 解析后调 `Log.SetMinLevel`

## 实施期发现

无特别 surprise。z42 `string` 字面值在 source 里用普通 `"..."`，escape 比照
JSON/C 风格。`string.Length` / `string.CharAt(i)` / `string.Substring(start, len)`
都按预期工作。
