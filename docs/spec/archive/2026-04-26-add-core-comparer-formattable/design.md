# Design: IComparer / IEqualityComparer / IFormattable

## Architecture

3 个独立接口文件，与现有 `IEquatable.z42` / `IComparable.z42` / `IDisposable.z42`
等核心接口同模式：

```
z42.core/src/
├── IComparer.z42           (新)
├── IEqualityComparer.z42   (新)
├── IFormattable.z42        (新)
├── IEquatable.z42          (已有)
├── IComparable.z42         (已有)
├── IDisposable.z42         (已有)
├── IEnumerable.z42         (Wave 2)
├── IEnumerator.z42         (Wave 2)
└── ...
```

无依赖关系（除 Object 协议隐含）；不形成 forward / self-reference 链路。

## Decisions

### Decision 1: IComparer<T> 是泛型，IFormattable 非泛型

- `IComparer<T>` / `IEqualityComparer<T>` 泛型 — 类型参数标识被比较元素类型
  （C# BCL 同）
- `IFormattable` 非泛型 — 返回 `string`，无类型参数需求（C# BCL `IFormattable`
  非泛型；C# 7+ `ISpanFormattable` 是 ref struct 优化，本 wave 不上）

### Decision 2: 不分离 IHashable

`IEquatable<T>` 已含 `int GetHashCode();`（绑定）。再独立 IHashable 与
IEquatable 重叠 → 跳过。Wave 3 路线图修订。

### Decision 3: IEqualityComparer.GetHashCode 单参数

与 IEquatable.GetHashCode 无参形成对照：
- `IEquatable.GetHashCode()` — 实例自身的 hash
- `IEqualityComparer<T>.GetHashCode(T obj)` — 比较器对外部对象计算 hash

C# BCL 同模式。

### Decision 4: 仅契约定义，无 implementer

本 wave 不让 List / Dictionary 等已有集合实现这些接口。原因：
- 集合接入需要 ctor overload + Sort overload 等额外 API surface；属于独立
  工作（Wave 4 或更晚）
- 接口定义先就位，stdlib / 用户代码可立即使用泛型约束

### Decision 5: golden test 验证范围

仅 1 个 golden test (`100_comparer_contract`) — 用户类实现 IComparer 并经
接口引用调用，验证接口契约 + VCall dispatch 工作。

不写 IFormattable 单独 golden（与 Object.ToString 共存的 method overload
已通过 Wave 2 + #2 验证；此处接口契约同模式）。

不写 IEqualityComparer 单独 golden（同上）。

### Decision 6: namespace

3 个接口都放 `Std` namespace（与 IEquatable / IComparable 一致），扁平
位于 `z42.core/src/`（不进 Collections/ 子目录，因为它们是通用接口）。

## Implementation Notes

### IComparer.z42

```z42
namespace Std;

// 双参数比较器契约（与 IComparable 的"自比"模式区分）。
// 用于 stdlib Sort overload / SortedDictionary 等场景的自定义排序。
//
// Compare(x, y) 返回:
//   < 0  →  x 排在 y 之前
//   = 0  →  x 与 y 等价
//   > 0  →  x 排在 y 之后
public interface IComparer<T> {
    int Compare(T x, T y);
}
```

### IEqualityComparer.z42

```z42
namespace Std;

// 双参数相等性 + 哈希契约（与 IEquatable 的"自比"模式区分）。
// 用于 stdlib Dictionary / HashSet 自定义键比较器。
//
// 实现者必须保证：Equals(x, y) == true ⟹ GetHashCode(x) == GetHashCode(y)。
public interface IEqualityComparer<T> {
    bool Equals(T x, T y);
    int  GetHashCode(T obj);
}
```

### IFormattable.z42

```z42
namespace Std;

// 自定义格式化契约。format 字符串的语义由 implementer 决定（如数值
// 类型可能识别 "X8" 表示 8 位 hex）。无统一格式语法。
//
// 与 Object.ToString() (无参) 通过 method overload 共存：
//   - obj.ToString()       —— Object 协议
//   - obj.ToString(fmt)    —— IFormattable 协议
public interface IFormattable {
    string ToString(string format);
}
```

## Testing Strategy

- Golden test `run/100_comparer_contract`：自定义 DescIntComparer 实现
  IComparer<int>，经接口引用调用 Compare 验证 VCall dispatch 工作
- 全量 dotnet test / test-vm.sh / cargo test 全绿

## 兼容性风险

| 风险 | 评估 | 缓解 |
|------|------|------|
| IFormattable.ToString 与 Object.ToString 名字冲突 | 低 | method overload by arity（Wave 2 + #2 验证）已支持 |
| Equals 多版本（Object.Equals(other) vs IEqualityComparer.Equals(x, y)） | 低 | 不同接收者类型 + arity；不混淆 |
| 接口未被任何现有代码使用 | 低（预期） | 接口契约先行，未来 implementer 接入是独立工作 |
