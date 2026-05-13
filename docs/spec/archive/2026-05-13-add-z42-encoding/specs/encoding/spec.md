# Spec: z42.encoding — Hex / Base64 / Utf8

## ADDED Requirements

### Requirement: Hex 编码

#### Scenario: Encode 空数组
- **WHEN** `Hex.Encode(new byte[0])`
- **THEN** 返回 `""`

#### Scenario: Encode 单字节
- **WHEN** `Hex.Encode(new byte[] { 0xAB })`
- **THEN** 返回 `"ab"`（小写默认）

#### Scenario: Encode 多字节
- **WHEN** `Hex.Encode(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF })`
- **THEN** 返回 `"deadbeef"`

#### Scenario: EncodeUpper 变体
- **WHEN** `Hex.EncodeUpper(new byte[] { 0xDE, 0xAD })`
- **THEN** 返回 `"DEAD"`

#### Scenario: Decode 空字符串
- **WHEN** `Hex.Decode("")`
- **THEN** 返回 `new byte[0]`

#### Scenario: Decode 标准小写 / 大写 / 混写
- **WHEN** `Hex.Decode("deadbeef")` 或 `Hex.Decode("DEADBEEF")` 或 `Hex.Decode("DeAdBeEf")`
- **THEN** 三者都返回 `byte[] { 0xDE, 0xAD, 0xBE, 0xEF }`

#### Scenario: Decode 奇数长度抛错
- **WHEN** `Hex.Decode("abc")`
- **THEN** 抛 `Std.FormatException` 含 `"odd-length hex string"`

#### Scenario: Decode 非法字符抛错
- **WHEN** `Hex.Decode("ab cd")` 或 `Hex.Decode("xyz!")`
- **THEN** 抛 `Std.FormatException` 含 `"invalid hex character"`

#### Scenario: 往返
- **WHEN** `Hex.Decode(Hex.Encode(bytes))`
- **THEN** 等于 `bytes`（对任意 `byte[]`）

---

### Requirement: Base64 编码（RFC 4648 §4）

#### Scenario: 标准 test vectors（RFC 4648 §10）

| input (ASCII) | output |
|---|---|
| `""` | `""` |
| `"f"` | `"Zg=="` |
| `"fo"` | `"Zm8="` |
| `"foo"` | `"Zm9v"` |
| `"foob"` | `"Zm9vYg=="` |
| `"fooba"` | `"Zm9vYmE="` |
| `"foobar"` | `"Zm9vYmFy"` |

- **WHEN** `Base64.Encode(<bytes of "f">)`
- **THEN** 返回 `"Zg=="`
- （以此类推所有 7 个 case）

#### Scenario: Decode 标准 test vectors（与 Encode 反向）
- **WHEN** `Base64.Decode("Zm9vYmFy")`
- **THEN** 返回 `<bytes of "foobar">`

#### Scenario: Decode 含非法字符抛错
- **WHEN** `Base64.Decode("Zm9v$Ymar")` 或 `Base64.Decode("Zm9vYmFy*")`
- **THEN** 抛 `Std.FormatException` 含 `"invalid base64 character"`

#### Scenario: Decode 长度非 4 倍数（无 padding 时）抛错
- **WHEN** `Base64.Decode("Zm9vYg")`（缺末尾 `==`）
- **THEN** 抛 `Std.FormatException` 含 `"invalid base64 length"`

#### Scenario: Decode 内部 padding 抛错
- **WHEN** `Base64.Decode("Zm=v")`
- **THEN** 抛 `Std.FormatException`

#### Scenario: 往返
- **WHEN** `Base64.Decode(Base64.Encode(bytes))`
- **THEN** 等于 `bytes`（对任意 `byte[]`）

---

### Requirement: UTF-8 编解码

#### Scenario: 空字符串
- **WHEN** `Utf8.GetBytes("")` 与 `Utf8.GetString(new byte[0])`
- **THEN** 第一返回 `new byte[0]`；第二返回 `""`

#### Scenario: 纯 ASCII
- **WHEN** `Utf8.GetBytes("Hello")`
- **THEN** 返回 `byte[] { 72, 101, 108, 108, 111 }`

#### Scenario: 2 字节 UTF-8（拉丁扩展 / 希腊 / 西里尔 / ...）
- **WHEN** `Utf8.GetBytes("é")`（U+00E9）
- **THEN** 返回 `byte[] { 0xC3, 0xA9 }`

#### Scenario: 3 字节 UTF-8（中文 / 日文 / 韩文 BMP）
- **WHEN** `Utf8.GetBytes("中")`（U+4E2D）
- **THEN** 返回 `byte[] { 0xE4, 0xB8, 0xAD }`

#### Scenario: 4 字节 UTF-8（emoji / 补充平面）
- **WHEN** `Utf8.GetBytes("😀")`（U+1F600）
- **THEN** 返回 `byte[] { 0xF0, 0x9F, 0x98, 0x80 }`

#### Scenario: Decode 反向（4 个长度类别）
- **WHEN** `Utf8.GetString(<对应 byte 序列>)`
- **THEN** 返回原 string（对上述 4 个 case 分别验证）

#### Scenario: Decode 截断的多字节序列抛错
- **WHEN** `Utf8.GetString(new byte[] { 0xC3 })`（2-byte 开头但缺续）
- **THEN** 抛 `Std.FormatException` 含 `"truncated UTF-8 sequence"`

#### Scenario: Decode overlong / surrogate 抛错
- **WHEN** `Utf8.GetString(new byte[] { 0xED, 0xA0, 0x80 })`（U+D800 surrogate 的 overlong 编码）
- **THEN** 抛 `Std.FormatException` 含 `"invalid UTF-8 sequence"`

#### Scenario: 往返
- **WHEN** `Utf8.GetString(Utf8.GetBytes(s))`
- **THEN** 等于 `s`（对包含 ASCII + 中文 + emoji 的混合字符串）

---

## MODIFIED Requirements

无（纯新增包，无既有行为变更）。

---

## IR Mapping

无新 IR 指令。所有操作走现有 byte / int / char / string 算术 + 控制流。

## Pipeline Steps

- [ ] Lexer / Parser / AST：无变动
- [ ] TypeChecker：无变动（z42.encoding 类型通过 TSIG 跨包导入既有路径）
- [ ] IR Codegen：无变动
- [ ] VM interp：无变动
- [ ] VM JIT：无变动

## Testing Strategy

- **单元测试**（[Test] dogfood）：每类一个 `.z42` 文件
  - `hex.z42`：~10 case（空 / 单字节 / 多字节 / EncodeUpper / Decode 大小写 / 奇数长度 / 非法字符 / 往返）
  - `base64.z42`：~15 case（RFC 4648 §10 全部 7 vectors × 2 方向 + 非法字符 + 长度错误 + 往返）
  - `utf8.z42`：~12 case（空 / ASCII / 2-byte / 3-byte / 4-byte 各 encode + decode + 截断 + overlong + 混合往返）
- **跨包回归**：现有 stdlib 不依赖本包；新包加入后 build-stdlib 7 库全绿
- **GREEN**：./scripts/test-all.sh 通过（除 add-std-process WIP pre-existing 失败外）
