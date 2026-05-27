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

- scrypt (RFC 7914) — `Std.Crypto.Scrypt` (add-scrypt, 2026-05-27)
  - `Derive(password, salt, N, r, p, dkLen) -> byte[]` — memory-hard password hash
  - Pure-script Salsa20/8 + BlockMix + ROMix over the shipped PBKDF2-HMAC-SHA-256
  - N must be a power of 2 ≥ 2; `r*p < 2^30` per RFC §6
  - Verified against RFC 7914 §11 vector #1 (N=16, r=1, p=1) — larger vectors (N≥1024)
    are correct algorithmically but too slow for interpreted z42 in CI; cdylib-backed
    `Scrypt.DeriveNative` is a follow-up for production hashing throughput

- SHA-3 (FIPS 202) — `Std.Crypto.Sha3` (add-sha3, 2026-05-27)
  - `Hash224(byte[]) -> byte[28]` / `Hash256(byte[]) -> byte[32]` / `Hash384(byte[]) -> byte[48]` / `Hash512(byte[]) -> byte[64]`
  - Each has parallel `HashNxxxString(string) / HashNxxxHex(byte[]) / HashNxxxStringHex(string)` forms — same naming convention as Sha256
  - Sponge construction over Keccak-f[1600] permutation; rates r = 144 / 136 / 104 / 72 bytes (224/256/384/512); 24 rounds θ ρ π χ ι per absorb/squeeze cycle
  - Domain-separation byte `0x06` (FIPS 202)
  - Legacy Keccak (domain byte `0x01`) — `KeccakLegacy256` / `KeccakLegacy512` and their `String`/`Hex`/`StringHex` siblings — provided for Ethereum address derivation, Solidity `keccak256(bytes)` interop, and pre-FIPS Keccak tools. Mixing the two for the same input produces different hashes by design
  - SHAKE extendable-output functions (FIPS 202 §6.2): `Shake128(data, outputLen) -> byte[]` / `Shake256(data, outputLen) -> byte[]` + parallel String/Hex/StringHex forms. Rate r = 168 / 136 bytes; domain byte `0x1F`. Arbitrary output length (squeeze loops through Keccak-f for chunks larger than r). Use cases: stream-cipher / DRBG / SPHINCS+ signatures
  - State held as `long[25]` (flat `state[x + 5*y]`); little-endian lane interpretation per FIPS 202 §B.1
  - Verified against FIPS 202 §A.5 sample vectors ("abc" + 56-byte alphabet message) for all four output lengths + NIST CAVS empty-string vectors

- BLAKE2b (RFC 7693) — `Std.Crypto.Blake2b` (add-blake2b, 2026-05-28)
  - `Hash(byte[]) -> byte[64]` (512-bit default) / `Hash256(byte[]) -> byte[32]` (256-bit common)
  - `HashLen(byte[] data, byte[] key, int outLen) -> byte[outLen]` — variable output (1..64) + optional key (0..64 bytes, keyed-MAC mode)
  - `HashString` / `HashHex` / `HashStringHex` / `Hash256String` / `Hash256Hex` / `Hash256StringHex` convenience surface
  - 12 rounds of `G` mixer over a 16-word working vector; 128-byte block, 64-bit words; SHA-512-style IV
  - Parameter block (`0x01_01_kk_nn` for unkeyed default, with `kk = key length`, `nn = output length`) XORed into `h[0]` so output length and key length both influence the digest — same input with different `outLen` produces unrelated digests
  - Use cases: Argon2 inner compression, IPFS default CID hash, WireGuard handshake, Zcash, NaCl libsodium `crypto_generichash`
  - Verified against RFC 7693 §A.1 ("abc"), §A.1.1 keyed vector + libsodium reference empty / Hash256 vectors

- ChaCha20-Poly1305 AEAD (RFC 8439 §2.8) — `Std.Crypto.ChaCha20Poly1305` (add-chacha20-poly1305, 2026-05-27)
  - `Encrypt(byte[32] key, byte[12] nonce, byte[] aad, byte[] plaintext) -> byte[]` — output = ciphertext || 16-byte tag
  - `Decrypt(byte[32] key, byte[12] nonce, byte[] aad, byte[] ctAndTag) -> byte[]` — constant-time tag verification; throws `ArgumentException` on mismatch
  - Construction: Poly1305 one-time key derived from `ChaCha20(key, nonce, counter=0)[0..32]`; encrypt with ChaCha20 starting at counter=1; authenticate `aad || pad16(aad) || ciphertext || pad16(ciphertext) || len(aad)_8LE || len(ciphertext)_8LE`
  - Use cases: TLS 1.3 (`TLS_CHACHA20_POLY1305_SHA256`), WireGuard transport, age file format, NaCl `crypto_aead_*`; software-friendly AEAD alternative to AES-256-GCM (no AES-NI needed)
  - Verified against RFC 8439 §2.8.2 reference vector ("Ladies and Gentlemen of the class of '99..." + 114-byte plaintext + AAD)

- Poly1305 (RFC 8439 §2.5) — `Std.Crypto.Poly1305` (add-poly1305, 2026-05-27)
  - `Mac(byte[32] key, byte[] message) -> byte[16]` — one-time authenticator over GF(2^130 - 5)
  - `MacHex(byte[32] key, byte[] message) -> string` — hex convenience
  - 32-byte key = r (clamped per RFC §2.5.1) || s (one-time pad)
  - **One-time use only**: the key MUST be fresh for each message — reuse leaks the polynomial coefficient and breaks security entirely. In ChaCha20-Poly1305 AEAD, the key is derived per-message via `ChaCha20(key, nonce, counter=0)[0..32]`
  - Pure-script over `Std.Numerics.BigInt` (z42 has no native 128/160-bit type)
  - Verified against RFC 8439 §2.5.2 ("Cryptographic Forum Research Group") + all three §A.3 reference vectors

