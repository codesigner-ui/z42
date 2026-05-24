# Tasks: add z42.numerics BigInt v0

> 状态：🟢 已完成 | 创建：2026-05-24 | 类型：feat (new stdlib pkg, pure script)
> Spec 类型：minimal mode

**变更说明**：新增 `Std.Numerics.BigInt` — arbitrary-precision 整数。纯脚本实现，无新 VM builtin / IR / 语法。z42.numerics 是 P2 roadmap entry；F v0 = BigInt only（`Vector<T>` / `Complex` / `Decimal` 留 follow-up）。

**为什么需要**：

- 大数加密 (RSA / Diffie-Hellman 等) 需要 ModPow（follow-up）
- 阶乘 / 组合数 / 数论计算
- 任意精度算术
- C# BCL `System.Numerics.BigInteger` / Python `int` 对标 — 用户期待存在

## API (v0)

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
    public static BigInt Parse(string s);          // decimal, optional leading "-"
    public static BigInt ParseHex(string s);       // hex, optional "-" or "0x" prefix

    // Inspection
    public bool IsZero();
    public bool IsOne();
    public bool IsNegative();
    public int Sign();   // -1, 0, +1

    // Conversion
    public int  ToInt32();   // throws OverflowException
    public long ToInt64();   // throws OverflowException
    public string ToString();  // decimal
    public string ToHex();     // hex, lowercase, no "0x" prefix; "-" for negative

    // Arithmetic
    public BigInt Add(BigInt other);
    public BigInt Subtract(BigInt other);
    public BigInt Multiply(BigInt other);
    public BigInt Divide(BigInt other);   // truncated toward zero
    public BigInt Mod(BigInt other);      // result sign = dividend sign (truncation semantics)
    public BigInt Negate();
    public BigInt Abs();
    public BigInt Pow(int exponent);       // exponent >= 0, repeated squaring

    // Comparison
    public int  CompareTo(BigInt other);
    override bool Equals(object other);
    override int GetHashCode();
}
```

## Internal representation

- `int[] _mag` — magnitude as little-endian 31-bit limbs (each limb in `[0, 2^31)` — signed i32 positive range)
- `int _sign` — `-1` (negative), `0` (zero, _mag == empty), `+1` (positive)
- Normalised: no trailing zero limbs

**31-bit limb choice rationale**：z42 `int` 是 signed i32，最大正数 2^31-1 ≈ 2.1e9。若用 32-bit unsigned limb，必须把 `uint32` 存到 z42 `long` (i64)。但 multiplication of two 32-bit limbs = 64-bit，刚好爆 i64。  
用 31-bit limb 后，两 31-bit limb 乘积 = 62-bit，留 1 bit 给 carry，仍 fit i64。Add carry chain 也清爽。

trade-off：限制单 limb 容量 4.6%（vs 32-bit），换 carry/multiply 路径全部 z42 `long` 无 wraparound。

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.numerics/z42.numerics.z42.toml` | NEW | manifest (deps z42.core) |
| `src/libraries/z42.numerics/README.md` | NEW | package README |
| `src/libraries/z42.numerics/src/BigInt.z42` | NEW | core class — arithmetic + parse + ToString |
| `src/libraries/z42.numerics/tests/bigint_basic.z42` | NEW | Construct + ToString + ToInt32/64 + comparison |
| `src/libraries/z42.numerics/tests/bigint_arithmetic.z42` | NEW | Add / Sub / Mul / Div / Mod + edge cases (zero / overflow) |
| `src/libraries/z42.numerics/tests/bigint_parse.z42` | NEW | Parse decimal + hex + invalid format + round trip |
| `src/libraries/z42.numerics/tests/bigint_pow.z42` | NEW | Pow + large values (2^100 等) |
| `src/libraries/z42.workspace.toml` | MODIFY | default-members + 加 z42.numerics |
| `scripts/build-stdlib.z42` | MODIFY | `_stdlibList()` + `_indexJson()` 加 z42.numerics / Std.Numerics |
| `docs/design/stdlib/numerics.md` | NEW | design doc (v0 scope + Deferred) |
| `docs/design/stdlib/roadmap.md` | MODIFY | P2 z42.numerics 行加✅；Deferred Backlog Index 加 ID |
| `docs/design/stdlib/organization.md` | MODIFY | 现状段加 z42.numerics |
| `docs/design/stdlib/overview.md` | MODIFY | Module Catalog 加 Std.Numerics 段 |

## Out of Scope（独立 follow-up spec）

- **Bit ops**（AND / OR / XOR / NOT / Shift）— 需要 crypto RSA / hash 用例驱动
- **ModPow（modular exponentiation）** — RSA 必备，但 v0 不引入；独立 `add-bigint-modpow` spec
- **Gcd / Lcm / IsProbablyPrime / NextPrime** — 数论用例驱动
- **Karatsuba / FFT multiplication** — schoolbook O(n²) 对 v0 用例足够；perf bench 驱动后续
- **`Vector<T>`** — z42.numerics 另一支柱，独立 spec
- **`Complex` / `Decimal`** — 数值类型扩展，独立 spec
- **Operator overloading**（`a + b` 写法）— z42 当前 op_Add 仅 primitive；类级 op_overload 是 L3 特性

## Tasks

