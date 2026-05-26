# Design: Std.Crypto.SecureRandom — CSPRNG

## Architecture

```
z42 user code
  ↓
Std.Crypto.SecureRandom.GetBytes(n)  ← SecureRandom.z42 (pure script)
  ↓
[Native("__crypto_random_bytes")] _CryptoRandomBytes(n)  ← extern bridge
  ↓
crypto::builtin_crypto_random_bytes  ← corelib/crypto.rs
  ↓
getrandom::getrandom(&mut buf)  ← getrandom crate
  ↓                              ┌─ Linux:   getrandom(2) syscall
  Platform CSPRNG syscall ───────┼─ macOS:   getentropy(2) / SecRandomCopyBytes
                                 ├─ Windows: BCryptGenRandom (CNG)
                                 ├─ iOS:     SecRandomCopyBytes
                                 ├─ Android: getrandom(2) (API 28+) / /dev/urandom
                                 └─ wasm32:  cfg-gated `bail!` (NotSupported)
```

## Decisions

### Decision 1: 命名 `SecureRandom`（避开 `Std.Random.Random` 短名冲突）

**问题**：原拟名 `Std.Crypto.Random` 与已有 `Std.Random.Random` 类共用短名 `Random`。跨 zpkg dep-index 用短名作 first-wins key（详 `.claude/rules/common-pitfalls.md §1`），加载后 `Random.<Method>` 静态查找命中字母序较早的那个，破坏现有 WebSocket / 其他 PCG 调用。

**决定**：用 `SecureRandom`。理由：
- 完全避开短名冲突
- 对标 .NET `RandomNumberGenerator` 语义（"安全随机源" vs "通用 PRNG"）
- API 调用方一眼能判断"我要的是安全随机还是普通随机"

### Decision 2: 走 in-VM builtin，不走 native-ext-loader cdylib

**问题**：z42.compression 是首个走出 z42vm 的 stdlib native（cdylib + dlopen via `native::ext` loader）。CSPRNG 该跟还是不跟？

**决定**：in-VM builtin。理由：
1. **footprint 小**：getrandom crate 编译产出极小（~10KB），不像 zstd 几百 KB
2. **安全关键**：CSPRNG 是 baseline security 原语，要 always available（不走 dlopen 可省一类失败路径）
3. **无外部 C 依赖**：getrandom 是 pure Rust + libc syscall 包装

### Decision 3: 用 getrandom crate 而非手写 syscall

**决定**：用 `getrandom = "0.2"` crate。原因：
- crate 已封装 10+ 平台（Linux / macOS / iOS / Windows / Android / FreeBSD / NetBSD / OpenBSD / Solaris / Fuchsia / WASI / ...）
- `rand` ecosystem 标准依赖；社区 `rand::rngs::OsRng` 底下就是它
- 维护成本零；安全审计覆盖好

### Decision 4: NextU32Bounded — rejection sampling 避免 modulo bias

**问题**：均匀 `[0, bound)`：
- modulo: `bytes[u32] % bound` —— 当 bound 非 2 的幂时偏向小值
- rejection sampling: 拒绝 `>= floor(2^32 / bound) * bound` 的样本 → 真正均匀

**决定**：rejection sampling。期望拒绝次数 ≤ 2（bound ≤ 2^31 时拒绝率 < 50%）。

### Decision 5: NextInt 在 long 域算位运算

**问题**：直接 `((int)b[0] << 24)` 在 z42 i32 上 sign-bit 行为不可靠（实测 100 次 NextInt 全部非负 — z42 shift left 不像 C/Java 那样让 sign bit set 后保留负值）。

**决定**：先把 4 个字节 build 到 long（i64），再 `(int)long` 截断到 i32。`(int)long` 在 z42 是 sign-aware 截断（保留低 32 位的 bit pattern），所以 sign bit 正确恢复。

NextLong 不存在这个问题（自始至终在 long 域）。

### Decision 6: byte[] 返回 shape 不改

**决定**：沿用现有 byte[] = `Value::Array<I64(0..=255)>`（同 z42.encoding / Sha256 等）。VM-wide 改 byte[] 表示属于 P1 性能 spec，超出 scope。

