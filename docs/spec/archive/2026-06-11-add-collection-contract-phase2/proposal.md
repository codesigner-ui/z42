# Proposal: Collection contract Phase 2 — uniform `AddOne` + add/clear lifecycle (review.md S2.4)

> 状态：DRAFT 待审（阶段 6.5 前）

## Why

review.md **S2.4**（"stdlib 改造里 ROI 最高的一项"）的 trait-based test commons：
Phase 1（add-collection-contract-phase1, 2026-06-03）落地了 `Std.IBasicCollection<T>`
（`Count()`/`IsEmpty()`/`Clear()`）+ `BasicCollectionContract.Run`，5 个集合
（Queue/Stack/LinkedList/SortedSet/PriorityQueue）"零行加"获得 *空集合* 不变量覆盖。

但 Phase 1 的契约**只能断言空集合行为**（fresh 为空、Clear 幂等），因为
`IBasicCollection` 没有"添加"操作——Phase 1 注释明确把这步留给 "Phase 2 spec 决定
一个有意义的更丰富契约"，理由是 5 个集合的 add 命名/签名各异
（`Enqueue`/`Push`/`AddLast`→node/`Add`→bool/`Enqueue`），不想机械别名。

**问题**：没有 add，契约覆盖不到任何 *非空* 状态——而"加 N 个元素后 Count()==N、
非空；Clear 后回空"恰恰是所有集合**真正共享**的核心不变量，且是最容易写错的地方
（Count 漏增 / Clear 没清干净 / IsEmpty 标志滞留）。这正是 trait test commons 的价值点。

## What Changes

- `IBasicCollection<T>` 加一个统一的单元素插入 `void AddOne(T item)`——专为契约测试
  提供"加一个元素"的最小公共操作。每个集合实现它，委托到自己的自然 add：
  | 集合 | 自然 add | `AddOne` 委托 |
  |------|---------|--------------|
  | Queue | `Enqueue(T)` | `this.Enqueue(item)` |
  | Stack | `Push(T)` | `this.Push(item)` |
  | LinkedList | `AddLast(T)→node` | `this.AddLast(item)`（丢弃返回的 node）|
  | SortedSet | `Add(T)→bool` | `this.Add(item)`（丢弃返回的 bool）|
  | PriorityQueue | `Enqueue(T)` | `this.Enqueue(item)` |
- `BasicCollectionContract.Run` 扩成完整生命周期：空 → `AddOne` **distinct** 元素 ×3
  → 断言 `Count()==3 && !IsEmpty()` → `Clear()` → 断言 `Count()==0 && IsEmpty()`。
  **用 distinct 元素**（1/2/3）使 "加 3 distinct → Count==3" 对去重的 SortedSet
  也成立（用重复元素则 SortedSet 会 dedup 到 1，契约就不通用了）。
- 5 个集合既有的 contract 测试（已传 fresh 实例）无需改调用——更丰富的 `Run`
  在同一 fresh 实例上跑完整生命周期。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.core/src/Protocols/IBasicCollection.z42` | MODIFY | 接口加 `void AddOne(T item);` + 注释说明用途 |
| `src/libraries/z42.collections/src/Queue.z42` | MODIFY | 实现 `AddOne` 委托 `Enqueue` |
| `src/libraries/z42.collections/src/Stack.z42` | MODIFY | 实现 `AddOne` 委托 `Push` |
| `src/libraries/z42.collections/src/LinkedList.z42` | MODIFY | 实现 `AddOne` 委托 `AddLast` |
| `src/libraries/z42.collections/src/SortedSet.z42` | MODIFY | 实现 `AddOne` 委托 `Add` |
| `src/libraries/z42.collections/src/PriorityQueue.z42` | MODIFY | 实现 `AddOne` 委托 `Enqueue` |
| `src/libraries/z42.test/src/Contracts/BasicCollectionContract.z42` | MODIFY | `Run` 扩 add→count→clear 生命周期断言 + 顶部注释更新 |
| `src/libraries/z42.collections/tests/queue.z42` | NEW | Queue<int> contract [Test]（Phase 1 未接 Queue）|
| `src/libraries/z42.collections/tests/stack.z42` | NEW | Stack<int> contract [Test]（Phase 1 未接 Stack）|

**只读引用**：
- 5 个集合各自的既有 contract 测试文件（`tests/*_contract*.z42` 等）—— 确认 `Run`
  调用签名不变，无需改

## Out of Scope

- **泛型 `Run<T>`**（Phase 1 deferred：当前硬编码 `int`，绕开 generic-fn-with-interface-
  constraint 路径）—— 仍留待后续；本 Phase 2 继续用 `int` 元素。
- **`IEnumerableContract` / `IComparableContract` / `IDictionaryContract`** 等更多契约
  族（CoreCLR ~30 套）—— 独立后续；本变更只把 *collection* 契约从"空集合"扩到
  "add/clear 生命周期"。
- **删除/重命名集合的自然 add API**（Enqueue/Push/...）—— 不动；`AddOne` 是*额外*的
  契约用统一入口，不替代自然 API。
- **Remove/Contains/迭代 契约** —— 需要更多接口方法（语义分叉更大），独立评估。

## Open Questions

- [ ] `AddOne` 命名：用 `AddOne` 表明"加一个元素"的契约语义，不与任何集合的自然
      add（Enqueue/Push/Add/AddLast）撞名，也不诱导用户用它替代自然 API。design.md
      论证 vs 备选（`ContractAdd` / 复用 `Add`）。
