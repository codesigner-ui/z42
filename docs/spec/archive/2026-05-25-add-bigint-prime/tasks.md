# Tasks: add BigInt.IsProbablyPrime + NextPrime (Miller-Rabin)

> 状态：🟢 已完成 | 创建：2026-05-25 | 归档：2026-05-25
> 类型：feat (extend existing class + add z42.random dep) | Spec 类型：minimal mode

**变更说明**：在 `Std.Numerics.BigInt` 上加 `IsProbablyPrime(int rounds)` /
`IsProbablyPrime(int rounds, Random rng)` 和 `NextPrime()`。Miller-Rabin
witness-based probabilistic primality test，witnesses 从 `Std.Random.Random`
取（不是 CSPRNG — 但 Miller-Rabin 对 witness 质量的要求只是 "uniformly
distributed enough to surface composites in expected rounds"，非加密 RNG
足够）。NextPrime 用 IsProbablyPrime 串行扫描 candidate。

**原因**：
- RSA / Diffie-Hellman key generation 闭环：现在 Gcd + ModPow + ModInverse 已
  到位，缺最后一块 prime generation
- 数论 / 验证程序 / 教学场景
- 完成 BigInt 数论工具集（add-bigint-bitops → modpow → gcd → modinverse → prime）

**依赖变更**：`z42.numerics` 增加对 `z42.random` 的 dep（workspace topo 已
保证 random 先编译 → numerics）。Std.Random.Random 是 PCG-XSH-RR 通用 PRNG。

**算法（Miller-Rabin）**：
```
n = this; assume odd > 3 after quick rejects
write n - 1 = 2^r * d   (d odd, found by ShiftRight + TestBit)
repeat `rounds` times:
    a = random in [2, n - 2]
    x = a^d mod n
    if x == 1 or x == n - 1: continue (witness OK)
    repeat r - 1 times:
        x = x^2 mod n
        if x == n - 1: witness OK, break
    if no n-1 hit: return false (definitely composite)
return true (probably prime; error ≤ 4^(-rounds))
```

**Random injection 设计**：
- `IsProbablyPrime(int rounds)` → 内部 `new Random()`（wall-clock 默认 seed）
- `IsProbablyPrime(int rounds, Random rng)` → 接受 caller-provided RNG
  （seeded RNG 用于 deterministic 测试 + reproducible benchmark）
- 两个 arity 不同（1 vs 2）→ 不撞 `compiler-future-typed-overload-resolution` 已知 mangling 碰撞

**v0 限制 / 约定**：
- `rounds <= 0` → `ArgumentException`
- `this < 2` → `false`（包含 0 / 1 / negative）
- `this == 2` → `true`
- 偶数 > 2 → `false`（fast path）
- `this == 3` → `true`
- Witness 取自 `[2, n - 2]`；用 `_randomBelow(rng, n-3) + 2` 生成
  （Mod-bias 对 Miller-Rabin correctness 无影响，只略损 witness quality）

**NextPrime 约定**：
- `this < 2 → 2`
- 返回严格大于 `this` 的最小 (probably) prime
- 内部用 20 rounds Miller-Rabin（cryptographic-grade default）

**Out of scope（follow-up）**：
- Deterministic Miller-Rabin（small-number 已知 witness sets — 见 OEIS A014233；
  仅适用于 `n < 3,317,044,064,679,887,385,961,981` 等 bound 内）— 用例小，
  `bigint-future-prime-deterministic`
- BPSW (Baillie-PSW) test — known no counterexample but slower; `bigint-future-bpsw`
- Small-prime sieve in NextPrime — wheel factorisation 跳过 6k±1 候选；`bigint-future-prime-sieve`

**文档影响**：
- `numerics.md`：flip `bigint-future-prime` Deferred → ✅ landed + 新增三个 follow-up
- `z42.numerics.z42.toml`：add `"z42.random" = "0.1.0"` dep
- `roadmap.md`：本变更不直接在 Deferred Index（`bigint-future-prime` 仅存于 numerics.md）

## Tasks

- [x] 1.1 `z42.numerics.z42.toml`: 增加 `z42.random` dep
- [x] 1.2 `BigInt.z42`: `using Std.Random;` + `_randomBelow(rng, n)` private static helper
- [x] 1.3 `BigInt.z42`: `IsProbablyPrime(int rounds)` + `IsProbablyPrime(int rounds, Random rng)`
- [x] 1.4 `BigInt.z42`: `NextPrime()` (uses 20 rounds, wall-clock-seeded RNG)
- [x] 2.1 NEW `tests/bigint_prime.z42` — 23 tests (小素数 / 小合数 / Carmichael 561 + 1105 / Mersenne 2^31-1 / NextPrime 各起点 / fast-rejects / round=0 throws / seeded-RNG reproducibility / default-overload smoke)
- [x] 3.1 `numerics.md`: flip `bigint-future-prime` → ✅ landed + 新增 `bigint-future-prime-deterministic` / `bigint-future-bpsw` / `bigint-future-prime-sieve` follow-ups
- [x] 4.1 GREEN (stdlib scope: 23/23 new prime tests pass; numerics + random + downstream libs build clean) + archive + commit + push

## 备注

GREEN 在执行时 z42.yaml `parse_strings.z42` 与 `scripts/build-stdlib.z42`
均出现 compile error，但与本变更无关 — 是并行 session 正在进行的 Exception
类型重命名（`catch type 'Exception' not found`）。本变更纯 z42 源码（zero
Rust 改动），影响隔离于 z42.numerics + z42.random。Numerics 测试 23/23 全绿
（同次 run 的早期阶段）。
