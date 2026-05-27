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

- SHA-1 (FIPS 180-4) — `Std.Crypto.Sha1` (add-sha1-to-crypto, 2026-05-25)
  - `Hash(byte[]) -> byte[20]`
  - `HashString(string) -> byte[20]`
  - `HashHex(byte[]) -> string`
  - `HashStringHex(string) -> string`
  - ⚠️ SHA-1 is broken for collision-resistant uses since SHAttered (2017).
    Acceptable for HMAC-SHA1, git compat, Sec-WebSocket-Accept, legacy
    protocol interop. Do **not** use for new content-addressing or signature
    schemes — use SHA-256.

- HMAC-SHA1 (RFC 2104) — `Std.Crypto.HmacSha1` (add-sha1-to-crypto, 2026-05-25)
  - `Compute(byte[] key, byte[] message) -> byte[20]`
  - `ComputeString(string key, string message) -> byte[20]`
  - `ComputeHex(byte[] key, byte[] message) -> string`
  - `ComputeStringHex(string key, string message) -> string`
  - HMAC-SHA1 is **not** broken by SHAttered (the HMAC construction protects
    even with weak hashes). Still in use: TOTP (RFC 6238 default),
    AWS Signature V2, etc.

- SHA-512 + SHA-384 (FIPS 180-4) — `Std.Crypto.Sha512` / `Std.Crypto.Sha384` (add-sha512-to-crypto, 2026-05-27)
  - `Hash(byte[]) -> byte[64]` (SHA-512) / `byte[48]` (SHA-384)
  - Same `Hash / HashString / HashHex / HashStringHex` surface as Sha256
  - SHA-384 shares the SHA-512 compression function (FIPS §6.5) — only IV and output-truncation differ
  - 80 rounds, 128-byte blocks, 64-bit words (z42 `long` natural fit; logical right shift via `_lshr64` to dodge `>>` sign-extension)

- HMAC-SHA-512 + HMAC-SHA-384 (RFC 2104) — `Std.Crypto.HmacSha512` / `Std.Crypto.HmacSha384` (add-hmac-sha512-sha384, 2026-05-27)
  - Same `Compute / ComputeString / ComputeHex / ComputeStringHex` surface as HmacSha256
  - 128-byte block size (vs HmacSha256's 64); HmacSha384 reuses the 128-byte block since SHA-384 shares SHA-512's compression
  - 27 NIST FIPS 180-2 + RFC 4231 vectors GREEN end-to-end

- HKDF (RFC 5869) — `Std.Crypto.HkdfSha256` / `Std.Crypto.HkdfSha512` (add-hkdf, 2026-05-27)
  - `Derive(salt, ikm, info, length) -> byte[]` — one-shot Extract+Expand
  - `Extract(salt, ikm) -> byte[HashLen]` — pseudo-random key from input keying material
  - `Expand(prk, info, length) -> byte[length]` — derived bytes from PRK + context
  - Length cap: 255 × HashLen (8160 for SHA-256, 16320 for SHA-512)
  - Null/empty salt substituted with HashLen zero bytes per RFC §2.2
  - Verified against all 3 RFC 5869 §A vectors (SHA-256) + SHA-512 cross-check vs Python cryptography

- OS CSPRNG — `Std.Crypto.SecureRandom` (add-csprng-to-crypto, 2026-05-26)
  - `GetBytes(int n) -> byte[]` — fill `n` bytes from OS entropy source
  - `NextInt() -> int` — uniform over full i32 range
  - `NextLong() -> long` — uniform over full i64 range
  - `NextU32Bounded(int bound) -> int` — uniform in `[0, bound)` via rejection sampling
  - Bridges to `__crypto_random_bytes` builtin: Linux `getrandom(2)` / macOS `getentropy` / Windows `BCryptGenRandom`
  - wasm32 throws `NotSupportedException` (browser `crypto.getRandomValues` bridge is follow-up)

Pure-script implementation built on `Sha256.Hash` / `Sha1.Hash`. State held as `long`
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

### CSPRNG wasm32 bridge（`Std.Crypto.SecureRandom` on wasm32）

- **来源**：add-csprng-to-crypto (2026-05-26)；native 已落地，wasm32 仍抛 `NotSupportedException`
- **触发原因**：wasm32 无 `getrandom` syscall；需桥接到浏览器 `crypto.getRandomValues` 或 WASI `random_get`
- **前置依赖**：wasm32 WASI 运行时路径或 JS interop bridge
- **触发条件**：wasm32 target 落地时
