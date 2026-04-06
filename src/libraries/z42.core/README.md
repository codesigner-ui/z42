# z42.core — 核心库

## 职责

所有 z42 程序隐式依赖的基础类型，对应 .NET `System` 命名空间核心部分。

## src/ 核心文件

| 文件 | 内容 |
|------|------|
| `Object.z42` | 所有类型的基类，`ToString()`、`Equals()` 等协议方法 |
| `String.z42` | 字符串类型，实例方法和静态方法 |
| `Type.z42` | 运行时类型对象（`typeof` 运算符返回值） |
| `Assert.z42` | 断言工具（`Assert.True`、`Assert.Equal` 等） |
| `Convert.z42` | 类型转换工具 |
| `IEquatable.z42` | 相等性接口 |
| `IComparable.z42` | 比较接口 |
| `IDisposable.z42` | 资源释放接口 |

`Object.z42ir.json`、`String.z42ir.json` 为预编译 IR JSON，供 VM 直接加载。
