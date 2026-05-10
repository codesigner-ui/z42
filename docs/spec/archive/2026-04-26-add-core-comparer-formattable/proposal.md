# Proposal: z42.core 补齐 IComparer / IEqualityComparer / IFormattable

## Why

stdlib 缺少**双参数比较器**和**自定义格式化**的契约：

- 当前 `IComparable<T>.CompareTo(other)` 是"自比"模式（实例知道如何比自己与
  另一个），无法支持外部传入的自定义排序（如 `list.Sort(myComparer)`）
- 当前 `IEquatable<T>.Equals(other)` 同样是"自比"模式；HashSet / Dictionary
  自定义 hash + equality 没有标准接口
- `Object.ToString()` 无格式参数；C# BCL `IFormattable.ToString(format)`
  在数值 / 日期类型常用（如 `42.ToString("X8")` 输出 `"0000002A"`）

Wave 3 范围（修订版）：补齐这三个接口契约。Script-First 纯定义，无 extern，
无 implementer（不在本 wave 让 List/Dictionary 实现，与 Wave 2 IEnumerable
同模式）。

> **跳过 IHashable**：路线图原列 IHashable，调研确认 `IEquatable<T>` 已含
> `int GetHashCode();`。再独立 IHashable 与 IEquatable 重叠，反而分散契约。

## What Changes

### IComparer<T>

```z42
public interface IComparer<T> {
    int Compare(T x, T y);
}
```

返回 negative / 0 / positive 与 `IComparable.CompareTo` 同约定。
典型使用：`list.Sort(IComparer<int> cmp)` 自定义排序顺序。

### IEqualityComparer<T>

```z42
public interface IEqualityComparer<T> {
    bool Equals(T x, T y);
    int  GetHashCode(T obj);
}
```

双参数相等性 + 单参数 hash。典型使用：构造 `Dictionary` 时传入自定义键
比较器（如大小写不敏感的字符串字典）。

### IFormattable

```z42
public interface IFormattable {
    string ToString(string format);
}
```

对齐 C# BCL `IFormattable`。format 字符串语义留给具体 implementer
（如 `"X"` hex / `"D2"` 数字 + 0 填充等）；本 wave 仅契约定义，
具体格式语义由后续数值类型的 implementer 提供。

## Scope（允许改动的文件/模块）

| 文件 | 变更类型 | 说明 |
|------|---------|------|
| `src/libraries/z42.core/src/IComparer.z42` | add | 接口定义 |
| `src/libraries/z42.core/src/IEqualityComparer.z42` | add | 接口定义 |
| `src/libraries/z42.core/src/IFormattable.z42` | add | 接口定义 |
| `src/libraries/z42.core/README.md` | edit | 追加三个接口到核心文件表 |
| `docs/roadmap.md` | edit | L2 stdlib 条目更新 |
| `src/runtime/tests/golden/run/100_comparer_contract/` | add | golden test：定义实现自定义 IComparer 并使用接口引用调用 |

## Out of Scope

- List / Dictionary 实现这些接口（推迟到 Wave 4 或独立 change）
- IComparer / IEqualityComparer 在 stdlib Sort / Dictionary ctor 的接入
  （需要 List 重载 ctor / Sort overload，独立工作）
- `Format` 字符串语义规范（如 `"X"` / `"D2"` 等格式化代码列表）—
  IFormattable 仅契约定义，语义由 implementer 决定
- `Object.GetHashCode(obj)` 静态版本（C# 9+ 新增）

## Open Questions

- [x] IHashable 是否本 wave 加 → **不加**（与 IEquatable 重叠）
- [x] IFormattable 是否泛型 `IFormattable<TSelf>` → **不泛型**（C# 同样
  `IFormattable` 非泛型；返回 `string`，无类型参数需求）
- [x] golden test 是否需要 IComparer 真实使用场景（如 Sort 调用）→ **否**，
  当前 Sort 不接受 IComparer；测试仅验证接口可被实现 + 经接口引用调用

## Blocks / Unblocks

- **Unblocks**：
  - 未来 List.Sort 重载 / Dictionary 自定义键比较器接入有标准契约
  - 未来数值类型可实现 IFormattable
- **Blocks**：无
