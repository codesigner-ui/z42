# Tasks: add BigInt.IsBpswPrime()

> 状态：🟢 已完成 | 创建：2026-06-03 | 归档：2026-06-03 | 类型：stdlib feat

## 进度概览
- [x] 阶段 1: `_jacobiSymbol(a, n)` helper
- [x] 阶段 2: `_strongLucasPRP(D, P, Q)` — strong Lucas pseudoprime test
- [x] 阶段 3: Public `IsBpswPrime()` — MR base 2 + Lucas
- [x] 阶段 4: Tests (10/10 green)
- [x] 阶段 5: Doc sync + verify + archive

## 阶段 1: Jacobi symbol
- [ ] 1.1 Private static `_jacobiSymbol(BigInt a, BigInt n) → int`
- [ ] 1.2 Iterative quadratic-reciprocity algorithm: reduce `a` mod `n`,
      strip factors of 2 (tracking sign via mod-8 of `n`), swap with
      sign flip via mod-4 of `(a, n)`, terminate when `a == 0` (→0) or
      `a == 1` (→sign)
- [ ] 1.3 Handle negative `a` via `jacobi(-1, n) = (-1)^((n-1)/2)`

## 阶段 2: Strong Lucas pseudoprime test
- [ ] 2.1 Private `_strongLucasPRP(int D, int P, int Q, BigInt n) → bool`
- [ ] 2.2 Decompose `n + 1 = 2^s * d` with `d` odd
- [ ] 2.3 Compute `inv2 = ModInverse(2, n)` once for /2 reductions
- [ ] 2.4 Binary-expand `d` MSB-first; double `(U, V)` each step; if
      bit set, advance via `U_{2k+1}`, `V_{2k+1}` formulas
- [ ] 2.5 Return true iff `U_d ≡ 0 (mod n)` OR
      `V_{d * 2^r} ≡ 0 (mod n)` for some `0 <= r < s`
- [ ] 2.6 Track `Q^k` (running power) to feed `V_{2k}` formula

## 阶段 3: Public IsBpswPrime
- [ ] 3.1 Add public `bool IsBpswPrime()` method
- [ ] 3.2 Special-cases (n ≤ 1 → false; n = 2, 3 → true; even → false)
- [ ] 3.3 Pre-trial divide by primes 2..41 via existing
      `_trialDivideUpTo41`
- [ ] 3.4 Strong Miller-Rabin base 2: decompose `n - 1 = 2^r * d_mr`
      and call existing `_millerRabinFixedWitness(BigInt.Two, ...)`
- [ ] 3.5 Selfridge parameter search: D in sequence
      `5, -7, 9, -11, 13, ...`; pick first with `jacobi(D, n) = -1`.
      If a perfect square is found in the search (jacobi = 0 and
      D ≠ ±n's divisor), return false; otherwise compute
      `Q = (1 - D) / 4` and call `_strongLucasPRP`
- [ ] 3.6 Both tests pass → return true; either fails → false

## 阶段 4: Tests
- [ ] 4.1 Create `src/libraries/z42.numerics/tests/bigint_bpsw.z42`
- [ ] 4.2 `test_zero_one_negative_are_not_prime`
- [ ] 4.3 `test_small_primes_through_41_all_true`
- [ ] 4.4 `test_more_small_primes_true` — 43..101
- [ ] 4.5 `test_small_composites_false` — 4, 6, 8, 9, 15, 21, 25, ...
- [ ] 4.6 `test_carmichael_caught` — 561, 1105, 1729, 2465, 2821, 6601
- [ ] 4.7 `test_strong_pseudoprime_base_2_caught` — 2047 (MR-2 fools
      it, Lucas catches), 3215031751 (MR-bases-{2,3,5,7} fools)
- [ ] 4.8 `test_lucas_pseudoprime_caught` — 5459 = 53 × 103 is a
      Lucas PRP with D=-7; MR base 2 catches it. Verifies the
      BPSW combination is stronger than either piece alone.
- [ ] 4.9 `test_mersenne_primes_in_range` — 2^13-1, 2^31-1, 2^61-1
- [ ] 4.10 `test_large_prime_above_isprime_ceiling` — 2^89 - 1
      (Mersenne prime 6.18×10^26, above IsPrime's 3.317×10^24
      ceiling) returns true; IsPrime would throw on this input
- [ ] 4.11 `test_large_composite_above_isprime_ceiling` —
      `(2^89-1) * 3` (composite > IsPrime ceiling) returns false
- [ ] 4.12 `test_perfect_square_caught` — 49, 169, 14641 (11^4)
      detected without infinite-looping the Selfridge search

## 阶段 5: Doc sync + verify + archive
- [ ] 5.1 `docs/design/stdlib/numerics.md`: mark `bigint-future-bpsw` ✅;
      add a brief comparison table of the three primality methods
- [ ] 5.2 `docs/design/stdlib/roadmap.md`: update Deferred row
- [ ] 5.3 `./scripts/test-stdlib.sh z42.numerics` — green
- [ ] 5.4 `./scripts/test-all.sh` — full GREEN
- [ ] 5.5 Move dir to `docs/spec/archive/2026-06-03-add-bigint-bpsw/`
- [ ] 5.6 Commit + push

## 备注

- Touches only existing `BigInt.z42` + new test file. No new source
  file in z42.numerics, so safe from the TypeChecker E0402 bug
  blocking `add-datetime-offset`.
