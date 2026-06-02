# Proposal: add `BigInt.IsBpswPrime()` Baillie–PSW primality test

## Why

`IsPrime()` (shipped same day, `add-bigint-prime-deterministic`)
is **provably** deterministic up to `n < 3.317×10^24` via OEIS
A014233 witness sets. Above that threshold it throws — callers must
fall back to probabilistic `IsProbablyPrime(rounds)`, which has a
small but non-zero error and depends on RNG.

**Baillie–PSW (BPSW)** is the canonical answer for "primality with
no provable bound but no known counterexample either":

- The original 1980 paper proved it correct up to `10^9`. Pomerance,
  Selfridge & Wagstaff offered a US$30 cash prize for a counter-
  example — still unclaimed 45 years later.
- Empirical verification through 2017 (Feitsma & Galway) covered
  every n ≤ 2^64 with **zero** false positives.
- Crandall & Pomerance prove no false positive can have all of
  {2, 3, 5, 7, 13} as small prime factors — so structural
  counterexamples (if any) must be enormous and weird.
- Adopted as the default in Python's `sympy.isprime`, Java's
  `BigInteger.isProbablePrime` for large n, Wolfram Mathematica's
  `PrimeQ`, PARI/GP's `ispseudoprime`.

This gives z42.numerics a third, complementary primality method:

| Method | Bound | Error |
|--------|-------|-------|
| `IsPrime()` | n < 3.317×10^24 | 0 (proven) |
| `IsBpswPrime()` | any n | 0 known counterexample (since 1980) |
| `IsProbablyPrime(rounds)` | any n | ≤ 4^-rounds (random witness) |

Listed in `docs/design/stdlib/numerics.md` Deferred as
`bigint-future-bpsw`. No blocker — pure script. Touches the
existing `BigInt.z42` (no new files → safe from the recently-
discovered TypeChecker E0402 bug on new files referencing sibling
cross-file types).

## What Changes

1. **New public method `BigInt.IsBpswPrime()`** — true iff the
   value passes BPSW. Returns false for `n <= 1` and for negatives.
   For `n` in {2, 3} returns true immediately; for even `n > 2`
   returns false.
2. **Strong Lucas pseudoprime test** with parameters chosen by
   **Selfridge's method**:
   - Find the first D in the sequence `5, -7, 9, -11, 13, -15, ...`
     with `jacobi(D, n) = -1`.
   - Set `P = 1`, `Q = (1 - D) / 4`.
   - Decompose `n + 1 = 2^s × d` with `d` odd.
   - Compute the Lucas sequences `U_d`, `V_d` mod n via binary
     expansion of `d` using the doubling formulas:
       - `U_{2k} = U_k × V_k`
       - `V_{2k} = V_k² - 2 × Q^k`
       - `U_{2k+1} = (P × U_{2k} + V_{2k}) / 2`
       - `V_{2k+1} = (D × U_{2k} + P × V_{2k}) / 2`
     (Division by 2 is done mod n via `(x × inv2) mod n`.)
   - n is a probable strong Lucas prime if `U_d ≡ 0 (mod n)` or
     `V_{d × 2^r} ≡ 0 (mod n)` for some `0 <= r < s`.
3. **Jacobi symbol `jacobi(a, n) → int`** — quadratic reciprocity
   algorithm returning -1, 0, or 1. Private helper used only by
   the Selfridge parameter search.
4. **Composition**: `IsBpswPrime()` returns
   `_millerRabinFixedWitness(BigInt.Two, d_mr, r_mr, n-1)` (the
   existing strong MR base 2 helper — Step 1 of BPSW) **AND** the
   new strong Lucas test (Step 2 of BPSW). Pre-trial divides by
   the same 13 small primes used by `IsPrime` as a fast path.
5. **Tests** under `bigint_bpsw.z42`.

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.numerics/src/BigInt.z42` | MODIFY | new `IsBpswPrime()` public + private `_jacobiSymbol(a, n)` + `_strongLucasPRP(D, P, Q, n)` helpers; reuses existing `_trialDivideUpTo41`, `_millerRabinFixedWitness`, `ModPow`, `ModInverse` |
| `src/libraries/z42.numerics/tests/bigint_bpsw.z42` | NEW | small primes / small composites / Mersenne primes inside + outside the IsPrime cutoff / strong Lucas pseudoprime 5459 / Carmichael 561, 1729 / large composite ~10^25 (above IsPrime ceiling, so IsBpswPrime is the only deterministic answer) |
| `docs/design/stdlib/numerics.md` | MODIFY | mark `bigint-future-bpsw` ✅; document the three-method primality lineup |
| `docs/design/stdlib/roadmap.md` | MODIFY | refine corresponding row |
| `docs/spec/changes/add-bigint-bpsw/` | NEW | this spec dir |

**只读引用**：

- `src/libraries/z42.numerics/src/BigInt.z42` — existing
  `_trialDivideUpTo41` / `_millerRabinFixedWitness` / `ModPow` /
  `ModInverse` / `ShiftRight` / `TestBit` helpers
- `docs/spec/archive/2026-06-02-add-bigint-prime-deterministic/` —
  pattern for primality spec wording

## Out of Scope

- **Replacing `IsProbablyPrime` with BPSW** — three methods stay
  in parallel; users pick by tradeoff.
- **`NextPrime()` using BPSW** — currently uses 20-round Miller-
  Rabin which is sufficient. Optional follow-up.
- **APR-CL / ECPP / AKS** — provable primality tests for n >
  3.317×10^24. Substantial implementation effort, no current demand.

## Open Questions

- [ ] **Jacobi symbol on negative `a`**: standard convention
  `jacobi(-a, n) = jacobi(-1, n) × jacobi(a, n)` where
  `jacobi(-1, n) = (-1)^((n-1)/2)`. Handle in `_jacobiSymbol`.
- [ ] **`n = 1` Jacobi**: defined as 1 by convention. Edge case
  documented; the Lucas test path won't reach n=1 anyway (caller
  short-circuits).
