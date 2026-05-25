# Tasks: add BigInt.ModInverse (modular multiplicative inverse)

> 状态：🟢 已完成 | 创建：2026-05-25 | 归档：2026-05-25
> 类型：feat (extend existing class) | Spec 类型：minimal mode

**变更说明**：在 `Std.Numerics.BigInt` 上加 `ModInverse(BigInt modulus) → BigInt`，
返回 `x ∈ [0, modulus)` 使得 `(this * x) mod modulus == 1`。算法用迭代版
扩展欧几里得 (`a*x + m*y = gcd(a, m)`)；若 `gcd(a, m) != 1` 抛 `ArgumentException`。

**原因**：
- ModPow 负指数路径（`a^(-n) mod m == (a^(-1))^n mod m`）的前置条件
- RSA 私钥派生：给定 `(e, φ(n))` 求 `d = e^(-1) mod φ(n)` 是 RSA key gen 标准流程
- 数论应用（Bezout 系数、线性同余方程求解）

**算法（迭代扩展欧几里得）**：
```
g0 = m, g1 = a (normalised into [0, m))
x0 = 0, x1 = 1
while !g1.IsZero():
    q = g0 / g1
    (g0, g1) = (g1, g0 - q*g1)
    (x0, x1) = (x1, x0 - q*x1)
// invariant: g0 == gcd(a, m); x0 satisfies x0 * a ≡ g0 (mod m)
if !g0.IsOne(): throw "not coprime"
return ((x0 mod m) + m) mod m   // normalize to [0, m)
```

**v0 限制 / 约定**：
- `modulus > 1` — `modulus == 1` 时所有数模 1 都是 0，没有 invertible 元素；
  `modulus <= 0` 数学上无定义；皆抛 `ArgumentException`
- `gcd(this, modulus) != 1` → 抛 `ArgumentException("inverse does not exist")`
- 负 `this` 先 `Mod(modulus)` 归一化到 `[0, modulus)` 再计算
- `this == 0` → `gcd(0, m) == m != 1` 走入 not-coprime 分支抛错

**Out of scope（follow-up）**：
- ModPow 负指数自动路由（内部调用 ModInverse 再正向 ModPow）— 待 ModInverse 落地后单独
  做 follow-up spec `bigint-future-modpow-negexp`
- `ExtendedGcd(BigInt) → (BigInt, BigInt, BigInt)` 公开 Bezout 系数 — 需要
  z42 tuple/struct return；当前 BigInt API 全单返回值，暂不引入

**文档影响**：
- `numerics.md`：flip `bigint-future-modinverse` Deferred → ✅ landed
- `roadmap.md`：本变更不直接在 Deferred Index，因 `bigint-future-modinverse`
  仅存于 numerics.md（属于 BigInt 内部 follow-up，roadmap 未单列索引行）

## Tasks

- [x] 1.1 `BigInt.z42`: `public BigInt ModInverse(BigInt modulus)` — iterative ext-Euclidean
- [x] 2.1 NEW `tests/bigint_modinverse.z42` — 16 tests (basic, RSA toy round-trip with ModPow, negative-this, range invariant, product identity, all 5 error paths, Mersenne-31 large, Fermat-little-theorem cross-check)
- [x] 3.1 `numerics.md`: flip `bigint-future-modinverse` Deferred → ✅ landed + 新增 `bigint-future-modpow-negexp` follow-up
- [x] 4.1 GREEN (stdlib scope 135/135 files; 16/16 new modinverse tests pass) + archive + commit + push

## 备注

GREEN 限定 `--scope=stdlib`，与并行 session Rust VM WIP 解耦（本变更零 Rust 改动）。
