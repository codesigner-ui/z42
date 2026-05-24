# z42.crypto

Cryptographic primitives — hashing, MAC, key derivation, CSPRNG.

## v0 scope

- SHA-256 (FIPS 180-4) — `Std.Crypto.Sha256`
  - `Hash(byte[]) -> byte[32]`
  - `HashString(string) -> byte[32]`
  - `HashHex(byte[]) -> string`
  - `HashStringHex(string) -> string`

- HMAC-SHA256 (RFC 2104) — `Std.Crypto.HmacSha256` (add-hmac-sha256, 2026-05-24)
  - `Compute(byte[] key, byte[] message) -> byte[32]`
  - `ComputeString(string key, string message) -> byte[32]`
  - `ComputeHex(byte[] key, byte[] message) -> string`
  - `ComputeStringHex(string key, string message) -> string`

Pure-script implementation built on `Sha256.Hash`. State held as `long`
(i64) masked to 32 bits at every op boundary — z42 `int` is signed i32 and
overflows on the message schedule additions.

**命名约定**：mirror `Sha256` — distinct method name per parameter form
(`Compute` / `ComputeString` / `ComputeHex` / `ComputeStringHex`) instead
of overload-by-arg-type. z42 当前 overload 解析对 `byte[]` vs `string`
歧义（曾在 BinaryWriter / JsonValue.Parse 踩过），distinct names 既绕开
该限制也跟 stdlib 既有风格一致。

**测试**：RFC 4231 §4.2-4.4 / §4.5 / §4.7 / §4.8 全部覆盖；§4.6
（HMAC-SHA-256-128 truncation）跳过，用户需要时可 `result[:16]`。

## Deferred / Future Work

### CSPRNG（`Std.Crypto.Random`）

- **来源**：add-z42-crypto 计划但 v0 未做
- **触发原因**：依赖 OS-level `getrandom` / `BCryptGenRandom` syscall；z42 还没有 syscall 抽象层
- **触发条件**：z42.os / z42.io.fs 落地后，配合 `Std.Random` API 区分 deterministic vs secure 两层
