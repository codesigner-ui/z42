# Design: Std.Cli 可选 positional

## Decisions

### Decision 1: parallel `_positionalOptional: bool[]`

沿用本库既有 parallel-array 模式（`_optionRequired` 等）。`AddPositional` 写 `false`，`AddOptionalPositional` 写 `true`。`EnsurePositionalCap` 同步扩容该数组。

### Decision 2: 排序约束在声明期强制

`AddPositional`（必填）若发现已存在任一 optional positional → 抛 `CliException("required positional '<name>' cannot follow an optional positional")`。这保证"必填全在前、可选全在后"，使 Parse 的必填校验可简化为"提供数 ≥ requiredCount"（requiredCount = `!optional` 的个数 = 首个 optional 的下标）。

### Decision 3: Parse 必填校验改用 requiredCount

`Parse` 末尾的缺-positional 校验：
```
int requiredCount = 0;
while (requiredCount < this._positionalCount && !this._positionalOptional[requiredCount]) {
    requiredCount = requiredCount + 1;
}
if (!r._showHelp && r._positionalCount < requiredCount) {
    throw new CliException("missing positional argument: '" +
        this._positionalNames[r._positionalCount] + "'");
}
```
扫描阶段（填 positional）与"多余报错"（line 300，`>= 声明总数` 抛）不变 —— 可选不放宽上限。

### Decision 4: ParseResult.GetPositional 边界放宽

`Parse` 初始化 `r._positionalValues` 为**声明总数**长度、全 `""`（现状已如此），仅 `r._positionalCount` 记实际提供数。`GetPositional(i)` 边界改用**声明总数**（即 `_positionalValues.Length`）而非 `_positionalCount`：
```
public string GetPositional(int index) {
    if (index < 0 || index >= this._positionalValues.Length) {
        throw new CliException("positional index " + index.ToString() +
            " out of range (declared " + this._positionalValues.Length.ToString() + ")");
    }
    return this._positionalValues[index];
}
```
未提供的可选 → 槽位默认 `""`。`PositionalCount()` 不变（返回提供数）。

> 对现有严格用法零影响：所有 positional 必填时，成功 Parse 后"提供数 == 声明数"，边界一致；越界（≥ 声明数）仍抛。`cli_errors.z42::test_positional_out_of_range_throws`（1 声明、取 index 5）仍抛。

### Decision 5: HelpText 区分 `<req>` / `[opt]`

usage 行与 ARGS 段按 `_positionalOptional[i]` 渲染：必填 `<name>`、可选 `[name]`。

## Implementation Notes

- `ParseResult` 需访问声明的可选性？不需要 —— GetPositional 只用 `_positionalValues.Length` + 默认 `""`，可选性判断全在 ArgParser 侧（声明校验 + Parse 必填校验 + HelpText）。ParseResult 仅改 GetPositional 边界。
- 行数：ArgParser.z42 现 ~685 行（已超软限，是既有状态；本次仅小增量，不触发新拆分义务，但避免显著增长）。新增 `AddOptionalPositional`（~10 行）+ 校验调整（~6 行）+ HelpText 分支（~6 行）。
- 验证经 fresh 0.15 vm 旁路（repo apphost 陈旧），同 add-cli-nested-subcommands。

## Testing Strategy

- `tests/cli_optional_positional.z42`（NEW [Test]）：覆盖 spec 全场景 —— 缺/有可选、必填+可选混合、与 flag 混用、排序违规声明报错、多余报错、help 含 `<src>`/`[dst]`。
- 回归：`cli_required_and_typed.z42` / `cli_errors.z42` / `cli_help.z42` 仍绿（严格 positional 零影响）。
- `z42.cli` lib test 全绿 + 完整 GREEN gate。
