# Tasks: add BigInt.IsPrime() deterministic primality

> 状态：🟢 已完成 | 创建：2026-06-02 | 归档：2026-06-02 | 类型：stdlib feat

## 进度概览
- [x] 阶段 1: Helpers (extended trial division + fixed-witness MR)
- [x] 阶段 2: Public `IsPrime()` with tiered witness selection
- [x] 阶段 3: Tests (13/13 green)
- [x] 阶段 4: Doc sync (numerics.md + roadmap.md) + verify + archive

## 阶段 1: Helpers
- [ ] 1.1 Add private `_smallPrimeTable42()` returning the 13 primes
      `[2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41]` (same set
      as the largest deterministic witness set; covers pre-trial
      division and matches the witness alphabet)
- [ ] 1.2 Add private `_trialDivideUpTo41(BigInt n) → int`: returns
      `0` if `n` is divisible by one of the 13 primes and not equal
      to it (definitely composite); `1` if `n` equals one of them
      (definitely prime); `2` if `n` is coprime to all (need
      Miller-Rabin). Keeps the existing `_trialDivideSmallPrimes` as
      a thin wrapper that maps `0 → false` and `1`/`2 → true` for
      backwards compatibility with `NextPrime`.
- [ ] 1.3 Add private `_millerRabinFixedWitness(BigInt witness,
      BigInt d, int r) → bool`: returns `true` if witness "passes"
      (probably-prime by that witness); `false` if witness proves
      composite. Caller pre-computes `(d, r)` so the loop reuses
      decomposition across witnesses.

## 阶段 2: Public `IsPrime()` + tier selector
- [ ] 2.1 Add public `bool IsPrime()` to BigInt
- [ ] 2.2 Special-cases: `_sign <= 0` / `IsOne()` → false; via
      pre-trial division handle small primes / small composites
- [ ] 2.3 Decompose `n - 1 = 2^r * d` once
- [ ] 2.4 Tier-select witness set by 13 ascending `CompareTo`
      comparisons against thresholds parsed from string constants
- [ ] 2.5 Run `_millerRabinFixedWitness` for each chosen witness
      (skipping any `a >= n` defensively, though pre-trial-division
      ensures `n > 41 >= max(small witness)`)
- [ ] 2.6 If above the top threshold, throw `ArgumentException`
      pointing the user at `IsProbablyPrime`

## 阶段 3: Tests
- [ ] 3.1 Create `src/libraries/z42.numerics/tests/bigint_prime_deterministic.z42`
- [ ] 3.2 `test_zero_negative_one_are_not_prime`
- [ ] 3.3 `test_small_primes_2_through_41_all_true`
- [ ] 3.4 `test_small_composites_all_false` — 4, 6, 8, 9, 15, 21, 25, 27, 33, 35, 49, 121
- [ ] 3.5 `test_mersenne_primes_in_range` — 2^13-1, 2^17-1, 2^19-1, 2^31-1, 2^61-1
- [ ] 3.6 `test_pseudoprime_2047_caught` — 2047 = 23 × 89 is a
      strong pseudoprime for base 2; tier should pick `{2, 3}` and
      identify composite
- [ ] 3.7 `test_pseudoprime_3215031751_caught` — known strong
      pseudoprime for bases 2, 3, 5, 7; tier should pick `{2, 7, 61}`
      and identify composite (3215031751 = 151 × 751 × 28351)
- [ ] 3.8 `test_carmichael_561_caught` — 561 = 3·11·17 is the
      smallest Carmichael number
- [ ] 3.9 `test_large_prime_below_top_threshold` — value parsed
      from string just below `3317044064679887385961981`
- [ ] 3.10 `test_value_at_top_threshold_throws`
- [ ] 3.11 `test_value_above_top_threshold_throws_with_hint`
- [ ] 3.12 `test_deterministic_repeat` — calling `IsPrime()` twice
      on the same value returns the same answer (no RNG dependency)

## 阶段 4: Doc sync + verify + archive
- [ ] 4.1 `docs/design/stdlib/numerics.md`: mark
      `bigint-future-prime-deterministic` ✅, add a brief table +
      threshold + delegation note
- [ ] 4.2 `docs/design/stdlib/roadmap.md`: replace the
      `bigint-future-prime-deterministic` entry status if listed
- [ ] 4.3 `./scripts/test-stdlib.sh z42.numerics` — green
- [ ] 4.4 `./scripts/test-all.sh` — full GREEN before commit
- [ ] 4.5 Move dir to `docs/spec/archive/2026-06-02-add-bigint-prime-deterministic/`
- [ ] 4.6 Commit + push (per workflow auto-archive)

## 备注

- Tier thresholds parsed from string literals — none fit `long`
  above tier 11. Parsing cost is ~13 × tens-of-microseconds, dwarfed
  by even one ModPow. If perf-critical, can be lazy-init'd into
  static fields later; not in v0.
- `_trialDivideSmallPrimes` (existing in `NextPrime`) is extended
  from 10 primes (3..31) to 13 primes (2..41) via the new shared
  table. `NextPrime` is unaffected — same semantic, narrower
  false-negative window.
