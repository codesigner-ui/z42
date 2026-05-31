# Spec: Md5 + HmacMd5

## ADDED Requirements

### Requirement: `Md5.Hash(byte[]) -> byte[16]`

#### Scenario: RFC 1321 §A.5 empty input

- **WHEN** `Md5.Hash(new byte[0])`
- **THEN** 返回 `byte[16]` 等同十六进制 `d41d8cd98f00b204e9800998ecf8427e`

#### Scenario: RFC 1321 §A.5 "abc"

- **WHEN** `Md5.HashStringHex("abc")`
- **THEN** 返回 `"900150983cd24fb0d6963f7d28e17f72"`

#### Scenario: 1-million-byte buffer (NIST CAVP MD5LongMsg)

- **WHEN** `Md5.Hash(buf)` 其中 `buf[i] = 'a'` 长 1,000,000 bytes
- **THEN** 返回 hex `"7707d6ae4e027c70eea2a935c2296f21"`

### Requirement: `Md5.HashString(string) -> byte[16]` 与 `Md5.HashStringHex`

#### Scenario: UTF-8 multibyte 输入

- **WHEN** `Md5.HashStringHex("café")`（5 bytes UTF-8）
- **THEN** 返回 `"7e54ec27d7c5e5a0e22f6571eb35eecd"`

### Requirement: `Md5.HashHex(byte[]) -> string`

#### Scenario: 输出为 32 个 lowercase hex 字符

- **WHEN** 任意输入
- **THEN** 长度 32 + 所有字符 ∈ `[0-9a-f]`

### Requirement: `HmacMd5.Compute(key, data) -> byte[16]`

#### Scenario: RFC 2202 §2 test case 1

- **WHEN** key=`0x0b * 16` (16 bytes 0x0b), data=`"Hi There"`
- **THEN** hex 输出 `"9294727a3638bb1c13f48ef8158bfc9d"`

#### Scenario: RFC 2202 §2 test case 7（key 80 bytes 0xaa）

- **WHEN** key=`0xaa * 80`, data=`"Test Using Larger Than Block-Size Key and Larger Than One Block-Size Data"`
- **THEN** hex 输出 `"6f630fad67cda0ee1fb1f562db3aa53e"`

#### Scenario: key 长于 64 byte 触发预 hash

- **WHEN** key.Length > 64
- **THEN** key 被替换为 `Md5.Hash(key)` 再用（RFC 2104 §2）

### Requirement: Md5 文档显式标 legacy

#### Scenario: Md5.z42 顶部注释含 "⚠️"

- **GIVEN** `src/libraries/z42.crypto/src/Md5.z42`
- **THEN** 文件 top doc 注释提到 "broken for collision-resistance" +
  列举可接受用例（Digest auth / ETag / git dumb proto / legacy interop）

## MODIFIED Requirements

无 — 纯新增类 + Hmac.z42 加 HmacMd5 类（不改 HmacSha*）。

## IR Mapping

无 — 纯 stdlib，无新 builtin / IR 指令。

## Pipeline Steps

- [ ] Lexer — N/A
- [ ] Parser / AST — N/A
- [ ] TypeChecker — N/A
- [ ] IR Codegen — N/A
- [ ] VM interp — N/A
