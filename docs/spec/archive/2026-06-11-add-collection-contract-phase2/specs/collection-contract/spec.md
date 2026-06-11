# Spec: Collection contract Phase 2 — add/clear lifecycle

> 状态：DRAFT 待审。扩展 trait test commons 的集合契约，从"空集合"到"add/clear
> 生命周期"。

## ADDED Requirements

### Requirement: `IBasicCollection<T>` 提供统一单元素插入 `AddOne`

#### Scenario: 接口声明 AddOne
- **WHEN** 检视 `Std.IBasicCollection<T>`
- **THEN** 含 `void AddOne(T item);`，语义为"把一个元素放入集合"（顺序/去重策略由实现决定）

#### Scenario: 5 个集合实现 AddOne 委托自然 add
- **WHEN** 检视 Queue/Stack/LinkedList/SortedSet/PriorityQueue
- **THEN** 各实现 `AddOne`，分别委托 `Enqueue`/`Push`/`AddLast`/`Add`/`Enqueue`
  （丢弃 `AddLast`/`Add` 的返回值），调用后该元素计入 `Count()`

### Requirement: `BasicCollectionContract.Run` 覆盖 add/clear 生命周期

#### Scenario: 加 distinct 元素后非空且 Count 准确
- **WHEN** 对任一 `IBasicCollection<int>` fresh 实例跑 `Run`，其中 `AddOne(1)`/`AddOne(2)`/`AddOne(3)`
- **THEN** `Count() == 3` 且 `IsEmpty() == false`（对去重的 SortedSet 也成立，因元素 distinct）

#### Scenario: 非空集合 Clear 后回空
- **WHEN** 在上面"已加 3 元素"的实例上 `Clear()`
- **THEN** `Count() == 0` 且 `IsEmpty() == true`（捕获 Clear 没清干净 / Count 未归零 / IsEmpty 滞留）

#### Scenario: 既有空集合不变量保留
- **WHEN** `Run` 起始（fresh 实例尚未 add）
- **THEN** Phase 1 的 `Count()==0 && IsEmpty()` 不变量仍先断言（生命周期从"空"开始）

## MODIFIED Requirements

### Requirement: `BasicCollectionContract.Run` 行为

**Before:** 断言 fresh 为空 + `Clear()` 在空集合上幂等（Phase 1）。
**After:** 完整生命周期——空 → `AddOne` distinct ×3 → `Count()==3 && !IsEmpty()` →
`Clear()` → `Count()==0 && IsEmpty()`。`Run(name, instance)` 签名不变（调用方零改动）。

### Requirement: 集合自然 add API 不变

**Before:** Queue.Enqueue / Stack.Push / LinkedList.AddLast / SortedSet.Add / PriorityQueue.Enqueue。
**After:** 不变；`AddOne` 是*额外*的契约统一入口，不替代、不重命名自然 API。

## IR Mapping

无 IR / 格式变化（纯 stdlib `.z42` 源）。

## Pipeline Steps

- [ ] Lexer / Parser / TypeChecker / IR Codegen / VM —— 不涉及（纯 stdlib 源 + 既有接口分派）
- [x] stdlib：接口加方法 + 5 实现 + 契约扩展 + [Test] 覆盖