- [x] 1.1 `src/libraries/z42.numerics/z42.numerics.z42.toml` NEW (deps z42.core)
- [x] 1.2 `src/libraries/z42.numerics/README.md` NEW
- [x] 1.3 `src/libraries/z42.workspace.toml` MODIFY — default-members 加 z42.numerics
- [x] 1.4 `scripts/build-stdlib.z42` MODIFY — `_stdlibList()` + `_indexJson()`
- [x] 2.1 `src/libraries/z42.numerics/src/BigInt.z42` NEW:
  - 字段: `int[] _mag`, `int _sign`
  - 静态构造工厂: `_fromMagSign(mag, sign)` (内部 normaliser)
  - `BigInt(int)` / `BigInt(long)` 构造器
  - `Zero / One / MinusOne` 静态字段
  - `IsZero / IsOne / IsNegative / Sign` accessor
  - `Add` / `Subtract` (绝对值 magnitude add/sub + 符号决策)
  - `Multiply` (schoolbook)
  - `Divide` / `Mod` (long division)
  - `Negate` / `Abs`
  - `Pow(int exp)` (repeated squaring)
  - `CompareTo` / `Equals` / `GetHashCode` 
  - `ToInt32` / `ToInt64` (overflow check)
  - `ToString` (decimal): repeated divmod-by-10⁹
  - `ToHex`: limb-wise hex emit + leading zero trim
  - `Parse(string)`: decimal chunked by 10⁹
  - `ParseHex(string)`: hex chunked
- [x] 2.2 内部 helper:
  - `_magCmp(int[] a, int[] b) -> int` (magnitude-only compare)
  - `_magAdd(int[] a, int[] b) -> int[]`
  - `_magSub(int[] a, int[] b) -> int[]` (assumes a >= b)
  - `_magMul(int[] a, int[] b) -> int[]`
  - `_magDivMod(int[] dividend, int[] divisor) -> [quotient, remainder]`
- [x] 3.1 `bigint_basic.z42` — construct from int / long; ToString of Zero/One/MinusOne/123/-456/huge; ToInt32 round trip + overflow throw
- [x] 3.2 `bigint_arithmetic.z42` — Add / Sub / Mul / Div / Mod 各 5+ 案例; (-7) / 3 == -2 (truncation), (-7) % 3 == -1
- [x] 3.3 `bigint_parse.z42` — Parse("0") / Parse("123") / Parse("-456") / Parse("99999999999999999999"); ParseHex("ff") / ParseHex("0x1A2B"); round trip Parse→ToString
- [x] 3.4 `bigint_pow.z42` — Pow(2, 10) == 1024; Pow(2, 100) huge; Pow(3, 0) == 1; Pow(-2, 3) == -8
- [x] 4.1 `docs/design/stdlib/numerics.md` NEW
- [x] 4.2 `docs/design/stdlib/roadmap.md` MODIFY (P2 numerics 行 + Deferred index)
- [x] 4.3 `docs/design/stdlib/organization.md` MODIFY
- [x] 4.4 `docs/design/stdlib/overview.md` MODIFY
- [x] 5.1 `./scripts/build-stdlib.sh` z42.numerics 编译通过
- [x] 5.2 `./scripts/test-stdlib.sh z42.numerics` 全过
- [x] 5.3 commit + push + archive 到 `docs/spec/archive/2026-05-24-add-z42-numerics-bigint/`

## 备注 / 实施期发现

- BigInt v0 scope **不含** ModPow / 位运算 / Gcd — 独立 follow-up spec 按 crypto 用例驱动
- 31-bit limb 设计已在内部表示段说明
- Pure script — 无新 VM builtin，无 Rust 改动
- **z42 不支持 `int[][]` (jagged 2D arrays)**：原设计想用 `int[][]` 返回 `[quotient, remainder]`，实施期改用 `_MagDivModResult { int[] q; int[] r; }` helper class
- **z42 不支持 `s[i]` string indexing**：原 Parse / ParseHex 用 `s[i]` 取字符，改 `s.CharAt(i)` 与 stdlib 既有风格一致
- **`Long.MinValue` 字面量 (`-9223372036854775808L`) parser overflow**：z42 parser 把 negative literal 解析成 `negate(positive literal)`，positive 9223372036854775808 超 i64 max。绕开用 `(-9223372036854775807L - 1L)`
- **`int` literal `2166136261` 超 i32**：FNV-1a offset basis 0x811C9DC5 落在 unsigned u32 范围；z42 `int` signed i32 上限 2^31-1。简化用减少 collision basis 的 31\*shift\*xor hash
- **`OverflowException` / `DivideByZeroException` z42.core 未提供**：用 `InvalidOperationException` 覆盖。follow-up spec 加 `Std.OverflowException` + `Std.DivideByZeroException`
- **方法链 `result.Multiply(ten).Add(...)` 编译失败**：被 type checker 误判为 "BigInt has no method Add"；拆分成两行 `BigInt scaled = result.Multiply(ten); result = scaled.Add(...);` 绕开。z42 method chain resolver bug，独立 follow-up
- **31-bit limb ToHex 不能 per-limb 直接 emit**：bit-boundary 不与 hex 4-bit 对齐；改用 divmod-by-16⁷ chunks (28-bit boundaries)
- 测试：39/39 全过 (basic 8 + arithmetic 14 + parse 10 + pow 7)
