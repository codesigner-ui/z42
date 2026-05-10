# Proposal: D2a — `MulticastAction<T>` 多播基础类型

> 这是 `docs/design/delegates-events.md` D2 阶段第 1/4 切片。
> 配套：D2b ISubscription wrapper / D2c event 关键字 / D2d MulticastFunc + 异常聚合。

## Why

D1 完成单播 delegate 路径（`Action<T>` / `Func<T,R>` / `Predicate<T>` 都是命名 callable 类型 + I12 cache + stdlib 真实导出）。但**多播**（一次注册多个 handler，invoke 时全部触发）尚无对应类型 —— 用户写不了 GUI 事件式的 `button.Clicked += onClick1; button.Clicked += onClick2;`。

`delegates-events.md` §4 + K1/K2 决策：
- **类型分离设计**：单播 = `Action<T>`（编译期 delegate 类型）；多播 = `MulticastAction<T>` 独立 sealed class
- **类名约定**：多播 = `Multicast` + 单播名（`MulticastAction<T>` / `MulticastFunc<T,R>` / `MulticastPredicate<T>`）

D2a 落地多播路径的最小切片 —— `MulticastAction<T>` 单类型 + Subscribe/Unsubscribe/Invoke + COW 多线程基础。后续 D2b/c/d 在此基础上增量。

## What Changes

- **stdlib `z42.core/src/MulticastAction.z42`**：新增 `public sealed class MulticastAction<T>`
  - 私有字段：`Action<T>[] _handlers`（D2a 仅 strong vec；D2b 加 advanced vec）
  - `public int Count => _handlers.Length;`
  - `public IDisposable Subscribe(Action<T> handler)` —— 加入 vec，返回 dispose token
  - `public void Unsubscribe(Action<T> handler)` —— 移除（first match）
  - `public void Invoke(T arg, bool continueOnException = false)` —— D2a 仅实现 fail-fast 路径（`continueOnException=false`）；true 路径在 D2d 完成
- **COW snapshot**：`Invoke` 触发时拷贝当前 invocation list，遍历副本 —— `Subscribe` / `Unsubscribe` 在 invoke 期间不影响本次触发
- **stdlib `z42.core/src/IDisposable.z42`** 已存在 —— Subscribe 返回的 token 走该接口
- **零 IR / VM 变更**：`MulticastAction` 是普通 generic class，方法走现有 VCall；没有新 opcode
- **Decl in stdlib only**：D2a 不引入 `event` 关键字（D2c），用户当前需直接调 `bus.Subscribe(handler)` / `bus.Invoke(args)`

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.core/src/MulticastAction.z42` | NEW | `MulticastAction<T>` 基础多播类（构造 / Subscribe / Unsubscribe / Invoke / Count）|
| `src/libraries/z42.core/src/Disposable.z42` 或 `IDisposable.z42` | NEW or 复用 | Subscribe 返回 IDisposable token；如 stdlib 已有 IDisposable 直接复用 |
| `src/compiler/z42.Tests/MulticastActionTests.cs` | NEW | TypeCheck 验证：subscribe / unsubscribe / invoke 类型 |
| `src/runtime/tests/golden/run/multicast_action_basic/source.z42` | NEW | 端到端 golden（多 handler / 移除 / COW）|
| `src/runtime/tests/golden/run/multicast_action_basic/expected_output.txt` | NEW | golden 期望输出 |
| `src/runtime/tests/golden/run/multicast_action_basic/source.zbc` | NEW | regen 产物 |
| `examples/multicast_basic.z42` | NEW | 演示 |
| `docs/design/delegates-events.md` | MODIFY | D2a 完成标记 |
| `docs/roadmap.md` | MODIFY | 加一行 |

**只读引用**：

- `docs/design/delegates-events.md` §4.1 (`MulticastAction<T>` 类型定义) + §4.2 (调用语义) + §4.3 (COW)
- `docs/design/delegates-events.md` §9.1 (优化 A — 双 vec) — D2b 实施
- `src/libraries/z42.collections/src/LinkedList.z42` —— stdlib 类风格参考
- D1c archive — Action/Func/Predicate 注册路径

## Out of Scope

- ❌ ISubscription 体系（StrongRef / WeakRef / OnceRef / CompositeRef）—— D2b
- ❌ `event` 关键字 + `+=` / `-=` desugar —— D2c
- ❌ `MulticastException` / `continueOnException=true` 路径 —— D2d
- ❌ `MulticastFunc<T,R>` / `MulticastPredicate<T>` —— D2d
- ❌ 三个性能优化（D2b 与 ISubscription 一起做）
- ❌ COW 在 invoke 内部 add/remove 的并发线程安全 —— v1 仅做 single-thread COW snapshot；多线程 spec 未来与 concurrency.md 协同设计

## Open Questions

- [ ] `IDisposable` —— grep 是否已存在；若不存在 D2a 加一个最小版（Dispose 方法）；若存在直接复用
- [ ] `Action<T>[]` 字段类型是 `Array<Action<T>>` 还是 `List<Action<T>>`？倾向 `List<Action<T>>`（mutable，便于 add / remove）；COW 在 invoke 时 `.ToArray()` 复制成快照
- [ ] handler 比较策略：`Unsubscribe(handler)` 怎么 match？`==` reference equality 还是 method+target？建议 v1 **reference equality**（lambda 字面量两次创建的实例不等，符合 C# delegate 比较心智的"匿名 lambda 不可比"现状）
