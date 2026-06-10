# Spec: Std.Cli 嵌套子命令

## ADDED Requirements

### Requirement: 嵌套子命令注册

`SubcommandRouter` 可注册子 router，形成多层命令树；叶子节点为 `ArgParser`。

#### Scenario: 注册子 router
- **WHEN** 调用 `parent.AddRouter("build", "build components", buildRouter)`
- **THEN** `"build"` 成为 `parent` 的一个子命令，其下钻目标是 `buildRouter`
- **AND** `parent.Has("build")` 返回 `true`

#### Scenario: 叶子与子 router 并存
- **WHEN** 同一 router 上既有 `Add("audit", ..., auditParser)`（叶子）又有 `AddRouter("build", ..., buildRouter)`（子树）
- **THEN** 两者都可被 `Resolve` 命中，按 token 区分

#### Scenario: 同名覆盖
- **WHEN** 先 `Add("x", ..., p)` 后 `AddRouter("x", ..., r)`（或反向）
- **THEN** 后注册者覆盖前者（与现有 `Add` 覆盖语义一致）

### Requirement: 逐层下钻解析

`Resolve(argv)` 递归消费 argv 前缀，下钻到叶子 `ArgParser` 解析剩余参数。

#### Scenario: 两层命中叶子
- **WHEN** `Resolve(["build", "package", "--rid", "linux-x64"])`，`build`→router、其下 `package`→ArgParser（声明了 `--rid` option）
- **THEN** 结果 `IsMatch()` 为 `true`
- **AND** `Path()` 等于 `["build", "package"]`
- **AND** `Result().GetOption("rid")` 等于 `"linux-x64"`

#### Scenario: 单层命中叶子（退化情形）
- **WHEN** `Resolve(["audit"])`，`audit`→ArgParser
- **THEN** `IsMatch()` 为 `true`，`Path()` 等于 `["audit"]`

#### Scenario: 三层命中
- **WHEN** 命令树为 `a`→router、`a b`→router、`a b c`→ArgParser，`Resolve(["a","b","c","--flag"])`
- **THEN** `IsMatch()`，`Path()` 等于 `["a","b","c"]`，`Result().GetFlag("flag")` 为 `true`

### Requirement: 每层 help

任一层遇 `-h`/`--help`，或 router 层无后续 token，归为 help 请求；`HelpText()` 给出该层帮助。

#### Scenario: 顶层 help
- **WHEN** `Resolve(["-h"])` 或 `Resolve([])`（空 argv）
- **THEN** `IsHelp()` 为 `true`
- **AND** `HelpText()` 为顶层 router 的帮助（列出顶层子命令）

#### Scenario: 中间层 help
- **WHEN** `Resolve(["build", "-h"])`，`build`→router
- **THEN** `IsHelp()` 为 `true`
- **AND** `HelpText()` 为 `build` 子 router 的帮助（列出 `package`/`runtime`/… 等）
- **AND** `Path()` 等于 `["build"]`

#### Scenario: 叶子层 help（核心修复场景）
- **WHEN** `Resolve(["build", "package", "-h"])`，`package`→ArgParser
- **THEN** `IsHelp()` 为 `true`（不进入打包逻辑）
- **AND** `HelpText()` 为 `package` 叶子 `ArgParser.HelpText()`（列出 `--rid` 等 option）
- **AND** `Path()` 等于 `["build", "package"]`

#### Scenario: router 层无后续 token
- **WHEN** `Resolve(["build"])`，`build`→router（其下还有子命令，未指定）
- **THEN** `IsHelp()` 为 `true`，`HelpText()` 为 `build` 子 router 帮助

### Requirement: 每层未知子命令报错

任一 router 层遇到不认识的 token（非 `-h`），归为 unknown。

#### Scenario: 顶层未知命令
- **WHEN** `Resolve(["bogus"])`，顶层无 `bogus`
- **THEN** `IsUnknown()` 为 `true`
- **AND** `ErrorMessage()` 含 `"bogus"`
- **AND** `HelpText()` 为顶层帮助

#### Scenario: 中间层未知子命令
- **WHEN** `Resolve(["build", "bogus"])`，`build`→router 下无 `bogus`
- **THEN** `IsUnknown()` 为 `true`
- **AND** `Path()` 等于 `["build"]`（已下钻到的层）
- **AND** `HelpText()` 为 `build` 子 router 帮助

### Requirement: 结果三态互斥

`CommandResolution` 的 `IsMatch()` / `IsHelp()` / `IsUnknown()` 恰有一个为 `true`。

#### Scenario: 互斥性
- **WHEN** 任意 `Resolve(argv)` 返回 `res`
- **THEN** `IsMatch()` + `IsHelp()` + `IsUnknown()` 中恰好一个为 `true`
- **AND** `IsMatch()` 时 `Result()` 非空；`IsHelp()`/`IsUnknown()` 时 `HelpText()` 非空字符串

## Behavior Notes（不变量）

- 现有 `Match()` / `SubcommandMatch` 行为不变：单层调用方不受影响（回归用现有 `cli_subcommand.z42` 覆盖）。
- 叶子层的 option/positional/required 校验仍由 `ArgParser.Parse` 负责；`Resolve` 只做"路由 + help/unknown 分流"，不重复 `ArgParser` 的参数语义。
- 叶子层 `-h` 经由 `ArgParser` 既有 `ShowHelp()` 机制识别，`Resolve` 据此归为 `IsHelp` 并取叶子 `HelpText()`，与 router 层 help 在消费端表现一致（同一 `IsHelp` 分支）。
