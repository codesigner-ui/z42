# Tasks: S2.4 Phase 1 — IBasicCollection contract + trait-based test commons

> 状态：🟢 已完成 | 创建：2026-06-03 | 完成：2026-06-03 | 类型：refactor + 小幅 API 加法
> 来源：[`docs/review.md`](../../../review.md) Part 3 S2.4

## 变更说明

引入 z42 第一个 trait-based test commons 模式：

- NEW `Std.IBasicCollection<T>` 接口 (z42.core)：`Count() / IsEmpty() /
  Clear()` —— 5 个 z42.collections 类已有的统一子集
- 5 个集合类（Queue / Stack / LinkedList / SortedSet / PriorityQueue）声明
  实现该接口（已有方法签名匹配，机械加法）
- NEW `Std.Test.Contracts.BasicCollectionContract.Run(name, factory)`
  (z42.test) —— 接受 `Func<IBasicCollection<int>>` 工厂，跑两条"普遍正确"
  断言（new is empty / clear preserves empty）
- 3 个测试文件加 `[Test] void test_<X>_basic_contract()` 调用契约

每个新集合只要 implement 接口 + 在测试文件加 2 行（contract import +
contract call），就免费获得契约测试覆盖。

## 原因 / 现实主义说明

review.md S2.4 设想是 `ICollectionContract` / `IEnumerableContract` /
`IComparableContract` ≥30 个测试函数的成套契约。完整版需要：
1. 每个集合暴露一个 `Add(T)` 抽象（不同集合 Push/Enqueue/AddLast 不同）—
   需新加 API surface 或泛型 helper
2. `IEnumerableContract` 依赖 IEnumerable 实现，当前集合都没声明
3. `IComparableContract` 与现有泛型约束体系交互复杂

**Phase 1 收紧到 `IBasicCollection` 子集** —— 只覆盖 5 集合**都已自然
提供**的 3 方法（Count/IsEmpty/Clear）。验证：
- z42 generics + interface impl 端到端工作
- 接口 + factory delegate + 契约调用模式可用
- 一个真实工作的 trait-based test commons 进 stdlib

Phase 2+ (独立 spec) 再扩展到 `IAddableCollection` / `IEnumerable` /
`IComparable` 等带类型方法的契约。

## 文档影响

- `docs/review.md` S2.4 / 总表更新 (🟡 Phase 1 done)
- `docs/design/stdlib/` 不动 (无新设计决策；接口本身 prelude 化)

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `src/libraries/z42.core/src/Protocols/IBasicCollection.z42` | NEW | 3-method interface |
| `src/libraries/z42.collections/src/Queue.z42` | MODIFY | `: IBasicCollection<T>` |
| `src/libraries/z42.collections/src/Stack.z42` | MODIFY | `: IBasicCollection<T>` |
| `src/libraries/z42.collections/src/LinkedList.z42` | MODIFY | `: IBasicCollection<T>`（保留原 `where T: IEquatable<T>` constraint）|
| `src/libraries/z42.collections/src/SortedSet.z42` | MODIFY | `: IBasicCollection<T>` |
| `src/libraries/z42.collections/src/PriorityQueue.z42` | MODIFY | `: IBasicCollection<T>` |
| `src/libraries/z42.test/src/Contracts/BasicCollectionContract.z42` | NEW | `Run(name, factory)` |
| `src/libraries/z42.collections/tests/linkedlist.z42` | MODIFY | 加 `[Test] void test_linkedlist_basic_contract()` |
| `src/libraries/z42.collections/tests/sorted_set.z42` | MODIFY | 加 `[Test] void test_sorted_set_basic_contract()` |
| `src/libraries/z42.collections/tests/priority_queue.z42` | MODIFY | 加 `[Test] void test_priority_queue_basic_contract()` |
| `docs/review.md` | MODIFY | S2.4 标 🟡 Phase 1 done |

