# Tasks: add-collection-contract-phase2

> 状态：🟢 已完成 | 创建：2026-06-11 | 完成：2026-06-11 | 类型：stdlib（新接口契约方法 + 测试契约扩展）
> 子系统锁：stdlib（见 ACTIVE.md）。

## 进度概览
- [x] 阶段 1: `IBasicCollection<T>` 加 `AddOne` + 5 集合实现
- [x] 阶段 2: `BasicCollectionContract.Run` 扩 add/clear 生命周期 + 补全 5 集合 [Test] 覆盖
- [x] 阶段 3: 验证（test stdlib：z42.collections 5/5 + z42.test 5/5）

## 阶段 1: 接口 + 实现
- [x] 1.1 `IBasicCollection.z42`：加 `void AddOne(T item);` + 注释
- [x] 1.2 `Queue.z42`：`AddOne → this.Enqueue(item)`
- [x] 1.3 `Stack.z42`：`AddOne → this.Push(item)`
- [x] 1.4 `LinkedList.z42`：`AddOne → this.AddLast(item)`（丢弃 node）
- [x] 1.5 `SortedSet.z42`：`AddOne → this.Add(item)`（丢弃 bool）
- [x] 1.6 `PriorityQueue.z42`：`AddOne → this.Enqueue(item)`

## 阶段 2: 契约扩展 + 覆盖
- [x] 2.1 `BasicCollectionContract.Run`：空 → `AddOne(1/2/3)` → `Equal(3,Count)`+`False(IsEmpty)` → `Clear` → empty → `Clear` 幂等
- [x] 2.2 补 Queue / Stack 的 contract [Test]（新建 `tests/queue.z42` / `tests/stack.z42`，各一行 Run）
- [x] 2.3 更新 `BasicCollectionContract` / `IBasicCollection` 顶部注释反映 Phase 2 形态

## 阶段 3: 验证
- [x] 3.1 `test stdlib z42.collections` —— 5/5 文件全绿（queue/stack 新契约 + linkedlist/sorted_set/priority_queue 扩展生命周期）
- [x] 3.2 `test stdlib z42.test` —— 5/5 全绿（契约 driver 自身）
- [x] 3.3 z42c 编译无 unimplemented-interface 错误（IBasicCollection 全部 5 实现者均加 AddOne，无其它实现者）
- [x] 3.4 spec scenarios 逐条覆盖确认
- [x] 3.5 无 docs/design/stdlib collection-contract 文档（契约自文档化于源码注释，已更新）

## 备注

- distinct 元素（1/2/3）是契约普适性的关键——SortedSet 去重下 "加 3 distinct → Count==3"
  仍成立；用重复元素会破坏全集合通用性。
- 泛型 `Run<T>` + 更多契约族（IEnumerable/IComparable/IDictionary）仍 Out of Scope。
