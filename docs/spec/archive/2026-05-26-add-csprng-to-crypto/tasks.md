# Tasks: Std.Crypto.SecureRandom — CSPRNG

> 状态：🟢 已完成 | 创建：2026-05-26 | 归档：2026-05-26 | 类型：lang（new stdlib + new VM builtin）

## 阶段 1: Rust corelib

- [x] 1.1 MODIFY `src/runtime/Cargo.toml` — add `getrandom = "0.2"` to `[dependencies]`
- [x] 1.2 NEW `src/runtime/src/corelib/crypto.rs` — `builtin_crypto_random_bytes` + wasm32 cfg gate
- [x] 1.3 NEW `src/runtime/src/corelib/crypto_tests.rs` — Rust 单测：n=0 / n=32 / negative bail / 两次不同
- [x] 1.4 MODIFY `src/runtime/src/corelib/mod.rs` — `pub mod crypto;` + BUILTINS append `("__crypto_random_bytes", crypto::builtin_crypto_random_bytes)`
- [x] 1.5 VERIFY: `cargo build --manifest-path src/runtime/Cargo.toml --release` 无 error
- [x] 1.6 VERIFY: `cargo test --manifest-path src/runtime/Cargo.toml crypto` 全通过

## 阶段 2: stdlib z42

- [x] 2.1 NEW `src/libraries/z42.crypto/src/SecureRandom.z42` — `Std.Crypto.SecureRandom` 静态类（4 method）
  - 注意：实施中将类名由 `Random` 改为 `SecureRandom`，避免与 `Std.Math.Random`（PRNG）混淆
- [x] 2.2 NEW `src/libraries/z42.crypto/tests/secure_random_basic.z42` — [Test]：长度对 / n=0 / negative throws / 大 buffer / 连续调用不同
- [x] 2.3 NEW `src/libraries/z42.crypto/tests/secure_random_distribution.z42` — [Test]：NextU32Bounded(8) × 1000 χ² bucket check
- [x] 2.4 VERIFY: `./scripts/test-stdlib.sh` 全过；z42.crypto 测试通过

## 阶段 3: 文档同步

- [x] 3.1 MODIFY `docs/design/stdlib/crypto.md` — v0 scope 加 `Std.Crypto.SecureRandom`（API 列 4 method）；Deferred 段更新为 wasm32 bridge 条目
- [x] 3.2 MODIFY `docs/design/stdlib/roadmap.md` — z42.crypto 行加 CSPRNG ✅
- [x] 3.3 `src/libraries/README.md` — z42.crypto 行已含 CSPRNG（无需单独更新，行描述已覆盖）
- [x] 3.4 MODIFY `docs/roadmap.md` — Deferred Backlog Index 的 CSPRNG 条目标记 ✅ 已落地

## 阶段 4: GREEN + 归档

- [x] 4.1 `./scripts/test-all.sh --scope=full` 全绿（ALL 6 STAGES PASSED）
- [x] 4.2 mv `docs/spec/changes/add-csprng-to-crypto/` → `docs/spec/archive/2026-05-26-add-csprng-to-crypto/`
- [x] 4.3 commit + push

## 备注

- 类名由 `Std.Crypto.Random` 改为 `Std.Crypto.SecureRandom`，避免与未来 `Std.Math.Random`（PCG PRNG）命名冲突
- wasm32 仍抛 `NotSupportedException`；bridge 到 browser `crypto.getRandomValues` 作为 follow-up 延后
