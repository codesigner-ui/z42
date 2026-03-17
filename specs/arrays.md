# z42 Arrays（数组）规范

## 设计参考

| 来源 | 借鉴点 |
|------|--------|
| **C#** | 语法：`T[]`、`new T[n]`、`new T[]{ ... }`、`.Length`、引用语义 |
| **Rust** | `Vec<T>` 的越界 panic 模型；不允许未初始化访问 |

---

## Phase 1 范围

只支持**一维动态数组**（对应 C# `T[]`），多维数组和 jagged array 留到 Phase 2。

---

## 语法

```csharp
// 字面量初始化
int[] arr = new int[] { 1, 2, 3 };

// 指定长度（零值初始化：int→0, bool→false, string→"", object→null）
int[] arr2 = new int[n];

// 元素读写
int x = arr[0];
arr[1] = 42;

// 长度
int len = arr.Length;
```

### 类型表示

| z42 类型 | 描述 |
|----------|------|
| `int[]`    | int 数组 |
| `string[]` | string 数组 |
| `T[]`      | 任意类型的一维数组 |

数组是**引用类型**（堆分配），赋值传递引用（与 C# 一致）。

---

## 语义

- 索引越界：运行时 panic（VM 抛出错误，对应 C# `IndexOutOfRangeException`）
- `.Length` 返回 `int`
- 元素类型检查：编译期由 TypeChecker 验证（`arr[i] = v` 中 v 必须可赋值给元素类型）

---

## IR 映射

新增 4 条指令：

| IR 指令 | 操作 |
|---------|------|
| `array_new { dst, size }` | 分配 size 个元素的零值数组 |
| `array_new_lit { dst, elems: [reg...] }` | 字面量数组 |
| `array_get { dst, arr, idx }` | 读元素，越界 panic |
| `array_set { arr, idx, val }` | 写元素，越界 panic |
| `array_len { dst, arr }` | 返回长度 (i32) |

### 示例：`new int[] { 1, 2, 3 }`

```
%r0 = const.i32 1
%r1 = const.i32 2
%r2 = const.i32 3
%arr = array_new_lit [%r0, %r1, %r2]
```

### 示例：`arr[i] += 1`（结合复合赋值）

```
%v   = array_get %arr, %i
%one = const.i32 1
%v2  = add %v, %one
       array_set %arr, %i, %v2
```

---

## VM 扩展（Rust）

`Value` 枚举新增：
```rust
Array(Rc<RefCell<Vec<Value>>>)
```

- 用 `Rc<RefCell<...>>` 实现引用语义和可变性
- 越界时 `bail!("array index {} out of bounds (len={})", idx, len)`

---

## TypeChecker 扩展

- `T[]` 对应 `Z42ArrayType { elem: Z42Type }`（已在 `Z42Type.cs` 定义）
- `new T[n]`：检查 `n` 为 `int`
- `arr[i]`：检查 `arr` 为数组类型，`i` 为 `int`，结果类型为元素类型
- `.Length`：仅允许在数组类型上访问，返回 `int`

---

## 不在此规范范围内

- 多维数组 `T[,]` / jagged `T[][]`（Phase 2）
- `Array.Sort`、LINQ、`IEnumerable`（Phase 2）
- 协变数组（Phase 2）
