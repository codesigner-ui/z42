# z42.crypto

Cryptographic primitives — hashing, MAC, key derivation, CSPRNG.

## v0 scope

- SHA-256 (FIPS 180-4) — `Std.Crypto.Sha256`
  - `Hash(byte[]) -> byte[32]`
  - `HashString(string) -> byte[32]`
  - `HashHex(byte[]) -> string`
  - `HashStringHex(string) -> string`

Pure-script implementation. State held as `long` (i64) masked to 32 bits at
every op boundary — z42 `int` is signed i32 and overflows on the message
schedule additions.

## Deferred / Future Work

### HMAC-SHA256

- **来源**：add-z42-crypto 计划但 v0 未做
- **触发原因**：HMAC 需要先确认 z42 string runtime 的 byte-level 操作 + 性能模型；与 SHA-256 算法独立
- **触发条件**：v1 stdlib crypto 阶段，配合 KDF / 签名 API 一并设计
- **当前 workaround**：用户需要 HMAC 时可用 RFC 2104 公式手写：`HMAC-SHA256(K, m) = SHA-256(K' XOR opad || SHA-256(K' XOR ipad || m))`

### CSPRNG（`Std.Crypto.Random`）

- **来源**：add-z42-crypto 计划但 v0 未做
- **触发原因**：依赖 OS-level `getrandom` / `BCryptGenRandom` syscall；z42 还没有 syscall 抽象层
- **触发条件**：z42.os / z42.io.fs 落地后，配合 `Std.Random` API 区分 deterministic vs secure 两层
