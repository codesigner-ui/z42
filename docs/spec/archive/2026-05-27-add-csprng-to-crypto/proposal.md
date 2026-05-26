# Proposal: Std.Crypto.SecureRandom — CSPRNG

## Why

z42.crypto v0 deferred `Std.Crypto.Random` 因为"需 OS syscall 抽象层"——而 z42.io 的 syscall 抽象层（File / Directory / Path / Environment / Process）已于 2026-05-12 至 2026-05-14 全部落地。延后条件已消失。

当前应用层只有 `Std.Random`（PCG，自己 doc 写"**不是 CSPRNG**"，仅 deterministic fixture / 临时 ID 用）。任何安全场景（session token / CSRF nonce / KDF salt / API key / OAuth state / WebSocket masking key 的"真"随机源 / 随机生成的密码）当前**没有**正确 API。已落地的 WebSocket K4 masking key 仍用 PCG 顶着，是已知弱点。

## What Changes

- 新增 `Std.Crypto.SecureRandom` 静态类（命名避开与 `Std.Random.Random` 短名冲突；对标 .NET `RandomNumberGenerator`）：
  - `GetBytes(int n) -> byte[]` — 主入口；填入 OS-level CSPRNG 字节
  - `NextInt() -> int` — 便捷 wrapper：4 字节 → int32（符号位保留）
  - `NextLong() -> long` — 便捷 wrapper：8 字节 → int64
  - `NextU32Bounded(int bound) -> int` — `[0, bound)` 均匀分布（rejection sampling 避免 modulo bias）
- 新增 1 个 VM builtin `__crypto_random_bytes(n: i64) -> byte[]`，append 到 BUILTINS array 末尾
- Rust 端 `corelib/crypto.rs` 新文件 + `getrandom = "0.2"` crate 依赖
- wasm32 throw `bail!`（VM 抛 RuntimeError → z42 看到 `Exception`；wasm bridge 是 follow-up）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.crypto/src/SecureRandom.z42` | NEW | `Std.Crypto.SecureRandom` 类（4 个 static method + extern `[Native]` 声明） |
| `src/libraries/z42.crypto/tests/secure_random_basic.z42` | NEW | 11 个 [Test]：长度对 / n=0 / negative throws / 大 buffer / 连续不同 / NextInt 正负覆盖 / NextLong 正负覆盖 / NextU32Bounded 各 case |
| `src/libraries/z42.crypto/tests/secure_random_distribution.z42` | NEW | 2 个 [Test]：NextU32Bounded(8) bucket 均匀 / 1024 byte 不被 0 dominate |
| `src/runtime/src/corelib/crypto.rs` | NEW | `builtin_crypto_random_bytes` 实现 + wasm32 cfg gate |
| `src/runtime/src/corelib/crypto_tests.rs` | NEW | Rust 单测（6 cases） |
| `src/runtime/src/corelib/mod.rs` | MODIFY | `pub mod crypto;` + BUILTINS 末尾 append `("__crypto_random_bytes", crypto::builtin_crypto_random_bytes)` |
| `src/runtime/Cargo.toml` | MODIFY | deps += `getrandom = "0.2"` |
| `docs/design/stdlib/crypto.md` | MODIFY | v0 段加 SecureRandom；Deferred 段加 wasm32 bridge entry |
| `docs/roadmap.md` | MODIFY | Deferred Backlog Index 里 CSPRNG 行 ✅ + 引用本 spec |
| `docs/design/stdlib/roadmap.md` | MODIFY | z42.crypto 行加 ✅ CSPRNG |
| `src/libraries/README.md` | MODIFY | shipping 表加 z42.crypto 行 |

**只读引用**：
- `src/libraries/z42.crypto/src/Sha256.z42` — 命名风格参考
- `src/libraries/z42.random/src/Random.z42` — 命名空间对照（避冲突）
- `src/runtime/src/corelib/network.rs` — wasm32 fallback pattern
- `src/runtime/src/corelib/bench.rs` — 单 builtin file 结构模板

## Out of Scope

- HKDF / PBKDF2 / Argon2 / scrypt（KDF 全栈）
- AES / ChaCha20 / 对称加密
- Ed25519 / X25519 / RSA / ECDH（公钥加密）
- wasm32 真 CSPRNG（getrandom `js` feature / WASI）—— `add-csprng-wasm-bridge` follow-up

## Open Questions

无（User 已批量授权 a→b→c 序列）。
