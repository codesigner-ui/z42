# Design: Std.Crypto.Random — CSPRNG

## Architecture

```
z42 user code
  ↓
Std.Crypto.Random.GetBytes(n)  ← Random.z42 (pure script)
  ↓
__crypto_random_bytes(n)        ← Builtin (BUILTINS array entry)
  ↓
crypto::builtin_crypto_random_bytes  ← corelib/crypto.rs
  ↓
getrandom::getrandom(&mut buf)  ← getrandom crate
  ↓                              ┌─ Linux:   getrandom(2) syscall
  Platform CSPRNG syscall ───────┼─ macOS:   getentropy(2) / SecRandomCopyBytes
                                 ├─ Windows: BCryptGenRandom (CNG)
                                 ├─ iOS:     SecRandomCopyBytes
                                 ├─ Android: getrandom(2) (API 28+) / /dev/urandom
                                 └─ wasm32:  cfg-gated throw NotSupported
```

## Decisions

### Decision 1: 走 in-VM builtin，不走 native-ext-loader cdylib

**问题**：z42.compression 是首个走出 z42vm 的 stdlib native（cdylib + dlopen via `native::ext` loader）。CSPRNG 该跟还是不跟？

**选项**：
- A — in-VM builtin（同 z42.net K1 / process / threading 等大多数 native stdlib）
- B — out-of-VM cdylib（同 z42.compression）

**决定**：A，因为：
1. **footprint 小**：getrandom crate 编译产出极小（~10KB），不像 zstd 是几百 KB
2. **安全关键**：CSPRNG 是 baseline security 原语，应该确保 always available（不走 dlopen 可省一类失败路径）
3. **无外部 C 依赖**：getrandom 是 pure Rust + libc syscall 包装，不需 cdylib 隔离
4. **z42.compression "首个走出 VM" 是 footprint 优化**：CSPRNG 没有 footprint 问题，自然留在 VM 内

### Decision 2: 用 getrandom crate 而非手写 syscall

**问题**：要不要自己写 `cfg_if!` + `extern "C" { fn getrandom; }` + Windows BCryptGenRandom binding？

**决定**：用 `getrandom = "0.2"` crate。原因：
- crate 已封装好 10+ 平台（Linux / macOS / iOS / Windows / Android / FreeBSD / NetBSD / OpenBSD / Solaris / Fuchsia / WASI / ...）
- 是 `rand` ecosystem 标准依赖；Rust 社区 `rand::rngs::OsRng` 底下就是 getrandom
- 维护成本零；安全审计覆盖好

### Decision 3: byte[] 返回 shape

**问题**：z42 byte[] 在 VM 表示为 `Value::Array(Rc<RefCell<Vec<Value>>>)` 且 element 是 `Value::I64(0..=255)`，每个 byte 8 字节包装。要不要引入 `Value::ByteArray` 专门类型？

**决定**：不引入。沿用现有 byte[] 表示（同 z42.encoding / z42.crypto Sha256 等所有 stdlib）。理由：
- VM-wide 改 byte[] 表示属于 P1 性能 spec，超出 CSPRNG scope
- 当前 SHA-256 / Base64 已证明 i64-per-byte 表示足够 OK（GC 路径冷的小 array）
- 一致性 > 单 builtin 性能（避免破坏 byte[] = Array<I64> 不变式）

### Decision 4: wasm32 fallback

**问题**：getrandom 在 wasm32 需要额外配置（`js` feature 走 browser crypto.getRandomValues / 或 WASI 模式）。要不要现在就接？

**决定**：cfg-gated throw `NotSupportedException`（同 z42.net K1 wasm pattern）。理由：
- v0 scope 限制；wasm CSPRNG 需要 host bridge 决策（browser bridge vs WASI）
- 现有 wasm 用例（playground）不需要 secure random
- 异常 path 已存在；用户/集成者拿到 clear 错误而非 silent 弱随机

后续 `add-csprng-wasm-bridge` spec 决定走 `wasm-bindgen` js bridge 或 WASI getrandom。

### Decision 5: NextU32Bounded — rejection sampling vs modulo

**问题**：均匀分布 `[0, bound)` 实现：
- modulo: `bytes[u32] % bound` —— 简单，但当 bound 非 2 的幂时偏向小值（modulo bias）
- rejection sampling: 抽 u32，若 ≥ `floor(u32::MAX / bound) * bound` 则丢掉重抽 —— 真正均匀

**决定**：rejection sampling。理由：
- CSPRNG API 用户期望均匀；modulo bias 是经典反例（密码学课本案例）
- 期望 rejection 次数 ≤ 2（bound ≤ 2^31 时拒绝率 < 50%）
- 实现 ~5 行；不增可测复杂度

### Decision 6: NextInt 不 bias 符号位

**问题**：NextInt 返回 `int`（z42 i32 signed）。要不要刻意把高位置 0 让结果 ≥ 0？

**决定**：直接 reinterpret 4 字节为 i32，允许负值。理由：
- "random int32" 普遍语义就是全 [i32::MIN, i32::MAX] 范围
- 想要 ≥ 0 的用 `NextU32Bounded(int.MaxValue) + 1` 或 `& 0x7FFFFFFF`
- C# `RandomNumberGenerator.GetInt32()` 行为是 ≥ 0，但 z42 选择 sign-preserving 与 `Std.Random.NextInt` 一致

