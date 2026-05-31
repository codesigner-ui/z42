# Proposal: MD5 hash + HMAC-MD5 in z42.crypto

## Why

z42.crypto 当前覆盖 SHA-1/256/384/512 + SHA-3 系列 + BLAKE2/3 + HMAC for
SHA-1/2 family，但**没有 MD5**。
MD5 已被破解（碰撞攻击 2004 起；前缀碰撞 2008 起），
新代码绝不应用 MD5 做安全用途。

但 MD5 仍是若干现实交互场景的**强制依赖**：

1. **HTTP Digest auth (RFC 2617)** — 是即将做的 `add-z42-net-http-digest-auth`
   spec 的前置；绝大多数 legacy server / router admin 接口仍用 Digest-MD5
2. **文件格式 / 数据库校验和** — git fetch via dumb HTTP protocol, ETag,
   torrent .info, S3 ETag, MySQL OLD_PASSWORD 等
3. **legacy protocol 兼容** — APR auth, CRAM-MD5 SASL mechanism

不做 MD5，调用方就得 shell out 到 `md5` / `openssl md5`，或者自己写一份
（容易出 endianness / padding bug）。直接 ship 一个明确标"legacy-only,
do not use for new security"的 `Std.Crypto.Md5` 是最务实方案，与 Sha1
已有的"⚠️ broken; legacy interop only" 注释模式完全对齐。

## What Changes

新增：

- `src/libraries/z42.crypto/src/Md5.z42` — 纯脚本 RFC 1321 实现
  - `Md5.Hash(byte[]) -> byte[16]`
  - `Md5.HashString(string) -> byte[16]`
  - `Md5.HashHex(byte[]) -> string` (32 lowercase hex chars)
  - `Md5.HashStringHex(string) -> string`
- `src/libraries/z42.crypto/src/Hmac.z42` MODIFY — 加 `HmacMd5` 系列
  （现有 HmacSha1/Sha256/Sha384/Sha512 模式扩展）
- `src/libraries/z42.crypto/tests/md5_vectors.z42` — RFC 1321 test suite
  + NIST CAVP
- `src/libraries/z42.crypto/tests/hmac_md5.z42` — RFC 2104 § 附录 / RFC 2202

依赖文档：

- `docs/design/stdlib/crypto.md` 加 Md5 / HmacMd5 行（标 legacy）
- `docs/design/stdlib/roadmap.md` — MD5 不在原 Deferred backlog，加一行说明
  "落地 2026-05-31 as Digest-auth 前置；显式 legacy-only"

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.crypto/src/Md5.z42` | NEW | RFC 1321 MD5 实现 |
| `src/libraries/z42.crypto/src/Hmac.z42` | MODIFY | 加 `HmacMd5.Compute / ComputeHex / ComputeString` |
| `src/libraries/z42.crypto/tests/md5_vectors.z42` | NEW | RFC 1321 §A.5 + NIST 试向量 |
| `src/libraries/z42.crypto/tests/hmac_md5_vectors.z42` | NEW | RFC 2202 §2 7 试向量 |
| `docs/design/stdlib/crypto.md` | MODIFY | API 表 + legacy 警告 |

**只读引用**：

- `src/libraries/z42.crypto/src/Sha1.z42`（实现结构模板：pure-script
  block compression + long-mask-32 pattern）
- `src/libraries/z42.crypto/src/Hmac.z42`（HMAC outer/inner pad pattern）

## Out of Scope

- Streaming MD5（`Md5.Hasher` slot-based）— 与 Sha 系列保持同步 v0 只有
  one-shot；streaming 留 follow-up
- MD2 / MD4 — 比 MD5 更弱、用例更窄；不补
- `Md5.Verify(expected, actual)` constant-time compare helper — 见 Sha1
  的同状态（未提供）；MD5 不当用做敏感比对，省了

## Open Questions

- [ ] 是否在 Md5.z42 顶部加 `[Obsolete]` 风格属性？
  **决定**：z42 当前无 `[Obsolete]` attribute（features.md 未列）。改用
  显眼的注释 + crypto.md 表里 "legacy" 标记。等 attribute landed 后回填。
