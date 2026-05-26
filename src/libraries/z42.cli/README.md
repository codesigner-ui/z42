# z42.cli

## 职责
CLI argv 解析器 — flag / option / positional + auto `-h/--help` 文本生成。
对标 Python `argparse` + Rust `clap` + Go `flag`（最小子集）。

**用途**：脚本类 z42 程序解析命令行参数。Phase 0 基础设施之一，为
`scripts/*.sh → *.z42` 迁移提供 argv parser。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/ArgParser.z42`   | `Std.Cli.ArgParser` — 注册 + Parse + HelpText |
| `src/ParseResult.z42` | `Std.Cli.ParseResult` — GetFlag / GetOption / GetPositional / ShowHelp |
| `src/CliException.z42`| `Std.CliException`（未知 flag / 缺 value / 缺 positional） |
| `src/SubcommandRouter.z42` | `Std.Cli.SubcommandRouter` + `SubcommandMatch` — git/cargo 风格 `Add(name, desc, ArgParser)` + `Match(argv)` 派发；`HelpText()` 顶层帮助 |

## 入口点

```z42
using Std.Cli;
using Std.IO;

void Main() {
    var p = new ArgParser("build-stdlib", "Compile z42 stdlib workspace");
    p.AddFlag("verbose", "v", "verbose output");
    p.AddOption("profile", "p", "build profile", "release");
    p.AddPositional("target", "build target");

    ParseResult r;
    try {
        r = p.Parse(Environment.GetCommandLineArgs());
    } catch (CliException e) {
        ConsoleError.WriteLine("error: " + e.Message);
        Environment.Exit(2);
    }

    if (r.ShowHelp()) {
        Console.WriteLine(p.HelpText());
        Environment.Exit(0);
    }

    bool verbose  = r.GetFlag("verbose");
    string prof   = r.GetOption("profile");
    string target = r.GetPositional(0);
    // ...
}
```

## 支持的 argv 形式

| 形式 | 含义 |
|------|------|
| `--flag` / `-f` | boolean flag → true |
| `--name value` | option with value |
| `--name=value` | option with value (inline) |
| `-n value`     | short-name option |
| `positional1 positional2 ...` | positional 按声明顺序匹配 |
| `-h` / `--help` | 自动 register；触发 `ShowHelp() == true`，跳过 positional 校验 |

## 错误情况（抛 CliException）

- 未知 long flag (`--bogus`)
- 未知 short flag (`-x`)
- option 缺 value (`--profile` 末尾无 value)
- 给 flag 传 value (`--verbose=yes`)
- positional 数量不足（除非 `--help`）
- positional 数量超出 declared
- `GetFlag("undeclared")` / `GetOption("undeclared")` / `GetPositional(超出范围)`

## 依赖关系
依赖 `z42.core`（基础类型 + Exception）+ `z42.text`（StringBuilder 用于
HelpText 拼接）。

## 不在本期 Scope（详 `docs/design/stdlib/cli.md` Deferred）

- subcommand（`prog sub --flag`）
- required option 标记
- 类型转换（int / float / list）— 调用方自己 `int.Parse(r.GetOption("port"))`
- `-nvalue` 紧贴形式（易歧义）
- env-var fallback（`if not on cmdline, fall back to $X`）
- mutually-exclusive group
- 可重复 option（list value）
- short flag 串联（`-vxf`）
