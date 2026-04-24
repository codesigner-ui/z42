# z42.core — 核心库

## 职责

所有 z42 程序隐式依赖的基础类型，对应 .NET `System` 命名空间核心部分。

## src/ 核心文件

| 文件 | 内容 |
|------|------|
| `Object.z42` | 所有类型的基类，`ToString()`、`Equals()` 等协议方法 |
| `String.z42` | 字符串类型；最小 intrinsic 核（`Length` / `CharAt` / `FromChars` / `Equals` / `CompareTo` / `GetHashCode` / `ToString` / `Split` / `Join` / `Concat` / `Format`），其余方法（`Contains` / `StartsWith` / `EndsWith` / `IndexOf` / `Replace` / `Substring` / `ToLower` / `ToUpper` / `Trim*` / `IsNullOr*`）为纯脚本实现 |
| `Int.z42` | `struct int` — 整数基元（`Parse` / `CompareTo` / `Equals` / `GetHashCode` / `ToString` + INumber op_* 纯脚本实现）|
| `Long.z42` | `struct long` — 64-bit 整数（同 Int）|
| `Double.z42` | `struct double` — 双精度浮点（同 Int）|
| `Float.z42` | `struct float` — 单精度浮点（VM 用 F64 存储）|
| `Bool.z42` | `struct bool` — 布尔（只实现 `IEquatable<bool>`）|
| `Char.z42` | `struct char` — 字符（`CompareTo` / `Equals` / `GetHashCode` / `ToString` / `IsWhiteSpace` / `ToLower` / `ToUpper`）|
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

INumber 的 `op_Add` 等通过**纯脚本 body** 实现（`return a + b;`，2026-04-24 迁移到
C# 11 static abstract 形式），编译后走 IR AddInstr 等指令 — **零新 VM builtin，
零 codegen 特化**，遵守 Script-First。

### String extern 预算（2026-04-25 simplify-string-stdlib）

C# BCL 对齐：`string.Length` / `string[i]` 相当于 `[Intrinsic]` property；其余
方法（`Contains` / `IndexOf` / `Trim` / `Substring` / `ToLower` / `Replace`）用
C# 代码循环字符实现。z42 照此：

| 保留 extern（11 个）| 说明 |
|-------|------|
| `Length` / `CharAt` / `FromChars` | 最小 intrinsic 核；循环基础 |
| `Equals` / `CompareTo` / `GetHashCode` / `ToString` | Object 协议 |
| `Split` / `Join` / `Concat` / `Format` | 分配 / 变参 / 格式串较复杂，保留 |

| 迁移到脚本（11 个）| 实现方式 |
|-------|---------|
| `IsEmpty` (property) | `Length == 0` |
| `Contains` / `StartsWith` / `EndsWith` / `IndexOf` | CharAt 循环 |
| `Substring` / `Replace` / `ToLower` / `ToUpper` | `char[]` + `FromChars` |
| `Trim` / `TrimStart` / `TrimEnd` | `IsWhiteSpace` 扫描 |
| `IsNullOrEmpty` / `IsNullOrWhiteSpace` (static) | null 检查 + 循环 |

**索引语义统一**：所有索引 / 长度 / 切片按 **Unicode scalar (char)** 计数；
UTF-8 byte 视图不对外暴露。ASCII 场景与旧行为完全等价。
**Casing 语义**：ASCII 规则（`'A'..'Z'` ↔ `'a'..'z'`），locale-sensitive
casing 延后到 L3 `CultureInfo`。