### Decision 7: wasm32 fallback

**决定**：cfg-gated bail (`Result::Err`)。理由：
- v0 scope 限制；wasm CSPRNG 需要 host bridge 决策（browser bridge vs WASI）
- 现有 wasm 用例（playground）不需要 secure random
- 用户/集成者拿到 clear 错误而非 silent 弱随机

后续 `add-csprng-wasm-bridge` 决定走 `wasm-bindgen` js bridge 或 WASI getrandom。

### Decision 8: while(true) 后必须有 unreachable throw

**问题**：z42 typechecker 不识别 `while(true) { ... return X; }` 为 always-terminating。

**决定**：用 `throw new Exception("unreachable")` 兜底（同 stdlib 既有模式如 TomlParser）。
比 `return 0` 更明确语义。

## Implementation Notes

### SecureRandom.z42 关键 method

```z42
public static class SecureRandom {
    [Native("__crypto_random_bytes")]
    private static extern byte[] _CryptoRandomBytes(int n);

    public static byte[] GetBytes(int n) {
        if (n < 0) throw new ArgumentException("n must be non-negative, got " + n.ToString());
        return _CryptoRandomBytes(n);
    }

    public static int NextInt() {
        byte[] b = GetBytes(4);
        long r = (((long)b[0] & 0xFFL) << 24)
              | (((long)b[1] & 0xFFL) << 16)
              | (((long)b[2] & 0xFFL) << 8)
              |  ((long)b[3] & 0xFFL);
        return (int)r;
    }
    // ... NextLong, NextU32Bounded see source
}
```

### crypto.rs 结构

```rust
const NAME: &str = "__crypto_random_bytes";

#[cfg(not(target_arch = "wasm32"))]
pub fn builtin_crypto_random_bytes(ctx: &VmContext, args: &[Value]) -> Result<Value> {
    let n = arg_i64(args, 0, NAME)?;
    if n < 0 { bail!("..."); }
    if n > i32::MAX as i64 { bail!("..."); }
    let mut buf = vec![0u8; n as usize];
    getrandom::getrandom(&mut buf).map_err(...)?;
    let elems: Vec<Value> = buf.into_iter().map(|b| Value::I64(b as i64)).collect();
    Ok(ctx.heap().alloc_array(elems))
}

#[cfg(target_arch = "wasm32")]
pub fn builtin_crypto_random_bytes(_ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    bail!("{}: CSPRNG not available on wasm32 — use add-csprng-wasm-bridge follow-up", NAME)
}
```

### Defensive n > i32::MAX bail

CSPRNG 典型用例每次 < 1 KB。`i64::MAX` 以下的"大量请求"是 misuse。i64-per-byte 表示有 8× 膨胀，2 GB 请求会 alloc 16 GB+。bail 防御此类 path。

## Testing Strategy

- **Rust 单测** (`crypto_tests.rs`, 6 cases): n=0 / n=32 / negative bail / huge bail / 两次不同 / 1024 byte 不被 0 dominate
- **z42 [Test] basic** (`secure_random_basic.z42`, 11 cases): GetBytes 5 scenario + NextInt + NextLong + NextU32Bounded 4 scenario
- **z42 [Test] distribution** (`secure_random_distribution.z42`, 2 cases): bucket χ² + byte 0 dominance
- **Golden / e2e**: 暂不引入（CSPRNG 输出本质不 deterministic）
- **GREEN**: `./scripts/test-all.sh --scope=full` 全绿

## 与 Std.Random 区分

| 包 | 类 | 用途 | seedable | secure |
|----|----|------|---------|--------|
| `z42.random` | `Std.Random.Random` | fixture / sim / 临时 ID | ✅ Random(seed) | ❌ PCG |
| `z42.crypto` | `Std.Crypto.SecureRandom` | nonce / token / KDF salt | ❌ | ✅ OS CSPRNG |

crypto.md cross-link：用户从 `Std.Random` 跳来时看到 "for secure use, see `Std.Crypto.SecureRandom`"。
