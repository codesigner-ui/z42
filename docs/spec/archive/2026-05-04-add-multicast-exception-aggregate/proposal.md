# Proposal: D2d-2 — MulticastException + AggregateException

## Why

[delegates-events.md §7](docs/design/delegates-events.md#7-异常处理multicastexception) 设计的异常聚合模式：当 `continueOnException=true` 时，多播 Invoke 跑完所有 handler，把异常聚合后一次性抛 `MulticastException`，不让单个 handler 异常打断剩余订阅链。

D2a/D2d-1 三个 multicast 类的 `continueOnException` 参数当前未消费（fail-fast）。本 spec 落地 Action 路径的聚合行为；Func/Predicate 路径延后。

## What Changes

- **stdlib**：
  - NEW `Std.AggregateException`（继承 `Std.Exception`）—— 内部 messages + 原异常列表
  - NEW `Std.MulticastException`（继承 `Std.AggregateException`）—— 加 `Failures: Exception[]` + `FailureIndices: int[]` + `TotalHandlers: int` + `SuccessCount: int`（parallel arrays 风格，避开 IReadOnlyDictionary）
- **`MulticastAction.Invoke(continueOnException=true)`** 行为：
  - 对每个活跃 handler 包 try/catch
  - 失败的 (idx, exception) pair 累加到 collectedFailures + failedIndices
  - Invoke 结束后：0 失败 → 不抛；≥1 失败 → 抛 `new MulticastException(failures, failureIndices, totalHandlers)`
  - `continueOnException=false`（默认）保持 fail-fast 行为不变（已 ship）
- **out-of-scope**：
  - MulticastFunc / MulticastPredicate 的 continueOnException=true 实现（依赖 `MulticastException<R>` 泛型 + Results 数组占位）→ 留独立 follow-up（D-8b）
  - `MulticastException<R>` 泛型版本

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.core/src/Exceptions/AggregateException.z42` | NEW | base class extending Exception |
| `src/libraries/z42.core/src/Exceptions/MulticastException.z42` | NEW | extends AggregateException with parallel arrays |
| `src/libraries/z42.core/src/MulticastAction.z42` | MODIFY | Invoke `continueOnException=true` 路径加 try/catch + 聚合抛 |
| `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs` | MODIFY | 41 → 43（+2 文件） |
| `src/runtime/tests/golden/run/multicast_exception_aggregate/source.z42` | NEW | 端到端 |
| `src/runtime/tests/golden/run/multicast_exception_aggregate/expected_output.txt` | NEW | 预期输出 |

**只读引用**：

- `src/libraries/z42.core/src/Exception.z42` —— base class
- `src/libraries/z42.core/src/Exceptions/InvalidOperationException.z42` —— 子类参考模式

## Out of Scope（→ D-8b follow-up）

- MulticastFunc<T,R>.Invoke continueOnException=true 聚合 + Results[]
- MulticastPredicate<T>.Invoke continueOnException=true 聚合 + bool[] results
- `MulticastException<R>` 泛型版本（Results 携带 default(R) 占位）
- WeakRef wrapper（D-1）
- 单播 event（add-event-keyword-singlecast）

## Open Questions

- [ ] `Failures` 用 parallel arrays 还是单一 `(int, Exception)[]` 元组？z42 不直接支持 tuple struct → 用 parallel arrays
- [ ] AggregateException 是否需要 `InnerExceptions: Exception[]` 数组（C# 风格）？倾向 yes，便于通用 catch
