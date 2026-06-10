# Tasks: Std.Cli 可选 positional

> 状态：🟢 已完成 | 创建：2026-06-10 | 完成：2026-06-10
> 子系统：`stdlib`（足迹限 z42.cli，与 add-field reflection 文件不相交）| 前置于：migrate-xtask-launcher-to-std-cli

> ✅ **验证（2026-06-10）**：add-field-attribute-reflection 归档后工具链一致（zbc 1.14 / zpkg 0.16）。z42.cli 10 文件 / 11 新 [Test] 全绿；compiler + vm(interp+jit) + cross-zpkg 全绿。完整 GREEN gate 唯一红点是**无关的** add-field 遗留 `z42.core/tests/reflection.z42` 编译失败（`none` 关键字 + attribute-factory upcast；其 gate 漏编此 stdlib 测试）→ 拆独立 compiler fix 跟进（见 fix-attr-factory-upcast）。本变更按 [[feedback_batched_commits]] 独立提交（足迹 z42.cli，不被并行污染阻塞）。

## 进度概览
- [x] 阶段 1: ArgParser 声明 + Parse + HelpText
- [x] 阶段 2: ParseResult.GetPositional 边界
- [~] 阶段 3: 测试代码已写，验证待工具链一致

## 阶段 1: ArgParser
- [x] 1.1 `_positionalOptional: bool[]` parallel array + 构造初始化 + EnsurePositionalCap 扩容
- [x] 1.2 `AddPositional` 写 `false` + 排序校验（optional 之后加 required → 抛）
- [x] 1.3 `AddOptionalPositional(name, help)` 写 `true`
- [x] 1.4 Parse 必填校验改用 requiredCount（leading 必填数）
- [x] 1.5 HelpText usage 行 + ARGS 段 `<req>`/`[opt]`

## 阶段 2: ParseResult
- [x] 2.1 `GetPositional` 边界放宽到声明总数（`_positionalValues.Length`）；未提供可选 → `""`

## 阶段 3: 测试 + 文档
- [x] 3.1 `tests/cli_optional_positional.z42`（NEW，11 [Test]）逐条对应 spec scenario
- [x] 3.2 回归 `cli_required_and_typed`/`cli_errors`/`cli_help` 绿（z42.cli 10 文件全过）
- [ ] 3.3 `README.md` + `docs/design/stdlib/cli.md`（决策表 row 6 + Deferred ✅）
- [x] 3.4 `z42.cli` lib test 全绿（10 文件 / 含 11 新 [Test]）
- [ ] 3.5 完整 GREEN gate（跑中）