## Implementation Notes

### Random.z42 关键 method

```z42
namespace Std.Crypto;
using Std;

public static class Random {
    public static byte[] GetBytes(int n) {
        if (n < 0) {
            throw new ArgumentException("n must be non-negative, got " + n.ToString());
        }
        return __crypto_random_bytes(n);
    }

    public static int NextInt() {
        byte[] b = GetBytes(4);
        // Reinterpret 4 bytes (big-endian) as i32. Sign bit preserved.
        return ((int)b[0] << 24) | ((int)b[1] << 16) | ((int)b[2] << 8) | (int)b[3];
    }

    public static long NextLong() {
        byte[] b = GetBytes(8);
        long r = 0;
        int i = 0;
        while (i < 8) {
            r = (r << 8) | ((long)b[i] & 0xFFL);
            i = i + 1;
        }
        return r;
    }

    public static int NextU32Bounded(int bound) {
        if (bound <= 0) {
            throw new ArgumentException("bound must be positive, got " + bound.ToString());
        }
        // Rejection sampling: avoid modulo bias.
        // Reject any sample ≥ floor(2^32 / bound) * bound; remap to [0, bound).
        long u32max_plus_one = 4294967296L;  // 2^32
        long zone = (u32max_plus_one / (long)bound) * (long)bound;
        while (true) {
            byte[] b = GetBytes(4);
            long u = ((long)b[0] & 0xFFL) << 24
                   | ((long)b[1] & 0xFFL) << 16
                   | ((long)b[2] & 0xFFL) << 8
                   |  (long)b[3] & 0xFFL;
            if (u < zone) {
                return (int)(u % (long)bound);
            }
        }
    }
}
```

### crypto.rs

```rust
//! `__crypto_random_bytes` — OS-CSPRNG via `getrandom` crate.
//!
//! add-csprng-to-crypto (2026-05-26). Backs `Std.Crypto.Random.GetBytes(n)`.

use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::{bail, Result};
use super::convert::arg_i64;

const NAME: &str = "__crypto_random_bytes";

#[cfg(not(target_arch = "wasm32"))]
pub fn builtin_crypto_random_bytes(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let n = arg_i64(args, 0, NAME)?;
    if n < 0 {
        bail!("{}: n must be non-negative, got {}", NAME, n);
    }
    if n > i32::MAX as i64 {
        // Defensive: prevent absurdly large allocation. Real CSPRNG usage
        // is < 1KB per call.
        bail!("{}: n exceeds i32::MAX ({}), got {}", NAME, i32::MAX, n);
    }
    let mut buf = vec![0u8; n as usize];
    getrandom::getrandom(&mut buf)
        .map_err(|e| anyhow::anyhow!("{}: OS CSPRNG failed: {}", NAME, e))?;
    let elems: Vec<Value> = buf.into_iter().map(|b| Value::I64(b as i64)).collect();
    Ok(ctx.heap().alloc_array(elems))
}

#[cfg(target_arch = "wasm32")]
pub fn builtin_crypto_random_bytes(_ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    bail!("{}: CSPRNG not available on wasm32 — use add-csprng-wasm-bridge follow-up", NAME)
}

#[cfg(test)]
#[path = "crypto_tests.rs"]
mod crypto_tests;
```

### Testing Strategy

- **Rust 单测** (`crypto_tests.rs`):
  - `n=0` 返回空数组
  - `n=32` 返回长度 32
  - 负值 bail
  - 两次连续调用结果不同（极概率假阴；统计上不会 false-negative）
- **z42 [Test]** (`tests/random_basic.z42`):
  - 5 个 scenario 直接镜像 spec scenarios
  - `Std.Crypto.Random.GetBytes(0).Length == 0`
  - 抓 ArgumentException 验证 negative input
- **z42 distribution test** (`tests/random_distribution.z42`):
  - 1000 调用 `NextU32Bounded(8)`，每 bucket 计数 ∈ [80, 170]（χ² loose bound around expected 125）
  - 失败概率 < 10^-6（实际通过的随机源应几乎无 false-positive）

### Bailing on huge n

`n > i32::MAX (2^31-1)` 时 bail。理由：CSPRNG 典型用例每次 < 1KB（一个 nonce / key），大量请求是 misuse。也避免一次 alloc 2GB+ `Vec<u8>` + 2GB+ `Vec<Value>`（每 byte 包成 i64 是 8x 膨胀）。

## Testing Strategy

- 单元测试：Rust + z42 双侧（前述）
- Golden / e2e：暂不引入（CSPRNG 输出本质不 deterministic，golden 不适用）
- GREEN：`./scripts/test-all.sh` 全绿 = pass

## 与 Std.Random 区分

| 包 | 类 | 用途 | seedable | secure |
|----|----|------|---------|--------|
| `z42.random` | `Std.Random` | fixture / sim / 临时 ID | ✅ Random(seed) | ❌ PCG |
| `z42.crypto` | `Std.Crypto.Random` | nonce / token / KDF salt | ❌ | ✅ OS CSPRNG |

文档（crypto.md）显式 cross-link：用户从 `Std.Random` 跳来时看到 "for secure use, see `Std.Crypto.Random`"。
