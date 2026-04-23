# z42.core — 核心库

## 职责

所有 z42 程序隐式依赖的基础类型，对应 .NET `System` 命名空间核心部分。

## src/ 核心文件

| 文件 | 内容 |
|------|------|
| `Object.z42` | 所有类型的基类，`ToString()`、`Equals()` 等协议方法 |
| `String.z42` | 字符串类型，实例方法和静态方法（含 `IComparable<string>` / `IEquatable<string>` 实现） |
| `Int.z42` | `struct int` — 整数基元类型（含 `Parse` / `CompareTo` / `Equals` / `GetHashCode` / `ToString`，实现 `IComparable<int>` / `IEquatable<int>`）|
| `Double.z42` | `struct double` — 浮点基元类型（同 Int） |
| `Bool.z42` | `struct bool` — 布尔基元类型（只实现 `IEquatable<bool>`，ordering 无自然定义）|
| `Char.z42` | `struct char` — 字符基元类型 |
| `Type.z42` | 运行时类型对象（`typeof` 运算符返回值） |
| `Assert.z42` | 断言工具（`Assert.True`、`Assert.Equal` 等） |
| `Convert.z42` | 类型转换工具 |
| `IEquatable.z42` | 相等性接口 |
| `IComparable.z42` | 比较接口 |
| `IDisposable.z42` | 资源释放接口 |

### primitive-as-struct 设计（L3-G4b 重构）

`int` / `double` / `bool` / `char` 以 **`struct <小写名>`** 形式声明；`string`
仍保留 uppercase `class String`（规范化映射）。这是 C# BCL 模式的对齐：
声明层面 primitive = struct，运行时层面 VM 仍用 unboxed `Value::I64` 等。

加入新接口（例如 `INumber<int>`）只需在对应 `.z42` 文件给 struct 头部加
`, INumber<int>` 并添加 extern 方法 —— 编译器 / VM 零改动。详见
`docs/design/generics.md` → primitive-as-struct 小节。

`Object.z42ir.json`、`String.z42ir.json` 为预编译 IR JSON，供 VM 直接加载。
