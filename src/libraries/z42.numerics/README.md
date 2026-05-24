# z42.numerics — 数值库

## 职责

z42 任意精度 + 扩展数值类型。v0 仅 BigInt（arbitrary-precision integer）。
未来 Vector<T> / Complex / Decimal 走 follow-up spec。

## src/ 核心文件

| 文件 | 类型 | 说明 |
|------|------|------|
| `BigInt.z42` | `BigInt` | 任意精度整数；construct / Parse(decimal+hex) / Add/Sub/Mul/Div/Mod / Pow / CompareTo / ToString |

## 入口点

- `Std.Numerics.BigInt`

## 依赖关系

- `z42.core` — 基础类型 / 异常

## 实现策略

纯脚本，无 VM 改动。Magnitude 用 `int[]` little-endian 31-bit limb（每 limb
存 0..2^31-1，留 1 bit 给 mul 中间结果 fit `long` i64）；sign 用 `int _sign`
(-1/0/+1)。详 [docs/design/stdlib/numerics.md](../../../docs/design/stdlib/numerics.md)。
