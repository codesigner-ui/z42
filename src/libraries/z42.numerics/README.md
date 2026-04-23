# z42.numerics — 数值抽象库

## 职责

承载泛型数值算法所需的接口抽象（L3-G2.5 INumber 迭代 1）。与 `z42.core`
分离，让数值抽象随业务需要按需引入，不给核心库加体积。

## src/ 核心文件

| 文件 | 类型 | 说明 |
|------|------|------|
| `INumber.z42` | `INumber<T>` | 泛型数值约束接口：`op_Add` / `op_Subtract` / `op_Multiply` / `op_Divide` / `op_Modulo` |

## 使用

primitive `int` / `long` / `float` / `double` 原生满足 `INumber<T>`（自身作为
type arg，self-referential），通过 VM 内置 builtin 路由到 corelib 算术函数。
用户自定义数值类型需声明 `: Std.Numerics.INumber<MyType>` 并实现 5 个方法。

```z42
T Double<T>(T x) where T: INumber<T> {
    return x.op_Add(x);
}
```

二元运算符 `a + b` 到 `a.op_Add(b)` 的自动 desugar 归 **迭代 2**（L3-G2.5
INumber 后续）。

## 依赖

- `z42.core`（基础接口与 Object）