只读引用：
- `src/libraries/z42.test/src/Assert.z42` — 契约内部用 `Std.Test.Assert`
- z42.collections.z42.toml / z42.test.z42.toml — 不改 deps（z42.test deps z42.core for `IBasicCollection`，已有）

## 设计要点

### 接口生命位置 = z42.core

z42.test 在 z42.collections **之后**编译 (workspace topo)，所以 contract
不能从 z42.test 暴露给 z42.collections。把接口放 z42.core (prelude) 避开
cycle。z42.test contract 只依赖接口，不依赖 z42.collections 具体类型。

### `Func<IBasicCollection<int>>` factory

Phase 1 契约硬编码 `int` 元素类型。z42 generic functions 加 interface
constraint 形态需要先趟一遍，留 Phase 2。`int` 实例化即可演示模式 +
通过所有现有 collection 测试 + 不需要更复杂的 type-level 编程。

### 契约方法签名

```z42
public static class BasicCollectionContract {
    public static void Run(string name, Func<IBasicCollection<int>> factory) {
        var fresh = factory();
        Assert.Equal(0, fresh.Count());
        Assert.True(fresh.IsEmpty());

        fresh.Clear();
        Assert.Equal(0, fresh.Count());
        Assert.True(fresh.IsEmpty());
    }
}
```

`name` 字符串将来用作 contract failure 上下文（`name + ": new is empty"`）。
Phase 1 仅记入备用，不立即用——保持签名给 Phase 2 扩展空间。

## 任务

- [x] 0.1 NEW `tasks.md`
- [x] 1.1 NEW `z42.core/src/Protocols/IBasicCollection.z42`（3 method 接口）
- [x] 1.2 MODIFY 5 collections to declare `: IBasicCollection<T>`
- [x] 1.3 NEW `z42.test/src/Contracts/BasicCollectionContract.z42`（instance-direct，因 z42 delegate 缺 covariance）
- [x] 1.S1 **Scope 扩展**：MODIFY `z42.core/src/Delegates/Delegates.z42` 加 0-arity `Func<R>`（stdlib 缺该 arity，顺手补）
- [x] 1.4 MODIFY 3 test files (linkedlist / sorted_set / priority_queue) to call contract
- [x] 1.5 VERIFY `./scripts/test-stdlib.sh` 全过（exit 0；包括 z42.collections 13 + 12 + 13 + 3 共 4 个文件，前 3 个含新 contract test）
- [x] 1.6 MODIFY `review.md` 标 🟡 Phase 1 done
- [x] 1.7 归档 + commit + push

## 备注

### Scope 扩展（实施期）

1. **`Func<R>` 0-arity**：原设计契约用 `Func<IBasicCollection<int>>` 工厂，
   但发现 stdlib `Delegates.z42` 缺 0-arity（注释说 "0–4 arity" 但只有 1–4）。
   补上作为通用 stdlib 修补。

2. **Contract API 改 instance-direct**：原设计用 `Func` 工厂，但 z42 当前
   delegate 不支持 return-type covariance（`() => new LinkedList<int>()` 不
   赋给 `Func<IBasicCollection<int>>`）。改为接 instance 直接传入；implicit
   up-cast 在 call boundary 工作。同样语义覆盖，1 行 contract call。

3. **Stack / Queue 测试文件**：原 spec 列出 3 个测试文件加 contract。
   Stack / Queue 没有 stdlib `[Test]` 测试文件（用 VM golden test）—— 加新
   stdlib test 文件是独立 scope 扩展，本 spec 不做。

### z42 语言/工具链发现

- z42 delegate 无 return-type covariance（实施时第一次撞到，可作为后续
  language 改进 backlog 项）
- z42 parser 对 `(IGeneric<T>)expr` cast syntax 有歧义问题（`<` / `>` 与
  comparison operator 冲突；本 spec 改 API 绕开，未深究 parser bug；可作
  fix-spec follow-up）
- 我之前 F5.4 的 expected-list error message 在这次实施里立功了：第一次
  contract 编译错误用新格式输出，明显比"unexpected token"友好
