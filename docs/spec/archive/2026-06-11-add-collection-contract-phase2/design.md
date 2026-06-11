# Design: Collection contract Phase 2

> 状态：DRAFT 待审

## Architecture

```
Std.IBasicCollection<T>            (z42.core/Protocols)
  int  Count()
  bool IsEmpty()
  void Clear()
  void AddOne(T item)             ← 新增：契约用统一单元素插入

  ├─ Queue<T>          AddOne → Enqueue
  ├─ Stack<T>          AddOne → Push
  ├─ LinkedList<T>     AddOne → AddLast      (丢弃 node 返回值)
  ├─ SortedSet<T>      AddOne → Add          (丢弃 bool 返回值)
  └─ PriorityQueue<T>  AddOne → Enqueue

Std.Test.Contracts.BasicCollectionContract.Run(name, instance)   (z42.test/Contracts)
  空 → AddOne(1) AddOne(2) AddOne(3) → Count()==3 && !IsEmpty()
      → Clear() → Count()==0 && IsEmpty()
```

## Decisions

### Decision 1: 在接口加统一 `AddOne(T)`，而非工厂回调 / 复用自然 add

**问题**：契约要断言"加元素后 Count 增长"，需要一个对所有集合统一的"加一个元素"入口。
**选项**：
- **A（接口加 `AddOne`）**：`IBasicCollection<T>` 加 `void AddOne(T)`，各集合委托自然 add。
- **B（契约取 inserter 回调）**：`Run(name, instance, Action<C,int> insert)`。但 z42 委托
  当前缺返回类型协变（Phase 1 已记：`Func<IBasicCollection<int>>` 不可赋），且 inserter
  仍要通过 *某个* 统一签名调用集合 add——绕不开接口暴露。
- **C（复用某集合已有的 `Add`）**：只有 SortedSet 有 `Add`，其余没有，不通用。
**决定**：**选 A**。接口加一个语义明确的契约入口最直接、零回调机制依赖。Phase 1 担心的
"机械别名"在这里不成立——`AddOne` 不是把 5 个不同语义硬塞成一个名字，而是提供一个
**契约层最小公共操作**："把一个元素放进集合"，这对 Queue/Stack/Set/Heap **都是真实、
有意义的操作**（只是顺序/去重策略不同，而契约不依赖顺序）。

### Decision 2: 命名 `AddOne`（不撞自然 API、不诱导替代）

**问题**：叫什么？
**选项**：`Add`（撞 SortedSet 的 `Add→bool`，且诱导用户在 Queue 上用 `Add` 而非 `Enqueue`）/
`Insert` / `ContractAdd` / `AddOne`。
**决定**：**`AddOne`**。① 不与任何集合自然 add 撞名；② "One" 强调"加*一个*"的契约语义，
读代码即知是测试用最小入口，不像通用 API；③ 返回 `void`（统一签名；自然 add 的
`bool`/`node` 返回值在委托里丢弃——契约不关心"是否新增/插到哪")。

### Decision 3: 契约用 distinct 元素（1/2/3），不用重复元素

**问题**：`AddOne` 三次加什么？
**决定**：**distinct**（1、2、3）。"加 3 个 distinct → Count()==3" 对**所有** `IBasicCollection`
成立：Queue/Stack/LinkedList/PriorityQueue 保留全部 = 3；SortedSet 去重但 3 个 distinct
仍 = 3。若用重复元素（1、1、1），SortedSet 去重到 Count==1，契约就不再"普适"——破坏
trait test commons 的全集合通用性。distinct 是让该不变量保持普适的关键约束。

> 注：契约只断言 `Count()==3 && !IsEmpty()`，**不**断言元素顺序或可迭代性——那依赖
> 集合特定语义（FIFO/LIFO/sorted/heap）+ 迭代接口，属 Out of Scope 的更丰富契约。

### Decision 4: 不改 5 个集合的既有 contract 测试调用

`Run(name, instance)` 签名不变；只是 `Run` 内部多跑 add/clear 阶段。既有测试已传 fresh
实例（契约首断言即空），更丰富的 `Run` 在同一实例上顺序跑完整生命周期——零调用方改动，
正是 trait test commons "加覆盖零成本"的体现。

## Implementation Notes

- 接口方法：`void AddOne(T item);`（无默认实现——z42 接口方法需各类实现；5 个类各加一个
  转发方法，≤2 行）。
- LinkedList 的 `AddLast(T)` 返回 `LinkedListNode<T>`；`AddOne` 里 `this.AddLast(item);`
  作为语句调用，丢弃返回值（z42 允许忽略返回值）。
- SortedSet 的 `Add(T)` 返回 `bool`；同样语句调用丢弃。
- 泛型约束：5 个集合的 `where`（LinkedList: `IEquatable<T>`；SortedSet:
  `IComparable<T>+IEquatable<T>`；PriorityQueue: `IComparable<T>`）不变；`AddOne` 在类体内，
  受同样约束。契约用 `int`（满足全部约束）实例化，不触发约束问题。

## Testing Strategy

- 既有 5 个集合的 contract 测试（call `BasicCollectionContract.Run`）自动获得新的
  add/clear 生命周期覆盖——无需新增测试文件，但需确认它们仍绿（更丰富的 `Run` 跑过）。
- 若某集合还没有 contract 测试调用（Phase 1 只接了 linkedlist/sorted_set/priority_queue），
  补上 Queue / Stack 的 `[Test]` contract 调用，使 5 个集合全覆盖（一行一个）。
- 验证：`z42 xtask.zpkg test stdlib z42.collections` + `z42.test`（dotnet 编译 + [Test]
  dogfood）。无格式 bump，无 runtime 改动。
- 不需要新 e2e / golden（纯 stdlib 行为，[Test] 覆盖即可）。
