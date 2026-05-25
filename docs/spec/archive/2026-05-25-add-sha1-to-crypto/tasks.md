# Tasks: add SHA-1 to z42.crypto

> 状态：🟢 已完成 | 创建：2026-05-25 | 类型：feat (extend existing pkg)
> Spec 类型：minimal mode

**变更说明**：在 `z42.crypto` 加 `Std.Crypto.Sha1` 类，配套 `Std.Crypto.HmacSha1`.
镜像 SHA-256 / HmacSha256 的 API + 实现风格（FIPS 180-4 / RFC 2104）。

**Why now**:
- K4 WebSocket 的 `Sec-WebSocket-Accept` 验证依赖 SHA-1 (RFC 6455 §4.2.2)。
  当前 K4 客户端跳过该验证 (`add-z42-net-websocket-accept-validate` 等)；
  加 SHA-1 之后该 follow-up 可以做。
- Git object hash / 老式 HMAC-SHA1 兼容场景。
- SHA-1 弱不抗碰撞（不应用于 collision-sensitive crypto） — 但仍有合法
  legacy / protocol-compat 用例。

**API** (mirror Sha256):

```z42
public static class Sha1 {
    public static byte[] Hash(byte[] data);          // 20 bytes (160 bits)
    public static byte[] HashString(string s);
    public static string HashHex(byte[] data);
    public static string HashStringHex(string s);
}

public static class HmacSha1 {
    public static byte[] Compute(byte[] key, byte[] message);    // 20 bytes
    public static byte[] ComputeString(string key, string message);
    public static string ComputeHex(byte[] key, byte[] message);
    public static string ComputeStringHex(string key, string message);
}
```

## Algorithm (FIPS 180-4 §6.1.2 SHA-1)

- Block size: 64 bytes (512 bits) — same as SHA-256
- Digest: 20 bytes (160 bits)
- Five 32-bit working variables: H0..H4
- 80 rounds with three round functions: Ch, Parity, Maj
- Four constants K0..K3 used per 20-round phase
- Initial hash values: 0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476, 0xc3d2e1f0
- Constants: 0x5A827999, 0x6ED9EBA1, 0x8F1BBCDC, 0xCA62C1D6

## Scope

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.crypto/src/Sha1.z42` | NEW | pure-script SHA-1 |
| `src/libraries/z42.crypto/src/Hmac.z42` | MODIFY | 加 `HmacSha1` class 平行 `HmacSha256` |
| `src/libraries/z42.crypto/tests/sha1_vectors.z42` | NEW | FIPS 180-2 + NIST CAVP test vectors |
| `src/libraries/z42.crypto/tests/hmac_sha1_vectors.z42` | NEW | RFC 2202 test vectors |
| `src/libraries/z42.crypto/README.md` | MODIFY | 加 SHA-1 / HMAC-SHA1 section |
| `docs/design/stdlib/crypto.md` | MODIFY | 加 v0 scope SHA-1 / HMAC-SHA1; `crypto-future-sha1` Deferred → ✅ |

## Out of scope

- SHA-1 collision detection / SHAttered mitigation — pure SHA-1 only
- SHA-2 family (224 / 384 / 512) — separate spec
- SHA-3 / Keccak — separate spec

## Tasks

- [x] 1.1 `src/libraries/z42.crypto/src/Sha1.z42` NEW — full impl
- [x] 1.2 `src/libraries/z42.crypto/src/Hmac.z42` MODIFY — append `HmacSha1` class (块大小 64 same as SHA-256)
- [x] 2.1 `tests/sha1_vectors.z42` — FIPS 180-2:
  - "abc" → a9993e364706816aba3e25717850c26c9cd0d89d
  - "" → da39a3ee5e6b4b0d3255bfef95601890afd80709
  - "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq" → 84983e441c3bd26ebaae4aa1f95129e5e54670f1
  - 1 million 'a' (skip; too slow for CI)
- [x] 2.2 `tests/hmac_sha1_vectors.z42` — RFC 2202 §3 test cases
- [x] 3.1 README + crypto.md docs
- [x] 4.1 build + test
- [x] 4.2 commit + push + archive

## 备注

- SHA-1 与 SHA-256 共用 64-byte block size → HmacSha1 与 HmacSha256 padding 路径同步
- Pure-script，无 VM 改动
