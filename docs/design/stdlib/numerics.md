# z42.numerics

Extended numeric types — BigInt today, `Vector<T>` / `Complex` / `Decimal`
follow-up specs.

## v0 scope (add-z42-numerics-bigint, 2026-05-24)

**F v0 = BigInt only**. Pure script, no VM builtin / Rust changes.

### Public API

```z42
namespace Std.Numerics;

public class BigInt {
    // Constants
    public static BigInt Zero;
    public static BigInt One;
    public static BigInt MinusOne;

    // Construction
    public BigInt(int value);
    public BigInt(long value);
    public static BigInt Parse(string s);          // decimal, optional "-" / "+"
    public static BigInt ParseHex(string s);       // hex, optional "-" / "0x" prefix

    // Inspection
    public bool IsZero();
    public bool IsOne();
    public bool IsNegative();
    public int  Sign();   // -1, 0, +1

    // Conversion
    public int  ToInt32();   // throws InvalidOperationException on overflow
    public long ToInt64();   // throws InvalidOperationException on overflow
    override string ToString();  // decimal
    public string ToHex();   // lowercase hex, no "0x" prefix; "-" prefix for negative

    // Arithmetic
    public BigInt Add(BigInt other);
    public BigInt Subtract(BigInt other);
    public BigInt Multiply(BigInt other);   // schoolbook O(n*m)
    public BigInt Divide(BigInt other);     // truncated toward zero
    public BigInt Mod(BigInt other);        // remainder same sign as dividend
    public BigInt Negate();
    public BigInt Abs();
    public BigInt Pow(int exponent);        // exponent >= 0, repeated squaring

    // Comparison
    public int  CompareTo(BigInt other);
    override bool Equals(object other);
    override int  GetHashCode();
}
```

### Internal representation

| Field | Type | Purpose |
|-------|------|---------|
| `_mag` | `int[]` | Magnitude as little-endian 31-bit limbs; each limb in `[0, 2^31)` |
| `_sign` | `int` | `-1` (negative), `0` (zero — `_mag.Length == 0`), `+1` (positive) |

**Normalised**: no trailing zero limbs (`_mag.Length == 0` iff `_sign == 0`).

### 31-bit limb design

z42 `int` is signed i32 (max `2^31-1`). Two choices:

1. **32-bit limbs stored in `long`** — full byte alignment; multiplication of two
   32-bit values is 64-bit, just fits `long` i64 but **no carry headroom**.
2. **31-bit limbs stored in `int`** — single hex digit lost per limb (4.6%
   capacity); but multiplication of two 31-bit values = 62 bits, leaving
   1 bit headroom for carry. Add chain has no wraparound concerns.

Chosen **(2)** for arithmetic cleanliness. Trade-off: ToHex needs divmod-by-16^7
chunks (28-bit boundaries) instead of direct per-limb hex dump.

### Algorithms

| Operation | Algorithm | Complexity |
|-----------|-----------|------------|
| Add / Sub | linear carry/borrow over limbs | O(max(n, m)) |
| Multiply | schoolbook (Karatsuba follow-up) | O(n × m) |
| Divide / Mod (single-limb divisor) | linear long-division | O(n) |
| Divide / Mod (multi-limb divisor) | top-2-limb estimate + correct-down | O(n × m) avg, O(n × m²) worst |
| Pow | repeated squaring | O(log exp) multiplies |
| Parse decimal | running × 10 + digit | O(n²) digits (multiply is O(n)) |
| Parse hex | running × 16 + digit | O(n²) hex chars |
| ToString | repeated divmod-by-10⁹ chunks | O(n²) digits |
| ToHex | repeated divmod-by-16⁷ chunks | O(n²) hex chars |

### Test coverage

- `tests/bigint_basic.z42` — construction + inspection + small CompareTo / Equals / ToInt32 overflow
- `tests/bigint_arithmetic.z42` — Add / Sub / Mul / Div / Mod with negative / zero / cross-limb / large-multi-limb cases
- `tests/bigint_parse.z42` — Parse(decimal + hex) + format errors + 100-digit round trip
- `tests/bigint_pow.z42` — small + large (2^100) + factorial(20!) + neg-exponent throws

39 tests, all passing.

## Out of Scope (follow-up specs)

### ~~`bigint-future-bitops`~~ — **✅ 已落地 2026-05-25 (add-bigint-bitops)**

Shipped: `And` / `Or` / `Xor` (v0 non-negative operands only),
`ShiftLeft(int)` / `ShiftRight(int)` (magnitude shift preserving
sign — non-arithmetic; `(-8 >> 1) == -4`), `TestBit(int)` (non-negative
receiver only), `BitLength()` (magnitude bit-count; `(-256).BitLength()
== 9`). 31 tests cover the byte-pattern / huge-mask / round-trip /
negative-amount / zero / overshoot / negative-receiver cases. Two's-
complement semantics for negatives (`Not` + signed-bitwise) is the
follow-up `bigint-future-bitops-twoscomp`.

### `bigint-future-bitops-twoscomp` — two's-complement bit-ops on negatives

- **来源**：add-bigint-bitops v0 scope cut (negatives currently throw
  ArgumentException for And/Or/Xor/TestBit; ShiftRight on negatives
  uses magnitude shift, not the .NET-style arithmetic shift toward -∞)
- **触发原因**：proper two's-complement requires a virtual infinite-
  sign-extension scheme over the sign+magnitude representation; non-
  trivial and rarely needed for the v0 use cases (crypto / bitset on
  non-negative big ints)
- **触发条件**：first real use case for negative-operand bitwise ops
  (signed serialization / bit-manipulation tricks on negatives)
- **当前 workaround**：take `.Abs()` first, do the op, then re-sign
  appropriately

### `bigint-future-modpow` — modular exponentiation `ModPow(exp, modulus)`

- **来源**：F v0 explicit deferral
- **触发原因**：RSA / DH 必备但需要左到右 windowed exp + Montgomery reduction 才高效
- **触发条件**：z42.crypto RSA / ECDH 支持
- **当前 workaround**：`a.Pow(b).Mod(m)`（指数爆炸，仅 toy 用例可用）

### `bigint-future-gcd` — Gcd / Lcm / IsProbablyPrime / NextPrime

- **来源**：数论 / RSA key gen 用例
- **触发条件**：crypto / 验证程序需求

### `bigint-future-karatsuba-fft` — sub-quadratic multiplication

- **来源**：v0 schoolbook O(n²) 对 < 1000 位数 OK，更大场景慢
- **触发条件**：bench 显示乘法成瓶颈 + 实际用例 (1000+ 位数 RSA / 大整数 ML)

### `bigint-future-operator-overloads` — `a + b` / `a * b` syntax

- **来源**：BCL `System.Numerics.BigInteger` 有 op_Addition 等
- **触发原因**：z42 当前 op overloading 限 primitive types
- **触发条件**：L3 generic op overload

### `numerics-future-vector` — `Std.Numerics.Vector<T>` SIMD

- **来源**：z42.numerics P2 roadmap entry
- **触发条件**：图形 / ML / 信号处理用例

### `numerics-future-complex` — `Std.Numerics.Complex` 复数

- **触发条件**：信号处理 / 量子计算用例

### `numerics-future-decimal` — `Std.Numerics.Decimal` 定点小数

- **触发条件**：金融 / 货币精确计算用例
