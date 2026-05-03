# Design: MulticastFunc + MulticastPredicate

## Architecture

```
MulticastFunc<T, R>          MulticastPredicate<T>
─ same shape as              ─ same shape +
  MulticastAction              All(T): bool 短路
─ Invoke returns R[]         ─ Any(T): bool 短路
                             ─ Invoke returns bool[]

stdlib：复用 D2b 双 vec 模板
  Func<T,R>[] strong + bool[] strongAlive
  ISubscription<Func<T,R>>[] advanced + bool[] advancedAlive

Parser 端 SynthesizeClassEvent / SynthesizeInterfaceEvent 接受三种
multicast 类型（MulticastAction / MulticastFunc / MulticastPredicate），
按字段类型选 handler 类型：
  MulticastAction<T>    → handler Action<T>
  MulticastFunc<T, R>   → handler Func<T, R>
  MulticastPredicate<T> → handler Predicate<T>
```

## Decisions

### Decision 1: 三类 multicast 共用双 vec 模板
**决定：** MulticastFunc / Predicate 直接复制 MulticastAction.z42 双 vec 结构。
- 避免引入 base class（z42 generic + 继承组合复杂）
- 三类各自 ~190 行 z42，重复但清晰
- 未来如有 base "MulticastBase<TD>" 抽象重构，独立 spec

### Decision 2: Predicate.All / Any 真短路
**决定：** All 一旦遇 false 立即返回 false（不再 invoke 后续）；Any 一旦遇 true 立即返回 true。
- 与 LINQ 习惯一致（K10）
- 短路 = 性能优势 + 副作用避免
- COW snapshot 仍取 invoke 时全部活跃 handler，但执行时短路退出

### Decision 3: continueOnException=false 路径（D2d-2 前）
**决定：** 第一个 handler 抛异常即原样传播；前面累积的 R / bool 结果丢失（与设计 §4.2 fail-fast 一致）。

D2d-2 加 `continueOnException=true` 时改为：catch 异常存入 Failures dict + Results 空槽填 default(R) + 全跑完 → 抛 `MulticastException<R>`。

### Decision 4: SynthesizeClassEvent / Interface 校验放宽实现
**问题：** 字段类型校验从单一 MulticastAction 改为接受 3 种。
**决定：** 简单 hash set check：
```csharp
private static readonly HashSet<string> MulticastTypeNames =
    new(StringComparer.Ordinal) { "MulticastAction", "MulticastFunc", "MulticastPredicate" };

if (gt.Name 不在 MulticastTypeNames) { 报 single-cast not yet supported; }
```

handler 类型映射：
```csharp
string handlerName = gt.Name switch {
    "MulticastAction"    => "Action",
    "MulticastFunc"      => "Func",
    "MulticastPredicate" => "Predicate",
};
var handlerT = new GenericType(handlerName, gt.TypeArgs, span);
```

注意：MulticastFunc<T, R> 有 2 个 type args，`new Func<T, R>(...)` 也是 2 个 args；MulticastPredicate<T> 1 个 arg → Predicate<T> 1 个 arg。直接复用 `gt.TypeArgs` 列表即可。

## Implementation Notes

- MulticastFunc.z42：copy MulticastAction.z42，把 `void Invoke(T arg, ...)` → `R[] Invoke(T arg, ...)`，结果累积进 `R[] result`，返回 `result`
- MulticastPredicate.z42：copy MulticastAction.z42，Invoke 返回 `bool[]`；额外加 `All(T)` `Any(T)` 短路实现
- 注意 generic 数量：MulticastFunc 是 `<T, R>`，对应 `Func<T, R>`；MulticastPredicate 是 `<T>`，对应 `Predicate<T>`
- IDisposable token 类型 `MulticastSubscription<T>`：MulticastAction 已有，无法跨类型复用（z42 不支持跨类型 generic 共享）→ 各 multicast 类各自合成自己的 token 类。命名：`MulticastFuncSubscription<T,R>` / `MulticastPredicateSubscription<T>`

## Testing Strategy

- Golden test `multicast_func_predicate/source.z42`：~10 scenarios
  - MulticastFunc Subscribe + Invoke + Unsubscribe
  - MulticastPredicate All / Any 短路 + Invoke 返回数组
  - event keyword + MulticastFunc/Predicate 字段
  - interface event + MulticastFunc/Predicate 声明 + 实现
- Unit tests EventKeywordTests +2-3：parser 合成 MulticastFunc / MulticastPredicate event
- D2a / D2b / D-5 / D2c 既有 golden 全 GREEN
- IncrementalBuildIntegrationTests 39 → 41
