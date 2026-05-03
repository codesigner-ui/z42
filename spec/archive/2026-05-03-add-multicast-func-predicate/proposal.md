# Proposal: D2d-1 — MulticastFunc + MulticastPredicate

## Why

[delegates-events.md §4.1](docs/design/delegates-events.md#41-类型定义) 设计的 `Std.MulticastFunc<T,R>` / `Std.MulticastPredicate<T>` 是与 `MulticastAction<T>` 严格对称的多播体系（K2）：

- `MulticastFunc<T, R>` —— 多播 `Func<T, R>` handler，Invoke 返回 `R[]`
- `MulticastPredicate<T>` —— 多播 `Predicate<T>` handler，Invoke 返回 `bool[]`，加 `All` / `Any` 短路求值

D2c-多播 + interface 已 ship；event keyword 当前只接受 `event MulticastAction<T>`，本 spec 加入 MulticastFunc / Predicate 后类型校验需放宽。

异常聚合（`continueOnException=true` → `MulticastException<R>`）拆到 D2d-2。

## What Changes

- **stdlib**：
  - NEW `Std.MulticastFunc<T, R>` —— 双 vec strong / advanced 路径（同 MulticastAction 模板）
    - `Subscribe(Func<T,R>) / SubscribeAdvanced(ISubscription<Func<T,R>>) / Unsubscribe(Func<T,R>)`
    - `Invoke(T arg, bool continueOnException=false): R[]` —— first-throw-wins（异常聚合 D2d-2）
    - `Count()`
  - NEW `Std.MulticastPredicate<T>` —— 同上 + `All(T)` / `Any(T)` 短路
  - 复用 `ISubscription<Func<T,R>>` / `ISubscription<Predicate<T>>` 接口（D2b 已 ship 通用 wrapper）
- **Parser**：
  - `SynthesizeClassEvent` / `SynthesizeInterfaceEvent` 类型校验放宽：接受 `GenericType("MulticastAction"|"MulticastFunc"|"MulticastPredicate", _)`
  - 合成 add_X / remove_X 时按字段类型决定 handler 参数类型：
    - `MulticastAction<T>` → handler 类型 `Action<T>`
    - `MulticastFunc<T, R>` → handler 类型 `Func<T, R>`
    - `MulticastPredicate<T>` → handler 类型 `Predicate<T>`

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.core/src/MulticastFunc.z42` | NEW | 多播 Func 双 vec |
| `src/libraries/z42.core/src/MulticastPredicate.z42` | NEW | 多播 Predicate + All/Any |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Members.cs` | MODIFY | `SynthesizeClassEvent` / `SynthesizeInterfaceEvent` 接受三种 multicast 类型；按 multicast 类型选 handler 类型 |
| `src/compiler/z42.Tests/EventKeywordTests.cs` | MODIFY | 加 MulticastFunc / Predicate event 测试 |
| `src/runtime/tests/golden/run/multicast_func_predicate/source.z42` | NEW | 端到端 golden |
| `src/runtime/tests/golden/run/multicast_func_predicate/expected_output.txt` | NEW | 预期输出 |
| `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs` | MODIFY | z42.core 39 → 41（+2 文件） |

**只读引用**：

- `src/libraries/z42.core/src/MulticastAction.z42` —— 双 vec 模板
- `src/libraries/z42.core/src/SubscriptionRefs.z42` —— ISubscription wrapper
- `src/libraries/z42.core/src/Delegates.z42` —— Func / Predicate delegate 类型

## Out of Scope

- `MulticastException` 异常聚合 → D2d-2
- `continueOnException=true` 实现（参数当前未消费） → D2d-2
- WeakRef wrapper → D-1 batch
- 单播 event → 后续 spec

## Open Questions

- [ ] `Predicate<T>.Any` 短路：实施时先 evaluate 全部然后 reduce，还是真短路？倾向真短路（性能 + 与 LINQ 一致）
- [ ] `MulticastFunc<T,R>.Invoke` 返回 `R[]` 长度 = 活跃 handler 数（含 wrapper 通道）or 总注册数？倾向活跃数（与 Count() 一致）
