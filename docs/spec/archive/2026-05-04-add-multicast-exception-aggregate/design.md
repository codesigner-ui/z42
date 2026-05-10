# Design: MulticastException 聚合（Action 路径）

## Architecture

```
Std.Exception (existing)
   │
   └─> Std.AggregateException                      ← NEW
           ├ public Exception[] InnerExceptions
           └─> Std.MulticastException              ← NEW
                   ├ public Exception[] Failures
                   ├ public int[] FailureIndices
                   ├ public int TotalHandlers
                   └ public int SuccessCount() => TotalHandlers - Failures.Length

MulticastAction<T>.Invoke(arg, continueOnException=true):
  if continueOnException:
    failedExceptions = []
    failedIndices = []
    total = 0
    for each handler in COW snapshot:
      total++
      try { handler(arg); }
      catch (Exception e) {
        failedExceptions.append(e)
        failedIndices.append(handlerIdx)
      }
    if failedExceptions.length > 0:
      throw new MulticastException(failedExceptions, failedIndices, total);
  else:
    // existing fail-fast
```

## Decisions

### Decision 1: parallel arrays vs Dictionary
**问题：** Failures 用什么容器？设计 line 367 说 `IReadOnlyDictionary<int, Exception>`。
**选项：**
- A. Dictionary<int, Exception>（z42.collections.Dictionary）—— 但 stdlib z42.core 不依赖 z42.collections
- B. parallel arrays (Exception[] + int[])
- C. 自定义 KeyValuePair[] 数组

**决定：B（parallel arrays）。** 原因：
- z42.core 自包含，不引 z42.collections 依赖
- 并行数组语义清楚，索引一致性靠构造时校验
- 用户用 LINQ 折叠两数组也方便

### Decision 2: AggregateException 是否独立或仅 MulticastException
**问题：** 是否需要 AggregateException 基类？
**决定：是**。原因：
- 设计明确（line 366）—— MulticastException : AggregateException
- AggregateException 是更通用 abstract（用户其他场景可能也需要）
- 加一个轻量 base class 不复杂

### Decision 3: Func/Predicate aggregate 延后
**决定：** 仅 Action 路径聚合。Func/Predicate 因需要 Results[] 占位（默认值是 R 类型相关）+ MulticastException<R> 泛型继承，独立 follow-up（D-8b）。
- Action 路径：Failures 数组够用（无返回值）
- Func/Predicate：需要 MulticastException<R>.Results 携带 R[]（成功位置 = 返回值；失败位置 = default(R)），R 默认值依赖类型系统支持

### Decision 4: handler 索引计数策略
**问题：** Failures 索引从哪开始？strong + advanced 是分开计数还是统一？
**决定：** 统一计数 0-based，按 Invoke 时 strong-first / advanced-after 累加：
- strong[0..sn) → 索引 0..sn-1
- advanced[0..an) → 索引 sn..sn+an-1
- 用户调试时可还原 handler 来源

## Implementation Notes

- z42 stdlib 用 dynamic-grow array：`Exception[]` + `int[]` 各自维护 capacity，复用 D2a/b ArrayList-style 模式
- 失败累积时 capacity *2 grow（避开 Std.Collections.List 依赖）
- `try { ... } catch (Exception e) { ... }` z42 已支持
- `throw new MulticastException(...)` 已支持
- continueOnException=false 路径保持 fail-fast 不变（已 ship）

## Testing Strategy

- Golden test `multicast_exception_aggregate/source.z42`：
  - 0 异常 + continueOnException=true → 不抛
  - 1 异常 → 抛 MulticastException 含正确 Failures
  - 多异常 → 全聚合
  - continueOnException=false 保持原 fail-fast
  - advanced wrapper 失败也聚合
- D2a/b/c/d-1 既有 golden 仍 GREEN
- IncrementalBuildIntegrationTests 41 → 43
