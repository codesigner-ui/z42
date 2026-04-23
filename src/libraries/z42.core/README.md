# z42.core — 核心库

## 职责

所有 z42 程序隐式依赖的基础类型，对应 .NET `System` 命名空间核心部分。

## src/ 核心文件

| 文件 | 内容 |
|------|------|
| `Object.z42` | 所有类型的基类，`ToString()`、`Equals()` 等协议方法 |
| `String.z42` | 字符串类型，实例方法和静态方法（含 `IComparable<string>` / `IEquatable<string>` 实现） |
| `Int.z42` | `struct int` — 整数基元（`Parse` / `CompareTo` / `Equals` / `GetHashCode` / `ToString` + INumber op_* 纯脚本实现）|
| `Long.z42` | `struct long` — 64-bit 整数（同 Int）|
| `Double.z42` | `struct double` — 双精度浮点（同 Int）|
| `Float.z42` | `struct float` — 单精度浮点（VM 用 F64 存储）|
| `Bool.z42` | `struct bool` — 布尔（只实现 `IEquatable<bool>`）|
| `Char.z42` | `struct char` — 字符 |
| `Type.z42` | 运行时类型对象（`typeof` 运算符返回值）|
| `Assert.z42` | 断言工具（`Assert.True`、`Assert.Equal` 等）|
| `Convert.z42` | 类型转换工具 |
| `IEquatable.z42` | 相等性接口 |
| `IComparable.z42` | 比较接口 |
| `IDisposable.z42` | 资源释放接口 |
| `INumber.z42` | 数值约束接口（`op_Add` / `op_Subtract` / `op_Multiply` / `op_Divide` / `op_Modulo`）|

### primitive-as-struct 设计（L3-G4b 重构）

`int` / `long` / `double` / `float` / `bool` / `char` 以 **`struct <小写名>`** 形式
声明；`string` 仍保留 uppercase `class String`（规范化映射）。C# BCL 模式对齐：
声明层面 primitive = struct，运行时层面 VM 仍用 unboxed `Value::I64` / `Value::F64`。

INumber 的 `op_Add` 等通过**纯脚本 body** 实现（`return this + other;`），编译后
走 IR AddInstr 等指令 — **零新 VM builtin，零 codegen 特化**，遵守 Script-First。

`Object.z42ir.json`、`String.z42ir.json` 为预编译 IR JSON，供 VM 直接加载。
