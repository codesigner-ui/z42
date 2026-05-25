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

### ~~`bigint-future-modpow`~~ — **✅ 已落地 2026-05-25 (add-bigint-modpow)**

Shipped: `BigInt.ModPow(BigInt exp, BigInt modulus) → BigInt` —
square-and-multiply (LSB-first) over `TestBit` + `ShiftRight`. v0
constraints: `modulus > 0`, `exp >= 0` (negative exp needs modular
inverse — separate spec), `exp == 0` → `1` (mathematical convention),
`modulus == 1` → `0`. Reduces O(2^N) intermediate-size explosion of
`Pow().Mod()` to O(N) modular multiplications — RSA / DH viable. 13
tests cover small + large exp, edge cases (modulus 1, exp 0, base 0),
toy RSA round-trip (p=11, q=13, e=7, d=103), negative-arg rejection,
negative-base normalisation.

Montgomery / Barrett reduction (constant-factor speedups) deferred as
`bigint-future-modpow-montgomery`.

### `bigint-future-modpow-montgomery`

- **来源**：add-bigint-modpow v0 scope cut
- **触发原因**：v0 ModPow does naïve `Multiply().Mod()` per bit;
  Montgomery / Barrett reduction skips the per-iteration trial
  division for ~2-3× speedup on cryptographic-size operands
- **触发条件**：real-world crypto perf needs (RSA-2048 ~ 1ms target)
- **当前 workaround**：v0 correctness is fine; speed is the issue

### ~~`bigint-future-modpow-negexp`~~ — **✅ 已落地 2026-05-25 (add-bigint-modpow-negexp)**

Shipped: `BigInt.ModPow(exp, modulus)` now routes negative `exp` through
`ModInverse`: `a^(-n) mod m == (a^-1 mod m)^n mod m`. Requires
`gcd(a, m) == 1` (ModInverse propagates ArgumentException if not).
Test coverage in `bigint_modinverse.z42` (3 new tests: matches
ModInverse for exp=-1, higher negative power 3^-3 mod 11 = 9, RSA-style
two-step equivalence) + `bigint_modpow.z42` updated `test_modpow_negative_
exp_throws` → `test_modpow_negative_exp_computes_via_inverse` (no longer
throws) + new `test_modpow_negative_exp_not_coprime_throws` for the
gcd != 1 propagation path.

### ~~`bigint-future-gcd`~~ — **✅ Gcd / Lcm 已落地 2026-05-25 (add-bigint-gcd)**

Shipped: `BigInt.Gcd(BigInt) → BigInt` (classical Euclidean —
`gcd(a, b) = gcd(b, a mod b)`, operands taken as absolute value, result
always non-negative; conventions match .NET / Python: `gcd(0, 0) == 0`,
`gcd(a, 0) == |a|`) and `BigInt.Lcm(BigInt) → BigInt` (closed form
`|a*b| / gcd(a, b)`; `lcm(0, _) == 0` to avoid div-by-zero). 17 tests
cover classic / coprime / equal / zero / negative / cross-limb / Fib
adjacency / gcd-lcm-product identity. Binary GCD (Stein's algorithm)
deferred as `bigint-future-gcd-binary` (constant-factor optimization).

### `bigint-future-gcd-binary` — Binary GCD (Stein's algorithm)

- **来源**：add-bigint-gcd v0 scope cut
- **触发原因**：v0 Euclidean Gcd does `Mod` per iteration; binary GCD
  replaces division with shifts + subtractions — 2–3× faster on
  cryptographic-size operands where division dominates
- **触发条件**：bench shows gcd is on hot path for large operands
- **当前 workaround**：v0 correctness is fine; only speed differs

### ~~`bigint-future-prime`~~ — **✅ 已落地 2026-05-25 (add-bigint-prime)**

Shipped: `BigInt.IsProbablyPrime(int rounds) → bool` (Miller-Rabin with
wall-clock-seeded `Std.Random.Random`) + `IsProbablyPrime(int rounds,
Random rng) → bool` (caller-provided RNG for deterministic / reproducible
witnesses) + `NextPrime() → BigInt` (smallest probable prime > this; uses
20 Miller-Rabin rounds internally). Adds `z42.random` as `z42.numerics`
dependency (Std.Random.Random is PCG-XSH-RR; non-CSPRNG, but Miller-Rabin
correctness depends only on witness distribution, not unpredictability).

22 tests cover small primes (2 / 3 / classic list up to 97) / small
composites (4, 6, 8, 9, 15, 21, 25, 91) / Carmichael discriminators
(561 = 3·11·17; 1105 = 5·13·17 — Fermat-fooling, Miller-Rabin catches) /
Mersenne 2^31-1 / NextPrime from zero / one / negative / various small
starts / fast-rejects (negative, even>2) / error path (rounds<=0) /
RNG-determinism reproducibility / default-overload smoke test.

Follow-ups deferred:
- `bigint-future-prime-deterministic` — small-bound deterministic
  Miller-Rabin (known witness sets per OEIS A014233; valid for
  `n < 3,317,044,064,679,887,385,961,981` etc.)
- `bigint-future-bpsw` — Baillie–PSW; known no counterexample, slightly
  slower per round; deterministic up to current numerical tests
- ~~`bigint-future-prime-sieve`~~ — **✅ 已落地 2026-05-25 (add-bigint-prime-sieve)** —
  NextPrime 用 2-3 wheel（仅 6k±1 candidates，跳过 2/3 倍数）+ 小素数
  trial division (primes 3..31) 在 Miller-Rabin 之前 early-reject。5 new
  tests cover full sweep 4→30, composite-trial-div skip (e.g. 25 = 5²),
  large composite 1000 → 1009 (路上 1001/1003/1007 都被 trial-div 拦截),
  mod-6 landing cases for primes 7 / 11.

### ~~`bigint-future-modinverse`~~ — **✅ 已落地 2026-05-25 (add-bigint-modinverse)**

Shipped: `BigInt.ModInverse(BigInt modulus) → BigInt` — iterative
extended Euclidean algorithm. Returns `x ∈ [0, modulus)` such that
`(this * x) mod modulus == 1`. v0 限制：`modulus > 1`; gcd != 1 →
`ArgumentException`. 负 `this` 先 `Mod(modulus)` 归一化；result 总
非负。15 tests cover classic 3^-1 mod 11 = 4, RSA toy round-trip
(p=11, q=13, e=7, d=103, m=42), negative-this normalisation,
inverse*original ≡ 1 identity, error paths (modulus ≤ 1, negative,
not coprime, this == 0, multiple of modulus), large Mersenne-31
operand, Fermat-little-theorem cross-check on prime modulus
(`a^-1 ≡ a^(p-2)` agrees between ModInverse and ModPow).

ModPow 负指数自动路由（`a^(-n) mod m` 内部走 ModInverse） 留 follow-up
`bigint-future-modpow-negexp`。

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