- ChaCha20 (RFC 8439) — `Std.Crypto.ChaCha20` (add-chacha20, 2026-05-27)
  - `Encrypt(byte[32] key, byte[12] nonce, byte[] data) -> byte[]` — initial counter = 1 (RFC 8439 §2.4 standalone use)
  - `Decrypt(byte[32] key, byte[12] nonce, byte[] data) -> byte[]` — symmetric (same as Encrypt)
  - `Crypt(byte[32] key, byte[12] nonce, int counter, byte[] data) -> byte[]` — explicit-counter variant
  - `Block(byte[32] key, byte[12] nonce, int counter) -> byte[64]` — single keystream block; exposed for Poly1305 key derivation (counter=0 path of the ChaCha20-Poly1305 AEAD construction)
  - 256-bit key, 96-bit nonce; 20 rounds (10 column + 10 diagonal); 64-byte keystream blocks; pure-script
  - Verified against RFC 8439 §2.3.2 (keystream block) + §2.4.2 (114-byte encryption) reference vectors

- AES (FIPS 197) — `Std.Crypto.Aes` (add-aes, 2026-05-27)
  - `EncryptBlock(byte[] key, byte[16] plaintext) -> byte[16]` — single-block ECB primitive
  - `DecryptBlock(byte[] key, byte[16] ciphertext) -> byte[16]`
  - `EncryptCtr(byte[] key, byte[8] nonce, byte[] data) -> byte[]` — RFC 3686-style nonce||counter CTR mode
  - `DecryptCtr(byte[] key, byte[8] nonce, byte[] data) -> byte[]` — symmetric, same as encrypt
  - `EncryptCbcPkcs7(byte[] key, byte[16] iv, byte[] data) -> byte[]` — CBC mode with PKCS#7 padding (RFC 5652 §6.3); output length is always a positive multiple of 16, full padding block appended when input is already aligned
  - `DecryptCbcPkcs7(byte[] key, byte[16] iv, byte[] data) -> byte[]` — validates + strips PKCS#7 padding; throws `ArgumentException` on malformed padding (likely wrong key/IV/corrupted ciphertext)
  - `EncryptGcm(byte[] key, byte[12] iv, byte[] aad, byte[] plaintext) -> byte[]` — AEAD per NIST SP 800-38D; output is `ciphertext || 16-byte tag`. 96-bit IV path only (other lengths require GHASH-derived J0 — deferred to cdylib backend)
  - `DecryptGcm(byte[] key, byte[12] iv, byte[] aad, byte[] ctAndTag) -> byte[]` — verifies tag with constant-time compare; throws `ArgumentException` if tampered (do NOT swallow this — failure means ciphertext or AAD was modified). Tag length fixed at 16 bytes (NIST recommends ≥ 12; shorter tags are a footgun per RFC 5116 §5.3 and not exposed)
  - Key length selects variant: 16 bytes = AES-128, 24 = AES-192, 32 = AES-256
  - Pure-script implementation (matches Sha256 / Hmac / Hkdf pattern): KeyExpansion + SubBytes / ShiftRows / MixColumns over GF(2^8); flat `int[]` round-key layout because z42 lacks `int[][]` jagged arrays
  - Verified against FIPS 197 §C.1 / §C.2 / §C.3 (block) + NIST SP 800-38A §F.2.1 (CBC) + NIST SP 800-38D / GCM-spec §B.1-4 + B.15 (GCM) reference vectors
  - CTR counter: 8-byte big-endian, starts at 0, increments per 16-byte block; total ≤ 2^64 × 16 bytes effectively unbounded
  - Performance note: pure-script AES at z42-interp speeds is fine for low-rate use (token encryption, small-payload envelopes); bulk encryption wants the cdylib follow-up
  - **Out of scope (deferred)**: GCM / AEAD, Key Wrap, AES-NI / ARMv8 Crypto Extensions hardware acceleration — see Deferred section

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

### aes-future-gcm-non96iv: AES-GCM with non-96-bit IV

- **来源**：add-aes-gcm (2026-05-27)；GCM with 96-bit IV shipped, other IV lengths route through `GHASH_H(IV || pad || len(IV))` to derive J0
- **触发原因**：96-bit IV is by far the most common form (TLS 1.3, WireGuard, AES-GCM-SIV all use 96-bit) — keeps v0 surface minimal
- **前置依赖**：无（pure-script extension to existing GCM)
- **触发条件**：spec compliance for legacy GCM consumers, or AES-GCM-SIV implementation

### aes-future-hw-accel: AES-NI / ARMv8 Crypto Extensions

- **来源**：add-aes (2026-05-27)
- **触发原因**：纯脚本 AES 对低速率（token、小 payload）够用；> 1 MB 批量加密需要硬件加速 10-50× 提升
- **前置依赖**：z42-crypto cdylib 框架（与 z42-compression 类比）
- **触发条件**：实际用户场景出现高吞吐需求时

### CSPRNG wasm32 bridge（`Std.Crypto.SecureRandom` on wasm32）

- **来源**：add-csprng-to-crypto (2026-05-26)；native 已落地，wasm32 仍抛 `NotSupportedException`
- **触发原因**：wasm32 无 `getrandom` syscall；需桥接到浏览器 `crypto.getRandomValues` 或 WASI `random_get`
- **前置依赖**：wasm32 WASI 运行时路径或 JS interop bridge
- **触发条件**：wasm32 target 落地时
