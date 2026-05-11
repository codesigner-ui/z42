# workflow/testing/

z42 各层级测试的运行命令。**测试设计**（attribute 体系、TIDX section、runner 协议）见 [`docs/design/testing/`](../../design/testing/)。

## 测试层级

z42 测试分四层，各层独立运行：

| 层 | 跑什么 | 命令 |
|---|---|---|
| **C# 编译器单测** | xUnit on `z42.Tests/` — lexer / parser / type-check / IR-gen 单元 | [`unit-tests.md`](unit-tests.md) |
| **VM golden** | `src/tests/**/source.z42` 端到端（interp + JIT 双模） | [`vm-tests.md`](vm-tests.md) |
| **stdlib `[Test]`** | `src/libraries/<lib>/tests/*.z42` 经 z42-test-runner | [`stdlib-tests.md`](stdlib-tests.md) |
| **cross-zpkg** | 多 zpkg 协作（target lib + ext lib + main app） | [`cross-zpkg.md`](cross-zpkg.md) |

## 增量测试

[`changed-only.md`](changed-only.md) — `just test-changed` 根据 `git diff` 只跑受影响的测试命令集合（dev 内循环加速）。

## GREEN 门禁

CI 全绿门禁（`dotnet build` + `cargo build` + 上面 4 层全过）的定义见 [`../ci.md`](../ci.md)；规则在 [`.claude/rules/workflow.md`](../../../.claude/rules/workflow.md) 阶段 8。

## 一键全跑

```bash
just test        # 全部 4 层
just ci          # = build + test，CI 标准管线
```
