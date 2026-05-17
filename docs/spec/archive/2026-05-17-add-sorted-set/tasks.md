# Tasks: add SortedSet<T> to z42.collections

> 状态：🟢 已完成 | 创建：2026-05-17 | 完成：2026-05-17 | 类型：feat（纯脚本 stdlib，无新 VM/IR）
> Spec 类型：minimal mode

## 背景

z42.collections v0 已有 `Queue<T>` / `Stack<T>` / `LinkedList<T>`，缺**排序型唯一集合**。
对标 C# `SortedSet<T>` / Rust `BTreeSet`。

原本 v0 计划同时加 `PriorityQueue<T>` 与 `SortedDictionary<K, V>`，实施期发现两个类
`new ...()` 构造时抛 bare "VM error"（无具体信息），结构与 SortedSet 几乎一致，
怀疑是 generic class with constraints + 二参数（K, V）edge case。**这两个推迟到独立 spec**
（`add-priority-queue` / `add-sorted-dictionary`），等 IR-级调试可观察后再上。

## API Surface (v0)

```z42
namespace Std.Collections;

// Sorted-set of unique elements, sorted by T's IComparable<T>. v0 uses a
// sorted T[] with binary-search Add (O(n) due to shift) and O(log n)
// Contains. Red-black / AVL backing is a future optimisation.
public class SortedSet<T> where T: IComparable<T> + IEquatable<T> {
    public SortedSet();
    public int  Count();
    public bool IsEmpty();
    public bool Add(T item);       // false if already present
    public bool Contains(T item);
    public bool Remove(T item);
    public T    Min();             // throws on empty
    public T    Max();             // throws on empty
    public T[]  ToArray();         // sorted ascending
    public void Clear();
}
```

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. 后端 | 排序数组 / 红黑树 / 跳表 | 排序数组 | v0 简单；Add O(n) 可接受（典型 SortedSet 规模 < 1k）；BST 留作 v1 perf upgrade 独立 spec |
| 2. 错误处理 | throw / 返回 null + bool | throw | 与 `List<T>` / `Dictionary<K, V>` 现状一致；返回 bool 给 Remove 表示成功/失败 |
| 3. 重复 Add 返回 | true 当新加 / 无返回值 | bool | 与 C# `HashSet<T>.Add` / `SortedSet<T>.Add` 对齐；调用方常需要知道是否真新加 |
| 4. 约束 | `T: IComparable<T>` only / `+ IEquatable<T>` | 都加 | Add/Contains 用 `.Equals` 检测重复，必须 IEquatable；Min/Max 排序用 IComparable |
| 5. PriorityQueue + SortedDictionary | 同 spec 内一起做 / 独立 spec | 独立 spec | 实施期发现这两个的构造期失败，与 SortedSet 同结构却 fail，根因不明；先 ship 能跑的，PQ/SortedDict 留 IR 级调试 |

## 不在 v0（follow-up specs）

- **`add-priority-queue`**：min-heap binary PriorityQueue<T>（实施期发现构造 bug 待诊断）
- **`add-sorted-dictionary`**：sorted-array K/V 字典（同上）
- **`upgrade-sorted-set-bst`**：红黑树后端（log-n Add/Remove）
- `IntersectWith` / `UnionWith` / `ExceptWith` 集合代数
- `Range(min, max)` 范围查询

## Scope（允许改动的文件）

| 文件路径 | 变更 | 说明 |
|---------|------|------|
| `src/libraries/z42.collections/src/SortedSet.z42` | NEW | sorted-array set |
| `src/libraries/z42.collections/tests/sorted_set.z42` | NEW | 11 tests |

## 阶段

- [x] 1.1 NEW `src/libraries/z42.collections/src/SortedSet.z42`
- [x] 2.1 NEW `src/libraries/z42.collections/tests/sorted_set.z42`（11 tests）
- [x] 3.1 GREEN：`./scripts/test-stdlib.sh` 全 61 文件全过；`dotnet test` 1288/1288
- [x] 4.1 commit + push + archive
