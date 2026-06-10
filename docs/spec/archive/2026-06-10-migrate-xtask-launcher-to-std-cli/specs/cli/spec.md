# Spec: xtask + launcher CLI 重组与迁移

## ADDED Requirements

### Requirement: 每层 help

xtask 与 launcher 的任意命令层级响应 `-h`/`--help`，打印该层帮助而不执行动作。

#### Scenario: xtask 叶子层 help（核心修复）
- **WHEN** `xtask package -h`（或 `xtask build runtime -h` 等任意叶子）
- **THEN** 打印 `package` 的帮助（列出 `--rid`/`--variant`/`[release|debug]`），退出 0
- **AND** 不执行打包

#### Scenario: xtask 顶层 / 中间层 help
- **WHEN** `xtask -h` / `xtask build -h` / `xtask test -h`
- **THEN** 分别打印顶层命令列表 / `build` 子命令列表 / `test` 子命令列表，退出 0

#### Scenario: launcher 每层 help
- **WHEN** `z42 run -h` / `z42 link -h` / `z42 apphost build -h`
- **THEN** 打印对应命令的 flag/positional 帮助，退出 0

#### Scenario: help 文本由库生成
- **WHEN** 任意层 help
- **THEN** 帮助来自 `Std.Cli` 的 `HelpText()`（非手写 `_help()`）—— 命令树新增/改名时帮助自动同步

### Requirement: 命令树重组

#### Scenario: package 提顶层
- **WHEN** `xtask package release --rid linux-x64`
- **THEN** 等价于旧 `xtask build package release --rid linux-x64`（同样的产物 + SHA 校验）
- **AND** `xtask build package …` 不再是有效路径（`build` 下无 `package`）

#### Scenario: feature-matrix 提顶层
- **WHEN** `xtask feature-matrix`
- **THEN** 等价于旧 `xtask build feature-matrix`（各 cargo feature combo 编译验证）

#### Scenario: 删除 lib 别名
- **WHEN** `xtask test stdlib [lib]` / `xtask bench stdlib [lib]`
- **THEN** 正常执行
- **AND** `xtask test lib` / `xtask bench lib` 不再有效（归 unknown-subcommand → 报错 + help）

### Requirement: 未知命令 / 未知 flag 一致报错

#### Scenario: 未知子命令
- **WHEN** `xtask build bogus` / `xtask bogus`
- **THEN** 退出码 2 + 错误信息含 `bogus` + 打印该层 help

#### Scenario: 未知 flag
- **WHEN** `xtask package --no-such-flag`
- **THEN** 退出码非 0 + `CliException` 一致错误信息（不再静默吞掉）

## MODIFIED Requirements

### Requirement: 参数解析机制

**Before:** 各 handler 手写 `int i = 2; while (i < argv.Length) { if (a == "--rid") … }`，未知参数处理不一致（部分静默、部分报错），help 手写。

**After:** 各 handler 经 `Std.Cli` 声明式 `ArgParser`（`AddFlag`/`AddOption`/`AddPositional`）+ 嵌套 `SubcommandRouter`/`Resolve`；未知参数统一拦截；help 由 `HelpText()` 生成。

## Behavior Invariants（行为不变 —— 兼容测试向量）

以下现有调用形式**解析与行为完全不变**（CI 依赖，逐条验证）：

| 调用 | 不变点 |
|------|--------|
| `xtask test`（空） / `xtask test --parallel` | 跑完整 gate |
| `xtask test all` | 完整 gate |
| `xtask test vm jit --jobs=4` | mode=jit + jobs=4 |
| `xtask test vm --no-rebuild` | 跳过 rebuild |
| `xtask test cross-zpkg jit` | mode=jit |
| `xtask test changed [base] --dry-run` | base-ref + dry-run；env `Z42_TEST_CHANGED_BASE` 后备；default HEAD |
| `xtask deps install --os android` | platform 校验（android/ios/wasm）|
| `xtask regen [release] --no-stdlib` | release positional + skip stdlib |
| `xtask bench --quick` / `xtask bench --diff --current … --baseline … --threshold-time … --threshold-memory … --quiet` | 各 flag 取值不变 |
| `xtask package release --rid R` | profile + rid（提顶层后路径变、行为不变）|
| `z42 run [--runtime V] <app> [-- args]` | runtime 解析 + `--` app-args 透传 + runtimeconfig.json |
| `z42 link <dir> --as <ver>` / `z42 which [--runtime V]` | positional + flag |
| `z42 <app.zpkg> [-- args]` | apphost 简写 → run |
| `z42 apphost build <app|toml> [--out P]` | 三种输入模式 + out 路径 |

退出码、env 读取、stdout/stderr 透传一律不变。
