# Proposal: Std.Crypto.Random — CSPRNG

## Why

z42.crypto v0 deferred `Std.Crypto.Random` 因为"需 OS syscall 抽象层"——而 z42.io 的 syscall 抽象层（File / Directory / Path / Environment / Process）已于 2026-05-12 至 2026-05-14 全部落地。延后条件已消失。

当前应用层只有 `Std.Random`（PCG，自己 doc 写"**不是 CSPRNG**"，仅 deterministic fixture / 临时 ID 用）。任何安全场景（session token / CSRF nonce / KDF salt / API key / OAuth state / WebSocket masking key 的"真"随机源 / 随机生成的密码）当前**没有**正确 API。已落地的 `Sec-WebSocket-Accept` 验证（add-z42-net-websocket K4）的 masking key 还是用 PCG 顶着，是已知 known-weakness。

## What Changes

- 新增 `Std.Crypto.Random` 静态类（z42.crypto 包），4 个 API：
  - `GetBytes(int n) -> byte[]` — 主入口；填入 OS-level CSPRNG 字节
  - `NextInt() -> int` — 便捷 wrapper：4 字节 → int32（保留符号）
  - `NextLong() -> long` — 便捷 wrapper：8 字节 → int64
  - `NextU32Bounded(int bound) -> int` — `[0, bound)` 均匀分布（rejection sampling 避免 modulo bias）
- 新增 1 个 VM builtin `__crypto_random_bytes(n: i64) -> byte[]`（位置：BUILTINS array，紧跟 `__obj_upgrade_weak` 之后；index 由 BUILTINS array 自动分配）
- Rust 端 `corelib/crypto.rs` 新文件 + `getrandom` crate 依赖
- wasm32 throw `Std.NotSupportedException`（同 z42.net K1 / process 的 pattern，cfg-gated）
- 把 `docs/design/stdlib/crypto.md` Deferred CSPRNG 段移到 v0 已落地

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.crypto/src/Random.z42` | NEW | `Std.Crypto.Random` 类（4 个 static method） |
| `src/libraries/z42.crypto/tests/random_basic.z42` | NEW | basic functional test：长度 / 非全零 / 0 边界 / 负值 throw |
| `src/libraries/z42.crypto/tests/random_distribution.z42` | NEW | 统计性测试：1000 调用 NextInt 跨 8 个 bucket 均匀（χ² loose bound） |
| `src/runtime/src/corelib/crypto.rs` | NEW | `builtin_crypto_random_bytes` 实现 + getrandom backing |
| `src/runtime/src/corelib/crypto_tests.rs` | NEW | Rust 单测 |
| `src/runtime/src/corelib/mod.rs` | MODIFY | `pub mod crypto;` + BUILTINS entry |
| `src/runtime/Cargo.toml` | MODIFY | deps += `getrandom = "0.2"` |
| `docs/design/stdlib/crypto.md` | MODIFY | v0 段加 Random API；Deferred CSPRNG 条目删 |
| `docs/design/stdlib/roadmap.md` | MODIFY | crypto.md 表 ✅ 标记 CSPRNG；删 "未来包" 表里 CSPRNG 行（若有） |
| `src/libraries/README.md` | MODIFY | 同步 z42.crypto 行 |

**只读引用**：
- `src/libraries/z42.crypto/src/Sha256.z42` — 命名风格参考
- `src/libraries/z42.random/src/Random.z42` — 命名空间区分对照
- `src/runtime/src/corelib/network.rs` — wasm32 fallback pattern
- `src/runtime/src/corelib/bench.rs` — 单 builtin file 结构模板

## Out of Scope

- HKDF / PBKDF2 / Argon2 / scrypt（KDF 全栈，等独立 spec）
- AES / ChaCha20 / 对称加密
- Ed25519 / X25519 / RSA / ECDH（公钥加密）
- `Std.Crypto.SecureRandom` 流式接口 + 在 `Std.Random` 加抽象 trait（current scope 只暴露 static methods，无 stateful instance）
- wasm32 真 CSPRNG（getrandom `js` feature / WASI）—— follow-up
- range overload (`NextInt(min, max)`)—— 等真用例驱动

## Open Questions

(none — User has approved upfront, no decision points needed)
