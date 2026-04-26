# z42 迭代机制

> **状态**：foreach 语法 ✅（L1）+ 鸭子协议 ✅（L3-G4h step2）；Wave 2 引入
> `IEnumerable<T>` / `IEnumerator<T>` / `IDisposable` 作为显式接口契约。
> **使用者视角**。

---

## 两条路径并存

z42 当前同时支持两种迭代模型，它们**互不干扰**：

### 1. 索引驱动鸭子协议（foreach 实际走的路径）

`foreach (var x in c)` lower 为索引循环，要求 `c` 有：

```z42
int Count();
T   get_Item(int i);
```

适用于 `T[]` 数组、`List<T>`、`Dictionary<K,V>`（key set）、用户自定义可
索引类型。**foreach 不会自动调用 GetEnumerator()**。

详见 `docs/design/foreach.md`。

### 2. 显式 IEnumerable / IEnumerator 接口契约（Wave 2 引入）

```z42
public interface IDisposable {
    void Dispose();
}

public interface IEnumerator<T> : IDisposable {
    bool MoveNext();
    T    Current();
}

public interface IEnumerable<T> {
    IEnumerator<T> GetEnumerator();
}
```

**用途：**
- 泛型约束 `where T: IEnumerable<U>` —— 算法在抽象层依赖"可迭代"
- 显式契约声明 —— `class List<T> : IEnumerable<T>`（Wave 3+ 实现）
- 未来 LINQ / iterator chain 的协议入口
- 自定义 iterator 类（如 `RangeIterator` 实现 `IEnumerator<int>`）

**当前限制：**
- foreach 不识别 IEnumerable 路径 —— 必须同时提供 Count + get_Item 才能
  foreach（独立 change 升级 codegen）
- ~~`T Current { get; }` property 形式 parser 暂未支持~~ —— 已支持（2026-04-26
  add-auto-property-syntax）；IEnumerator 已恢复 C# 标准 property 形式

## 设计权衡

### 为什么不让 foreach 自动识别 IEnumerable

历史路径：L3-G4h step2 实现了 Count + get_Item 鸭子协议，已覆盖所有现有
集合（List/Dictionary/Stack/Queue/Array/用户类）。foreach codegen 升级支
持 IEnumerator 是独立工作，需考虑：

- 鸭子协议与接口路径优先级
- Dispose 自动调用时机（break / continue / 异常）
- iterator state 的 frame 捕获

留作 L3+ 独立 change（如 `extend-foreach-ienumerable-path`）。

### 为什么 IEnumerator 是 C# 风（MoveNext + Current）而非 Rust 风

- z42 整体对齐 C# BCL 命名（见 `docs/design/stdlib.md` Design Philosophy）
- `Option<T>` 尚未引入（L3 ADT），Rust 风 `Option<T> Next()` 需等 ADT 可用
- `MoveNext() → Current()` 两阶段访问对手写 iterator 更友好

### 为什么 IEnumerator 继承 IDisposable

C# 模式：foreach 块退出（正常 / break / 异常）应释放 iterator 内部状态
（如打开的文件句柄、线程同步原语）。z42 采用同样设计，为未来 foreach
codegen 升级保留 Dispose 自动调用的语义槽。

## 未来计划

| 项 | 说明 |
|---|----|
| foreach 识别 IEnumerator 路径 | codegen 优先 GetEnumerator()；自动 Dispose；与鸭子协议 fallback 共存 |
| 接口 property `T Current { get; }` | Parser / TypeChecker 扩展 |
| List/Dictionary 实现 IEnumerable | Wave 3+，配合 `KeyValuePair<K,V>` 引入 |
| `IReadOnlyList<T>` / `ICollection<T>` | C# BCL 派生接口按需补齐 |
| LINQ 风格扩展（Where/Select/...）| 依赖 lambda + IEnumerable，L3 推进 |
