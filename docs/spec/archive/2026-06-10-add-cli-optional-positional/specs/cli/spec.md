# Spec: Std.Cli 可选 positional

## ADDED Requirements

### Requirement: 声明可选 positional

`ArgParser.AddOptionalPositional(name, help)` 声明一个可选 positional。

#### Scenario: 可选 positional 缺省
- **WHEN** parser 仅 `AddOptionalPositional("mode", …)`，`Parse([])`
- **THEN** 不抛异常
- **AND** `GetPositional(0)` 返回 `""`
- **AND** `PositionalCount()` 返回 0

#### Scenario: 可选 positional 提供
- **WHEN** `Parse(["jit"])`
- **THEN** `GetPositional(0)` 返回 `"jit"`
- **AND** `PositionalCount()` 返回 1

#### Scenario: 必填 + 可选混合
- **WHEN** parser `AddPositional("src", …)` + `AddOptionalPositional("dst", …)`
- **WHEN** `Parse(["a"])` → `GetPositional(0)=="a"`、`GetPositional(1)==""`
- **WHEN** `Parse(["a","b"])` → `GetPositional(0)=="a"`、`GetPositional(1)=="b"`
- **WHEN** `Parse([])` → 抛 `CliException`（缺必填 `src`）

#### Scenario: 与 flag/option 混用
- **WHEN** parser `AddFlag("no-rebuild",…)` + `AddOption("jobs",…)` + `AddOptionalPositional("mode",…)`，`Parse(["jit","--jobs","4"])`
- **THEN** `GetPositional(0)=="jit"`、`GetOption("jobs")=="4"`，不抛

### Requirement: 排序约束（必填先于可选）

#### Scenario: 可选后加必填 → 声明期报错
- **WHEN** `AddOptionalPositional("a",…)` 后 `AddPositional("b",…)`
- **THEN** 抛 `CliException`（required positional 不能在 optional 之后）

### Requirement: 多余 positional 仍报错

#### Scenario: 超出声明总数
- **WHEN** 仅声明 1 个可选 positional，`Parse(["x","y"])`
- **THEN** 抛 `CliException`（unexpected positional `y`）

### Requirement: help 区分必填/可选

#### Scenario: HelpText 渲染
- **WHEN** parser `AddPositional("src",…)` + `AddOptionalPositional("dst",…)`，取 `HelpText()`
- **THEN** usage 行含 `<src>` 与 `[dst]`
- **AND** ARGS 段 `src` 标必填、`dst` 标可选（`[dst]`）

## MODIFIED Requirements

### Requirement: GetPositional 边界

**Before:** `GetPositional(i)` 当 `i >= 已提供数` 抛 out-of-range。

**After:** `GetPositional(i)` 当 `i >= 声明总数` 抛 out-of-range；`已提供数 <= i < 声明总数`（未提供的可选）返回 `""`。`PositionalCount()` 仍返回实际提供数。

## Behavior Notes（不变量）

- 现有 `AddPositional`（必填）行为不变：缺必填仍抛、`cli_required_and_typed.z42` / `cli_errors.z42` 回归绿。
- `--help` 短路仍跳过 positional 校验（含必填）。
- 多余 positional（超声明总数）仍抛，不进 `Extras()`。
