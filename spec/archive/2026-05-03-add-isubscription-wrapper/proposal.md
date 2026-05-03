# Proposal: D2b — ISubscription wrapper 体系（StrongRef / OnceRef / CompositeRef + 双 vec 优化）

> 这是 `docs/design/delegates-events.md` D2 阶段第 2/4 切片，紧跟 D2a。
> 配套：D2c event 关键字 / D2d MulticastFunc + 异常聚合。

## Why

D2a 落地了 `MulticastAction<T>` 基础多播 —— 用户可 `Subscribe(handler)` + Dispose token 取消。但没有"订阅策略"概念：

- 想要"一次性订阅"（触发后自动解绑）—— 用户得手动写 `var t = bus.Subscribe(...); ... t.Dispose();` 包装
- 想要"弱引用订阅"（避免 GUI 长寿对象持回调导致内存泄漏）—— 没办法
- 未来 throttle / debounce / scheduler dispatch 等策略 —— 没扩展点

`delegates-events.md` §5 设计的 `ISubscription` wrapper 体系：所有订阅策略由订阅者用 wrapper 类表达，**零新关键字 / 零新 attribute**。

## What Changes

- **stdlib `Std.ISubscription<TDelegate>` interface**：定义 `TryGet()` / `IsAlive` / `OnInvoked()` 协议
- **stdlib wrapper 类**：
  - `StrongRef<TDelegate>` — 强引用包装（默认；裸 handler 自动转此；fast path 通常不真实例化）
  - `OnceRef<TDelegate>` — 一次性包装；`OnInvoked()` 第一次后 `IsAlive=false`
  - `CompositeRef<TDelegate>` — flags 组合包装（v1 仅 `Once` flag；`Weak` 等待 D2b-followup）
- **stdlib impl 扩展**：`Action<T>.AsOnce()` 等扩展方法（impl 块）
- **`MulticastAction<T>.Subscribe(ISubscription<Action<T>>)` 重载**：advanced 路径
- **优化 A — 双 vec 通道**：`MulticastAction<T>` 内部分 strong（`Action<T>[]`）+ advanced（`ISubscription<Action<T>>[]`）；invoke fast loop + slow loop 串行
- **优化 B — Composite 融合**：`x.AsOnce().AsOnce()` 等链式不重复包装；累加 flag 复用对象
- **优化 C — 跳过无状态 OnInvoked**：`StrongRef.OnInvoked()` 是 nop；invoke loop 用 mode flag 短路
- **WeakRef 延后**：design.md 中 `WeakRef<T>` 需要 GC 弱引用 builtin（`make_weak`/`upgrade_weak` 已在 heap trait，但未暴露到 corelib）。D2b v1 不实现；记入 follow-up

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.core/src/ISubscription.z42` | NEW | interface 定义 |
| `src/libraries/z42.core/src/SubscriptionRefs.z42` | NEW | StrongRef / OnceRef / CompositeRef 三个 sealed class（含 Mode flag enum） |
| `src/libraries/z42.core/src/MulticastAction.z42` | MODIFY | 加 `Subscribe(ISubscription<Action<T>>)` 重载 + `advanced` 字段 + invoke 双 loop |
| `src/compiler/z42.Tests/SubscriptionRefsTests.cs` | NEW | TypeCheck + 行为单元测试 |
| `src/runtime/tests/golden/run/multicast_subscription_refs/` | NEW | 端到端 golden（OnceRef / Composite / fast/slow 通道）|
| `examples/multicast_subscription.z42` | NEW | 演示 |
| `docs/design/delegates-events.md` | MODIFY | D2b 完成标记；§5 调整说明 WeakRef 延后 |
| `docs/roadmap.md` | MODIFY | 加一行 |

**只读引用**：

- `delegates-events.md` §5（接口定义）+ §9.1-9.3（三个性能优化）
- `src/libraries/z42.core/src/MulticastAction.z42` —— D2a 基础结构

## Out of Scope

- ❌ `WeakRef<TDelegate>` —— GC 弱引用 builtin 未暴露；记 follow-up `expose-weak-ref-builtin`
- ❌ `event` 关键字 + `+=` / `-=` desugar —— D2c
- ❌ `MulticastFunc<T,R>` / `MulticastPredicate<T>` —— D2d
- ❌ `MulticastException` / `continueOnException=true` —— D2d
- ❌ Throttled / Debounced / OnUiThread 等高级 wrapper —— delegates-events.md §5.5 列为前瞻；当前不实现
- ❌ TDelegate 约束 `where T : Delegate`（z42 现无此约束概念；ISubscription 暂无 where 约束，靠用户传入 delegate 类型）

## Open Questions

- [ ] **Mode flag 是 enum 还是 int?**：design.md 用 `[Flags] Mode` enum；z42 enum 暂不支持位运算。建议 v1 用 `int` 常量（`Mode_None=0`, `Mode_Once=1`, `Mode_Weak=2`)；future 升级为 enum 后兼容
- [ ] **CompositeRef 持 inner ISubscription vs 持原始 delegate**：design.md `WithMode(Mode additional)` 暗示嵌套同一个 CompositeRef 复用对象。v1 设计：CompositeRef 持 `delegate handler + Mode modes + bool consumed`，AsOnce/AsWeak chain 检测同类则累加 modes 不新建实例
- [ ] **`AsOnce()` 是 impl 扩展方法还是 ISubscription 静态方法**：v1 仅在 `Action<T>` 上加 impl 块定义 `AsOnce()`；ISubscription 实例的 `.AsOnce()` 通过统一 chain 协议（CompositeRef 内部 fold）
