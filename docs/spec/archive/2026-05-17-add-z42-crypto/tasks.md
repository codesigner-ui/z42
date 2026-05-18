# Tasks: add z42.crypto (SHA-256 + HMAC-SHA256)

> 状态：🟡 进行中 | 创建：2026-05-17 | 类型：feat（纯脚本 stdlib，无新 VM/IR）
> Spec 类型：minimal mode

## 背景

z42 stdlib 缺加密原语，build-driver / API 鉴权 / 配置签名 / 资源完整性校验都用得到。
对标 .NET `System.Security.Cryptography.SHA256` / Rust `sha2` crate。

v0 范围：**SHA-256** + **HMAC-SHA256**，纯脚本实现（不引入 FFI、不引入新 corelib
builtin）。算法是 FIPS 180-4 标准，输出与所有合规实现 byte-identical。

CSPRNG / SHA-512 / AES / RSA 都留独立 follow-up spec（CSPRNG 需要 1 个 corelib
builtin 从 OS 拿 entropy；其他依赖更复杂的 modular arithmetic / 算法或 FFI）。

## API Surface (v0)

```z42
namespace Std.Crypto;

// SHA-256 (FIPS 180-4). Pure-script implementation on z42 int / byte
// arithmetic. Output: 32-byte digest.
public static class Sha256 {
    // Hash a byte[] message; returns 32-byte digest.
    public static byte[] Hash(byte[] data);

    // Convenience: UTF-8 encode the string then hash.
    public static byte[] HashString(string s);

    // Convenience: hash + lowercase hex encode (64-char output).
    public static string HashHex(byte[] data);
    public static string HashStringHex(string s);
}

// HMAC-SHA256 (RFC 2104). Block-size = 64 bytes.
public static class Hmac {
    public static byte[] Sha256(byte[] key, byte[] data);
    public static string Sha256Hex(byte[] key, byte[] data);
}
```

## 设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. 算法实现 | 纯脚本 / FFI to OpenSSL/libsodium | 纯脚本 | 无 FFI 依赖，跨平台零差异；SHA-256 算法稳定 + 输入 < 1MB 性能可接受；FFI 留 v1 perf upgrade |
| 2. byte[] vs int[] | byte[] 输入 / int[] 输入 | byte[] | z42 已有 byte[] 在 z42.io.binary；与 .NET / Rust API 对齐 |
| 3. Hash 输出形态 | byte[] only / +Hex / +Base64 | byte[] + Hex 便利方法 | Base64 留 follow-up（用 z42.encoding 容易加） |
| 4. SHA-512 / SHA-384 | 同 v0 / 独立 spec | 独立 spec | 算法形态类似但 64-bit op 多；v0 先证明纯脚本路径可行 |
| 5. CSPRNG | 同 v0 / 独立 spec | 独立 spec | 需 1 个 corelib builtin（/dev/urandom + BCryptGenRandom）；不阻塞 SHA/HMAC |
| 6. AES / RSA | 同 v0 / 独立 spec | 独立 spec | 数量级更复杂；AES 8 轮 sbox 表 + RSA 大数运算；走 FFI 更合理 |

## 不在 v0（follow-up specs）

- **`add-crypto-csprng`**：`Crypto.RandomBytes(int n)` — 需 1 个 corelib builtin
- **`add-crypto-sha512`**：SHA-384 / SHA-512 / SHA-3
- **`add-crypto-aes`**：AES-GCM / AES-CBC（可能走 FFI）
- **`add-crypto-rsa`**：RSA 签名 / 加密（需大数 / 模幂）
- **`add-crypto-pbkdf2`**：密码哈希（基于 HMAC，加 v0 后容易）
- **`add-crypto-base64`**：`HashBase64` 便利方法（一行包装 z42.encoding.Base64）

## Scope（允许改动的文件）

| 文件路径 | 变更 | 说明 |
|---------|------|------|
| `src/libraries/z42.crypto/z42.crypto.z42.toml` | NEW | 包 manifest，dep on z42.core + z42.encoding + z42.io.binary |
| `src/libraries/z42.crypto/src/Sha256.z42` | NEW | SHA-256 静态类（FIPS 180-4 算法 + 便利方法） |
| `src/libraries/z42.crypto/src/Hmac.z42` | NEW | HMAC-SHA256 静态类（RFC 2104） |
| `src/libraries/z42.crypto/tests/sha256_vectors.z42` | NEW | NIST 测试向量：空 / "abc" / 56 字节 / 长输入 |
| `src/libraries/z42.crypto/tests/hmac_sha256_vectors.z42` | NEW | RFC 4231 测试向量 |
| `src/libraries/z42.workspace.toml` | MODIFY | default-members 加 `z42.crypto` |
| `scripts/build-stdlib.z42` | MODIFY | LIBS 加 `z42.crypto` + index.json 加 `Std.Crypto` |

## 阶段

- [ ] 1.1 NEW `src/libraries/z42.crypto/z42.crypto.z42.toml`
- [ ] 1.2 NEW `src/libraries/z42.crypto/src/Sha256.z42`（FIPS 180-4 算法 + HashString + HashHex 便利）
- [ ] 1.3 NEW `src/libraries/z42.crypto/src/Hmac.z42`（基于 Sha256）
- [ ] 2.1 NEW `src/libraries/z42.crypto/tests/sha256_vectors.z42`（NIST 标准向量 ≥4）
- [ ] 2.2 NEW `src/libraries/z42.crypto/tests/hmac_sha256_vectors.z42`（RFC 4231 向量 ≥3）
- [ ] 3.1 MODIFY `src/libraries/z42.workspace.toml` + `scripts/build-stdlib.z42`
- [ ] 4.1 GREEN：build-stdlib + test-stdlib z42.crypto 全过 + dotnet test
- [ ] 5.1 commit + push + archive
