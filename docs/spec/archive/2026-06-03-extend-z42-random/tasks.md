# Tasks: extend z42.random — entropy seed, streams, distributions

> 状态：🟡 进行中 | 创建：2026-06-03 | 类型：stdlib feat

**变更说明：** Three small, related additions to `Std.Random.Random`,
closing `random-future-seed-from-entropy`, `random-future-stream-id`,
and `random-future-distributions`:

1. **`Random.FromEntropy()`** — static factory that seeds from
   `Std.Crypto.SecureRandom.GetBytes(16)`. Adds `z42.crypto` as a
   `z42.random` dependency.
2. **`Random(long seed, long streamId)` overload** — exposes PCG's
   per-instance increment to produce independent streams. The
   `streamId | 1L` makes the increment odd as PCG requires.
3. **`NextGaussian(double mean, double stddev)`** + **`NextExponential
   (double lambda)`** — Box-Muller for normal, inverse CDF for
   exponential. The two most-asked-for distributions; uniform is
   already covered by `NextDouble`.

**原因：** Today scripts either seed with wall-clock ms (non-secure,
predictable) OR don't have access to independent streams (parallel
Monte Carlo trials collide). Distribution helpers are the "Python
random.* parity" missing piece.

## Tasks
- [ ] 1.1 Add `z42.crypto = "0.1.0"` to
      `src/libraries/z42.random/z42.random.z42.toml`
- [ ] 1.2 `using Std.Crypto;` in `Random.z42`
- [ ] 1.3 `public static Random FromEntropy()` — pulls 16 bytes from
      `SecureRandom.GetBytes(16)`, mixes into a `long` seed via
      simple byte-pack + xor with a second 8-byte chunk so all 128
      bits of entropy contribute
- [ ] 1.4 Add private `Init(long seed, long inc)` overload; refactor
      the existing `Init(long seed)` to call it with the standard
      PCG default increment `1442695040888963407L | 1L`. Remove the
      hard-coded `inc` literal from `Step()` and read it from a new
      `_inc` instance field so different stream ids advance with
      different increments.
- [ ] 1.5 `public Random(long seed, long streamId)` — `streamId | 1L`
      becomes the increment; documented invariant: two `Random`
      instances with the same `seed` but different `streamId` produce
      strictly independent sequences (PCG correctness guarantee)
- [ ] 1.6 `public double NextGaussian(double mean, double stddev)` —
      Box-Muller: `mean + stddev × sqrt(-2 ln u) × cos(2π v)` where
      `u, v ∈ (0, 1]` from two `NextDouble` calls
- [ ] 1.7 `public double NextExponential(double lambda)` —
      `-ln(NextDouble()) / lambda` (inverse CDF); throws
      `ArgumentException` for `lambda <= 0`
- [ ] 1.8 Tests in new `tests/random_extensions.z42`:
      - FromEntropy: two instances differ (probabilistic check;
        compare first 4 values)
      - StreamId: same seed + different stream → first 10 values
        differ; same seed + same stream → identical sequence
        (reproducibility)
      - NextGaussian: 10k-sample mean within `±0.1` of declared
        mean; std-dev within `±0.1` of declared stddev
      - NextExponential: mean of 10k samples within `±5%` of
        `1/lambda`; rejects `lambda <= 0`
- [ ] 1.9 Doc sync: `docs/design/stdlib/random.md` — flip three
      Deferred entries; `docs/design/stdlib/roadmap.md` matching row
- [ ] 1.10 `./scripts/test-all.sh` — full GREEN
- [ ] 1.11 Archive + commit + push
