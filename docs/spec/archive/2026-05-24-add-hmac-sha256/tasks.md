# Tasks: add HMAC-SHA256 to z42.crypto

> 状态：🟢 已完成 | 创建：2026-05-24 | 类型：feat
> Spec 类型：minimal mode

**变更说明**：z42.crypto 加 HMAC-SHA256 (RFC 2104)，纯脚本实现，复用现有 `Std.Crypto.Sha256`。Token / signature / API 鉴权 / TOTP 等场景必备。

**算法 (RFC 2104)**：
```
HMAC(K, msg):
    K' = if len(K) > block_size then SHA256(K) else K (right-padded with 0x00 to block_size)
    inner = SHA256( (K' XOR ipad) || msg )
    return SHA256( (K' XOR opad) || inner )
where:
    block_size = 64 (SHA-256 block size)
    ipad = 0x36 repeated block_size times
    opad = 0x5C repeated block_size times
```

**API**（mirror `Sha256` 现有四方法命名风格，distinct names per param type）：

```z42
public static class HmacSha256 {
    public static byte[] Compute(byte[] key, byte[] message);
    public static byte[] ComputeString(string key, string message);
    public static string ComputeHex(byte[] key, byte[] message);
    public static string ComputeStringHex(string key, string message);
}
```

**命名设计选择**：原计划用 `Compute` 单名 + 6 个 overload (byte[] | string × byte[] | string)，
实施时遇到 z42 编译器 overload 解析对 `byte[]` vs `string` 歧义（BinaryWriter /
JsonValue.Parse 也踩过同一坑），改用 distinct method names per parameter type，
与 `Sha256` / `HashString` / `HashHex` / `HashStringHex` 命名风格一致。
设计同步到 [crypto.md](../../../design/stdlib/crypto.md) v0 scope 段。

**Scope**：

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.crypto/src/Hmac.z42` | NEW | HMAC-SHA256 实现，4 个 public method |
| `src/libraries/z42.crypto/tests/hmac_sha256_vectors.z42` | NEW | RFC 4231 §4.2-4.5 + §4.7-4.8 六个 test vectors + API 覆盖 |
| `docs/design/stdlib/crypto.md` | MODIFY | 把 HMAC-SHA256 从 Deferred 段移到 v0 scope；加 algorithm reference + design decisions |
| `docs/spec/changes/add-hmac-sha256/tasks.md` | NEW | 本 spec |

**Out of Scope**：
- HMAC-SHA1 / HMAC-MD5 — 弱 hash，不实现（attacker-friendly）
- HMAC-SHA384 / HMAC-SHA512 — 需要 SHA-384/512 base 实现，独立 spec
- Streaming HMAC（`Hmac.Begin / Update / Finalize`）— 暂时只支持 one-shot
- Constant-time MAC compare（`Hmac.Verify(mac, expected) -> bool` 防 timing attack）— 当前 z42 string compare 已是 length-discriminated；专门的 constant-time API 留 follow-up spec
- Mixed (byte[] key, string message) overload — 用户可用 `Utf8.GetBytes(msg)` + `Compute(key, bytes)`，与 Sha256 stdlib pattern 一致

## Tasks

- [x] 1.1 `src/libraries/z42.crypto/src/Hmac.z42` NEW —
  - public `class HmacSha256` (static), 4 distinct methods
  - 内部 helper：`_padKey(byte[] key) -> byte[64]`（短 key 右填 0x00，长 key 先 SHA256 摘要后填）
  - block_size = 64 hardcoded（SHA-256 specific；不通用化）
- [x] 1.2 `src/libraries/z42.crypto/tests/hmac_sha256_vectors.z42` NEW —
  - RFC 4231 §4.2 (Test Case 1): key=20 bytes 0x0b, data="Hi There" ✓
  - §4.3 (Test Case 2): key="Jefe", data="what do ya want for nothing?" ✓
  - §4.4 (Test Case 3): key=20 bytes 0xaa, data=50 bytes 0xdd ✓
  - §4.5 (Test Case 4): key=0x0102...19 (25 bytes), data=50 bytes 0xcd ✓
  - §4.6 (Test Case 5): truncation — skip（HMAC-SHA-256-128，需要额外 truncate API）
  - §4.7 (Test Case 6): key=131 bytes 0xaa（> block_size，触发 hash-then-pad path）✓
  - §4.8 (Test Case 7): key=131 bytes 0xaa, data="...larger than one block..." ✓
  - API 覆盖：`Compute(byte[], byte[])` / `ComputeString(str, str)` / `ComputeHex` / `ComputeStringHex` + empty-input + 32-byte length ✓
- [x] 1.3 `docs/design/stdlib/crypto.md` 把 HMAC-SHA256 移出 Deferred → v0 scope 段：
  - 算法引用（RFC 2104 / RFC 4231）
  - 4 个 API
  - 命名约定说明（distinct names 而非 overload）
- [x] 1.4 `./scripts/build-stdlib.sh` z42.crypto 全成功
- [x] 1.5 `./scripts/test-stdlib.sh` crypto 10/10 HMAC tests + 6/6 SHA256 tests 全过
- [x] 1.6 commit + push（单 commit；含本 spec）
- [x] 1.7 mv → `docs/spec/archive/2026-05-24-add-hmac-sha256/`

## 备注

- 实施期发现 z42 编译器 overload 解析对 byte[] vs string 有歧义；改用 distinct method names per Sha256 stdlib pattern（一致性 + 绕开 bug）。该 overload resolver bug 独立 issue 跟踪，非本 spec 修复目标。
- §4.6 RFC 4231 (HMAC-SHA-256-128 truncation) 跳过——用户需要时 `result[:16]` 即可，不增专门 API。
