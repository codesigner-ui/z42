# Proposal: add `BigInt.IsPrime()` deterministic primality test

## Why

`BigInt.IsProbablyPrime(rounds)` exists since 2026-05-25 but it's
probabilistic — error ≤ 4^-rounds. For values that fit a known
bound, deterministic Miller-Rabin witness sets give the same answer
with **zero** error and **smaller round count**. OEIS A014233
catalogs the known sets:

| Upper bound n < | Witnesses sufficient |
|-----------------|----------------------|
| 2,047 | 2 |
| 1,373,653 | 2, 3 |
| 9,080,191 | 31, 73 |
| 25,326,001 | 2, 3, 5 |
| 3,215,031,751 | 2, 3, 5, 7 |
| 4,759,123,141 | 2, 7, 61 |
| 1,122,004,669,633 | 2, 13, 23, 1662803 |
| 2,152,302,898,747 | 2, 3, 5, 7, 11 |
| 3,474,749,660,383 | 2, 3, 5, 7, 11, 13 |
| 341,550,071,728,321 | 2, 3, 5, 7, 11, 13, 17 |
| 3,825,123,056,546,413,051 | 2, 3, 5, 7, 11, 13, 17, 19, 23 |
| 318,665,857,834,031,151,167,461 | 2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37 |
| 3,317,044,064,679,887,385,961,981 | 2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37, 41 |

The top bound covers any value that fits in 82 bits (so all `long`
inputs, all 64-bit cryptographic primes used in toy keys, all
fully-determined keys for small-modulus RSA test fixtures, etc.).
This is the standard deterministic-primality cut-off used by
Python's `sympy.isprime`, Go's `math/big.ProbablyPrime` (when
combined with their Baillie-PSW), Java's `BigInteger.isProbablePrime`
in deterministic regime, and the CPython 3.12+ `isqrt`-based prime
checker.

Listed in `docs/design/stdlib/numerics.md` Deferred as
`bigint-future-prime-deterministic`. No blocker — pure script on
top of existing `ModPow` + `Mod` + `CompareTo`.

## What Changes

1. **New public method `BigInt.IsPrime()`** — returns `true` if
   the value is prime, `false` if composite or non-positive,
   **throws `ArgumentException`** if the value is ≥ the largest
   known deterministic bound (3,317,044,064,679,887,385,961,981).
   Callers with larger inputs must use `IsProbablyPrime(rounds)`.
2. **Tiered witness selection**: smallest valid set per OEIS table.
   For n < 2,047 one ModPow round suffices; for the top tier 13 are
   needed. This is the documented academic minimum — Python /
   Java / Go pick the same sets.
3. **Pre-trial division by 2, 3, 5, 7, 11, 13, 17, 19, 23, 29,
   31, 37, 41** before Miller-Rabin. Handles n equal to any of
   those witnesses (without this, `witness == n` paths would
   misfire), short-circuits common composites, and lets the MR loop
   assume `n > 41` and odd.
4. **No `Random` dependency** — `IsPrime()` is fully deterministic;
   given the same input it always picks the same witnesses and
   produces the same answer. The existing `IsProbablyPrime(rounds)`
   /  `IsProbablyPrime(rounds, Random)` overloads are unchanged.

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.numerics/src/BigInt.z42` | MODIFY | new `IsPrime()` + private `_pickDeterministicWitnesses()` + `_runMillerRabinFixed(witnesses)` helpers; pre-trial-division of 13 small primes shared with the existing `_trialDivideSmallPrimes` helper (extend the small-prime table to include 37 / 41) |
| `src/libraries/z42.numerics/tests/bigint_prime_deterministic.z42` | NEW | small primes / small composites / Mersenne primes / pseudoprime boundary (2047) / known witness-set boundary 318665857834031151167461 / value at threshold / value above threshold throws |
| `docs/design/stdlib/numerics.md` | MODIFY | mark `bigint-future-prime-deterministic` as ✅; add a "Deterministic primality" section describing the tier table + cutoff + delegation back to `IsProbablyPrime` for larger inputs |
| `docs/spec/changes/add-bigint-prime-deterministic/` | NEW | this spec dir |

**只读引用**：

- `src/libraries/z42.numerics/tests/bigint_prime.z42` — pattern
  for prime test inputs (small primes, NextPrime expectations)
- `docs/spec/archive/2026-05-25-add-bigint-prime/` — original
  `IsProbablyPrime` spec
- OEIS A014233 (cited in proposal) — witness set source of truth

## Out of Scope

- **Baillie-PSW** — separate spec `bigint-future-bpsw` (next on
  the queue). It's a stronger primality test with no known
  counterexample but the deterministic guarantee is empirical
  ("no counterexample found up to 10^20 / current") rather than
  formal like the tiered Miller-Rabin sets.
- **Probabilistic API changes** — `IsProbablyPrime(rounds)` stays
  exactly as-is for inputs above the deterministic cutoff.
- **NextPrime tuning** — `NextPrime()` currently runs 20-round
  probabilistic MR. A future spec could swap it for `IsPrime()` when
  the candidate fits the deterministic cutoff (typical case for any
  realistic `BigInt` < 10^24); deferred to keep this spec narrow.

## Open Questions

- [ ] **Threshold-too-large behaviour**: throwing vs silently
  falling back to a high-round probabilistic test. Going with
  **throw `ArgumentException`** — the method contract is
  "deterministic"; silently downgrading would surprise callers who
  picked this method specifically to avoid probabilistic answers.
- [ ] **Witness selection alternative**: a single fixed
  13-witness set works for any n in the supported range. Picked
  **tiered** instead because it's the academic minimum and gives a
  ~13x speed-up for small inputs (one MR round vs 13). Both have
  identical correctness.
