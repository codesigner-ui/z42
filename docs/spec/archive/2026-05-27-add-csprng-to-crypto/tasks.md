# Tasks: Std.Crypto.SecureRandom — CSPRNG

> 状态：🟢 已完成 | 创建：2026-05-26 | 归档：2026-05-27 | 类型：lang（new stdlib + new VM builtin）

## 阶段 1: Rust corelib

- [x] 1.1 MODIFY `src/runtime/Cargo.toml` — add `getrandom = "0.2"` to `[dependencies]`
- [x] 1.2 NEW `src/runtime/src/corelib/crypto.rs` — `builtin_crypto_random_bytes` + wasm32 cfg gate
- [x] 1.3 NEW `src/runtime/src/corelib/crypto_tests.rs` — 6 Rust 单测
- [x] 1.4 MODIFY `src/runtime/src/corelib/mod.rs` — `pub mod crypto;` + BUILTINS append entry
- [x] 1.5 VERIFY: `cargo build --release` 无 error
- [x] 1.6 VERIFY: `cargo test --lib corelib::crypto -- --test-threads=1` 全过（6/6）

## 阶段 2: stdlib z42

- [x] 2.1 NEW `src/libraries/z42.crypto/src/SecureRandom.z42` — `Std.Crypto.SecureRandom` 静态类（4 method + extern）
- [x] 2.2 NEW `src/libraries/z42.crypto/tests/secure_random_basic.z42` — 11 个 [Test]
- [x] 2.3 NEW `src/libraries/z42.crypto/tests/secure_random_distribution.z42` — 2 个 [Test]
- [x] 2.4 VERIFY: `./scripts/test-stdlib.sh z42.crypto` 全过（13/13 new + 6 file lib total）

## 阶段 3: 文档同步

- [x] 3.1 MODIFY `docs/design/stdlib/crypto.md` — v0 段加 SecureRandom API；Deferred 段加 wasm32 bridge entry
- [x] 3.2 MODIFY `docs/design/stdlib/roadmap.md` — z42.crypto 行 ✅ CSPRNG
- [x] 3.3 MODIFY `docs/roadmap.md` — Deferred Backlog Index CSPRNG 行 ✅
- [x] 3.4 MODIFY `src/libraries/README.md` — shipping 表加 z42.crypto 行

## 阶段 4: GREEN + 归档

- [x] 4.1 `./scripts/test-all.sh --scope=full` 全绿（6 stages，169 stdlib files in 22 libs）
- [x] 4.2 mv `docs/spec/changes/add-csprng-to-crypto/` → `docs/spec/archive/2026-05-27-add-csprng-to-crypto/`
- [x] 4.3 commit + push

## 备注

- **Naming pivot**: 原拟名 `Std.Crypto.Random` 撞 `Std.Random.Random` 短名 → 改 `SecureRandom`（对标 .NET RandomNumberGenerator）
- **NextInt bit-pattern fix**: 实测 `((int)b[0] << 24)` 在 z42 不保留 sign bit；改成 long 域算位运算再 `(int)long` 截断（保留低 32 位）。NextLong 自始至终 long 域，无此问题
- **unreachable throw**: `while(true) { ... return X; }` 后加 `throw new Exception("unreachable")` 满足 typechecker（同 TomlParser pattern）
- **Pre-existing test failures**: `cargo test --release` 整套 lib test 有 16 个 pre-existing E0599 错误（`gc/region` debug_assertions-gated `validate` 在 release mode 不存在 + `ArcMagrGC::debug_validate_invariants`）— 非本 spec 引入，与我代码无关。debug mode 跑我自己的测试通过。
