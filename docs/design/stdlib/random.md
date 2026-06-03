# z42.random — Deterministic pseudo-random number generator

> 落地版本：2026-05-15（add-z42-random）
> 包路径：`src/libraries/z42.random/`
> 命名空间：`Std.Random`

## 职责

通用 deterministic PRNG，用于测试 fixture、生成临时 ID、shuffling、UI 抖动等场景。

**不是 CSPRNG**：不适合密码学 / 安全令牌 / session id。安全随机走 `z42.crypto`（独立 spec）。

## 算法选择：PCG-XSH-RR

参考 [pcg-random.org](https://www.pcg-random.org/)。State 64-bit，每次步进
`state' = state * MUL + INC`（LCG），输出 32-bit by
`rotr32(((state >> 18) ^ state) >> 27, state >> 59)`。

**为什么不是其他算法**：

| 候选 | 选 / 不选 | 理由 |
|------|----|------|
| Linear Congruential（C `rand`）| ❌ | 低位 0/1 周期严重，分布不均 |
| xorshift64 | ❌ | 没有 LCG 步进，PractRand 不通过；状态全 0 失败 |
| Mersenne Twister | ❌ | 状态 624 word（~2.5KB），对纯脚本 stdlib 太重 |
| **PCG-XSH-RR** | ✅ | 状态 64-bit，passes PractRand 32TB，码量小 |
| ChaCha20 | ❌ | CSPRNG，留 z42.crypto |

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. State | (a) 32-bit / (b) 64-bit | (b) | PCG-XSH-RR 标准；z42 i64 原生 |
| 2. Default seed | (a) 固定 / (b) wall-clock | (b) | C# parity；用户也可显式 seed |
| 3. Thread-safety | (a) safe / (b) unsafe | (b) | z42 单线程；z42.threading 时再做 |
| 4. Modulo bias 处理 | (a) ignore / (b) rejection sampling | (a) | 对 span ≪ 2^32 偏差 < 1e-7，v0 可接受 |
| 5. NextDouble 精度 | (a) 32-bit / (b) 53-bit | (b) | 完整 double mantissa |
| 6. Hex 字面量 workaround | inline 十进制 + named const | — | z42 lexer 暂不支持 hex `0xFFFFFFFF` 字面量（与 z42.toml 数字解析同款限制） |

## 不支持（Deferred）

### ~~random-future-csprng~~ — ✅ 已落地 2026-05-26 (`add-csprng-to-crypto`)

Shipped in `z42.crypto`, not `z42.random` — `Std.Crypto.SecureRandom.
{GetBytes(n) / NextInt(...) / NextLong(...) / NextDouble()}` via
`getrandom(2)` / `BCryptGenRandom` per platform. The clean
crypto/PRNG split matches BCL / Rust where `random` is for
distribution-style helpers and `crypto` owns the unpredictability
guarantee. `random-future-csprng-wasm32` remains deferred (wasm32
needs `crypto.getRandomValues` / `WASI random_get` bridge).

### ~~random-future-distributions~~ — ✅ 已落地 2026-06-03 (`extend-z42-random`)

`NextGaussian(mean, stddev)` via Box-Muller, `NextExponential(lambda)`
via inverse CDF. Both reject pathological inputs (`u == 0.0`
re-draw; negative/zero lambda throws). 10k-sample tests verify
mean & stddev within `±0.1` (Gaussian) / `±5%` (exponential).
Uniform is already covered by `NextDouble`.

### ~~random-future-stream-id~~ — ✅ 已落地 2026-06-03 (`extend-z42-random`)

`Random(long seed, long streamId)` overload. `streamId | 1L`
becomes the per-instance PCG increment. Two instances with the
same seed but different streamId produce strictly disjoint
sequences (PCG correctness guarantee). The previous `Random(seed)`
constructor now delegates to `(seed, defaultIncrement)` so
existing seeded code reproduces the same sequences byte-identical.

### random-future-seed-from-entropy
- **来源**：从 OS entropy 源（`getrandom(2)` / `BCryptGenRandom`）取强 seed
- **触发原因**：wall-clock seed 可预测（PoC 攻击）；安全场景需要
- **architecture block**：`Std.Crypto.SecureRandom.GetBytes` (z42.crypto)
  would be the natural source, but
  **`z42.crypto → z42.numerics → z42.random` already exists** —
  adding `z42.random → z42.crypto` would form a cycle. Resolution
  paths: (a) move `SecureRandom` into a new lower-layer
  `z42.entropy` zpkg that both `z42.crypto` and `z42.random` can
  consume, or (b) expose a `__entropy_bytes` corelib builtin
  directly from `z42.random` (mirrors the SecureRandom backend
  without going through `z42.crypto`). Pick one when the use case
  hardens; attempted 2026-06-03 by `extend-z42-random` and
  reverted.

### ~~random-future-thread-safe~~
- **来源**：多线程并发使用同一 Random 实例
- **状态**：still deferred — would require z42.threading dep
  (`Mutex`) or atomic CAS state update. Workaround: per-thread
  Random instance with different streamId (now that streamId
  exists, this is the clean pattern).

## 跨 stdlib 交互

- 依赖 `z42.core`（基础类型、`ArgumentException`）
- 依赖 `z42.time`（`DateTime.UtcNow().UnixMs()` 提供 wall-clock seed）
- 被 z42.test 可能使用作 fixture 生成（follow-up）

## 实施期发现（已记录到归档 tasks.md）

1. **z42 lexer 暂不支持 hex 字面量**（`0xFFFFFFFF` 不解析），全部 mask 用十进制 named const（`long MASK32 = 4294967295`）。同 z42.toml 数字 parser 走十进制路径的限制。考虑独立 spec 加 hex/oct/bin 字面量支持。
2. **z42 primitive `int[]` 不零初始化**：`new int[N]` 后元素是 Null，`counts[i] + 1` → "type mismatch: Null vs I64"。测试代码 must 显式 init loop。其他类 (TomlValue 等) 只写不读已掩盖此问题，本 spec 第一次撞到。Backlog item 候选：要么 zero-init by language，要么 doc 显式说"primitive arrays not zero-initialized, init before read"。
3. **`u32` 是 z42 reserved keyword**：本地变量命名 `u32` 报 "unexpected token"。改名 `raw32`。z42 数值别名（`u8 / u16 / u32 / u64 / i8 / ... / isize / usize`）均保留。
4. **`(int) long` cast 不截断**：z42 int 内部存为 I64，cast 到 `int` 是 type-tag-only 操作，不丢高位。要 32-bit 截断必须显式 `& MASK32`。同 add-std-process `convert_value` 识别 reference-type identity cast 那条路径的另一面。
