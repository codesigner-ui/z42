# z42.encoding 设计

> 状态：v0 落地（2026-05-13 add-z42-encoding 归档）。

## 职责

byte ↔ text 编码三件套：`Hex` / `Base64` / `Utf8`。纯脚本实现，不依赖 VM native，与 stdlib 其他包 / add-std-process 等并行工作完全无冲突。

## 架构

```
┌────────────────────────────────┐
│ User code                      │
└──────────────┬─────────────────┘
               │
               ▼
┌────────────────────────────────┐
│ Std.Encoding                   │
│   Hex     — alpha + bitwise    │
│   Base64  — RFC 4648 §4        │
│   Utf8    — 1-4 byte codepoint │
└──────────────┬─────────────────┘
               │ 仅依赖
               ▼
┌────────────────────────────────┐
│ z42.core: byte / char / string │
│            FormatException     │
└────────────────────────────────┘
```

零 VM native，零 IR 改动，零 zbc 版本 bump。

## API 矩阵

### `Std.Encoding.Hex`

| 方法 | 签名 | 备注 |
|---|---|---|
| `Encode` | `(byte[]) → string` | 小写 hex |
| `EncodeUpper` | `(byte[]) → string` | 大写 hex |
| `Decode` | `(string) → byte[]` | 接受混合大小写；非法字符 / 奇数长度 抛 `Std.FormatException` |

### `Std.Encoding.Base64`

| 方法 | 签名 | 备注 |
|---|---|---|
| `Encode` | `(byte[]) → string` | RFC 4648 §4（`+/` 表，`=` padding） |
| `Decode` | `(string) → byte[]` | 严格 §4；非法字符 / 长度非 4 倍数 / 内部 padding 抛 `Std.FormatException` |

### `Std.Encoding.Base64Url`（2026-05-25 add-encoding-base64-url）

RFC 4648 §5 url-safe variant — `-_` 替换标准变体的 `+/`，输出默认无 `=` padding
（JWT / RFC 7515 Appendix C 要求）。Decode 接受 padded 与 unpadded 两种形态。

| 方法 | 签名 | 备注 |
|---|---|---|
| `Encode` | `(byte[]) → string` | RFC 4648 §5；输出 unpadded（无尾随 `=`） |
| `Decode` | `(string) → byte[]` | 接受 padded / unpadded；标准变体字符 `+/` 出现即抛 `Std.FormatException`（提示用 `Std.Encoding.Base64`） |

### `Std.Encoding.Base32`（2026-05-25 add-encoding-base32）

RFC 4648 §6 标准变体 — 字母表 `A-Z 2-7`（去 0/O/1/I/L 等歧义字符）。
TOTP / HOTP 密钥 / Bitcoin 部分协议 / Tor v3 onion address 都基于此。
严格大写、严格 padding count（`{0, 1, 3, 4, 6}` 之一）。

| 方法 | 签名 | 备注 |
|---|---|---|
| `Encode` | `(byte[]) → string` | 5-byte 输入 → 8-char 输出；尾部 padded 到 8 倍数 |
| `Decode` | `(string) → byte[]` | 长度 mod 8 == 0；padding 数 ∈ {0,1,3,4,6}；非法字符 / 小写均抛 `Std.FormatException` |

### `Std.Encoding.Base32Hex`（2026-05-25 add-encoding-base32-hex）

RFC 4648 §7 base32-hex — 字母表 `0-9A-V`（A=10..V=31）。同 §6 Base32 一样
的 5-byte→8-char + `=` padding；killer feature 是 **lex-order preservation**:
encoded string 的字典序 == decoded byte 的自然序，对 DNS NSEC3 等"排序后
还能反推顺序"的场景有用。

| 方法 | 签名 | 备注 |
|---|---|---|
| `Encode` | `(byte[]) → string` | 与标准 Base32 同 padding 规则 |
| `Decode` | `(string) → byte[]` | 严格大写；非 alphabet 字符抛 `Std.FormatException` |

### `Std.Encoding.Base32Crockford`（2026-05-25 add-encoding-base32-crockford）

Crockford Base32 — 字母表 `0123456789ABCDEFGHJKMNPQRSTVWXYZ`（去 I L O U
最大化降低歧义；与 RFC 4648 §6 完全不同字母表）。无 padding（输出长度 =
`ceil(input_bits / 5)`）。Decode 大小写不敏感；接受 `I` / `L` / `i` / `l`
→ `1` 与 `O` / `o` → `0` 别名（typo-tolerant）；`-` 分隔符在解码时丢弃。
用例：ULID / Stripe ID / 人类可读 ID。

| 方法 | 签名 | 备注 |
|---|---|---|
| `Encode` | `(byte[]) → string` | 无 padding；输出 `ceil(N*8/5)` chars |
| `Decode` | `(string) → byte[]` | 大小写不敏感；I/L→1, O→0；`-` 跳过；长度 mod 8 ∈ {0,2,4,5,7}；非法字符抛 `Std.FormatException` |

### `Std.Encoding.Utf8`

| 方法 | 签名 | 备注 |
|---|---|---|
| `GetBytes` | `(string) → byte[]` | 把 z42 char (Unicode scalar) 编码为 1-4 byte UTF-8 序列 |
| `GetString` | `(byte[]) → string` | 严格校验：overlong / surrogate / 截断 / 超界 抛 `Std.FormatException` |

### `Std.Encoding.Encoding`（2026-05-24 add-encoding-and-stream-text）

Instance-based facade — wraps a codec so Stream-level consumers
(`Std.IO.StreamReader` / `StreamWriter`) can accept "any encoding"
without committing to UTF-8 specifically. v0 ships only UTF-8; the
class is single-concrete because z42 has no `abstract` keyword yet.

| 方法 | 签名 | 备注 |
|---|---|---|
| `Encoding.GetUtf8` | `() → Encoding`（static）| Returns the canonical UTF-8 instance (singleton-cached) |
| `GetBytes` | `(string) → byte[]` | Delegates to underlying codec's encoder |
| `GetString` | `(byte[]) → string` | Delegates to underlying codec's decoder |
| `GetString` | `(byte[], int, int) → string` | Slice variant — decode `bytes[offset..offset+count]` |

Forward-compat path: when Latin-1 / UTF-16 land, extract an abstract
base (or interface) from `Encoding`, rename concrete class to
`Utf8Encoding`, and add `Encoding.GetLatin1()` etc. — call sites
already write `Encoding e = Encoding.GetUtf8()` and don't change.

## 决策记录

### Decision 1: 纯脚本实现，不引入 VM native

性能可接受（JIT 路径 byte loop 接近 native）；语义透明；不增加 corelib 表压力。若 hot path 出现性能问题再独立 spec 加 `__b64_encode` 等 native。

### Decision 2: 静态类 API（C# `Convert` / Java `Base64` 风格）

无需实例化；与 z42 已有 `Math.Abs` / `Assert.Equal` 风格一致。Streaming API（`Encoder` / `Decoder` 状态机）作为 P1 deferred。

### Decision 3: 错误统一抛 `Std.FormatException`

匹配 C# `Convert.FromBase64String` / `Encoding.UTF8.GetString` 默认行为。z42.core 已有 `FormatException` 类。

### Decision 4: 字符表用 private static field

`Hex.ALPHA_LOWER` / `Hex.ALPHA_UPPER` / `Base64.ALPHA` 直接声明为 `private static string`，所有 Encode 路径引用。Decode 用 if-else 范围检查（不依赖反向查表）。

> 历史：2026-05-13 落地时因 z42 跨包 `__static_init__` 函数名冲突 bug（同 namespace 不同 .z42 文件的初始化器互相覆盖）只能内联 `string alpha = "..."`。bug 在 2026-05-15 `dfcd1495 fix(compiler+vm): unique __static_init__ name per source file` 修复，static field 同 `cleanup-static-field-workarounds` spec 一并恢复。

### Decision 5: v0 不含 URL-safe Base64（RFC 4648 §5）

仅 §4 标准变体（`+/` 表 + `=` padding）。§5（`-_` 表，可选无 padding）独立 spec 落地（`Base64Url` 类）。

### Decision 6: UTF-8 严格校验

拒绝 overlong / surrogate (U+D800-U+DFFF) / 超界 (>U+10FFFF) / 截断 / 非法首字节。匹配 C# 默认 `EncoderExceptionFallback`。宽松解码（替换 U+FFFD）作为 follow-up overload。

## 实施期发现

- **静态字段跨包初始化 quirk**（Decision 4 触发）：z42.encoding 中的 `private static string LOWER = "..."` 在 Hex.Encode 被 z42.math.tests/* 调用时为 Null。验证方式：直接 z42vm 跑 zbc 看 stack trace（`__str_char_at: arg 0 expected string, got Null`）。
- 解决：方法内局部变量。

## Deferred / Future Work

### ~~URL-safe Base64 (RFC 4648 §5)~~ — **✅ 已落地 2026-05-25 (add-encoding-base64-url)**

Shipped: `Std.Encoding.Base64Url.Encode(byte[]) → string` (unpadded
url-safe output, JWT / RFC 7515 Appendix C 兼容) + `Decode(string) → byte[]`
(permissive: padded 或 unpadded 均接受；严格拒绝标准变体字符 `+` / `/`，
提示用 `Std.Encoding.Base64`). 实现策略：复用 `Base64` 核心算法 + char-table
翻译 + padding 处理（不重复 100 行 encoder/decoder）。15 tests cover RFC 4648
§10 vectors / 字母表差异 (`+/` → `-_`) / padded & unpadded round-trip / JWT
header round-trip / wrong-variant rejection / all-byte coverage。

### ~~Base32 (RFC 4648 §6)~~ — **✅ 已落地 2026-05-25 (add-encoding-base32)**

Shipped: `Std.Encoding.Base32.Encode(byte[]) → string` + `Decode(string) → byte[]`.
RFC 4648 §6 标准变体（字母表 `A-Z 2-7`，严格 padding count {0,1,3,4,6}）。
21 tests cover RFC 4648 §10 编/解 vectors, all-byte round-trip, TOTP 参考
key (RFC 6238 Appendix B 的 ASCII "12345678901234567890" → `GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ`),
非 8 倍数长度 / 非法 padding / 非法字符 / 小写大写各错误路径。

Crockford Base32 (`0-9A-HJKMNP-TV-Z`，去 I/L/O/U，用于 Stripe ID / ULID) 留
`encoding-future-crockford-base32`；RFC §7 hex 变体 (`0-9A-V`) 留
`encoding-future-base32-hex`。

### Base85 / ASCII85

- **来源**：本 spec v0 范围排除
- **触发原因**：极低频；Adobe / PostScript 历史格式
- **前置依赖**：无
- **触发条件**：实际用户需求

### ~~`encoding-future-crockford-base32`~~ — **✅ 已落地 2026-05-25 (add-encoding-base32-crockford)**

Shipped: `Std.Encoding.Base32Crockford.Encode(byte[]) → string` /
`Decode(string) → byte[]`. Alphabet `0123456789ABCDEFGHJKMNPQRSTVWXYZ`
(omits I L O U for maximum typo-tolerance). No padding on encode.
Decode case-insensitive, accepts `I` / `L` / `i` / `l` → `1` and
`O` / `o` → `0` aliasing per Crockford spec, drops `-` separators.
20 tests cover encode no-padding behaviour, alphabet-never-emits-ILOU
invariant, round-trips (basic / all 256 bytes / 5-byte chunks /
16-byte ULID), case-insensitivity, I/L/O aliasing, hyphen
acceptance, rejection of `U` (intentionally excluded), space rejection,
and length-mod-8 ∈ {1, 3, 6} rejection (impossible block sizes).

Check-digit extension (Crockford `*~$=U`) deferred — not part of v0.

### ~~`encoding-future-base32-hex`~~ — **✅ 已落地 2026-05-25 (add-encoding-base32-hex)**

Shipped: `Std.Encoding.Base32Hex.Encode(byte[]) → string` /
`Decode(string) → byte[]`. RFC 4648 §7 alphabet `0123456789ABCDEFGHIJKLMNOPQRSTUV`
(A=10..V=31). Identical bit-packing rules as standard Base32 — `=`
padding to multiple-of-8, same valid padding counts `{0, 1, 3, 4, 6}`.
Strict uppercase per RFC (lowercase rejected).

Killer property: the natural ordering of encoded strings preserves the
natural ordering of decoded byte values (because alphabet is in
ascending order). Useful for DNS NSEC3 (RFC 5155) hashed-owner-name
encoding and any application that wants encoded strings to sort.

16 tests cover: RFC 4648 §10 encode vectors (empty / f / fo / foo /
foob / fooba / foobar → CO/CPNG/CPNMU/CPNMUOG/CPNMUOJ1/CPNMUOJ1E8
with appropriate `=` padding), symmetric decode, **lex-order
preservation** (`enc(0x10) < enc(0x80)` byte-by-byte), all-byte
round-trip, length-not-mod-8 rejection, padding=2 rejection,
W-char (post-V) rejection, lowercase rejection, output-length-mod-8
invariant.

### UTF-16 / UTF-32 编解码

- **来源**：本 spec v0 范围排除
- **触发原因**：z42 string 内部 UTF-8 存储；UTF-16 仅 Windows API 边界需要
- **前置依赖**：z42 Windows native interop spec
- **触发条件**：Windows facade 实施 / 桌面 GUI 场景

### Streaming API（Encoder / Decoder 状态机）

- **来源**：本 spec v0 范围排除
- **触发原因**：v0 都是一次性 byte[] / string 转换；流式适合大文件 / 网络流
- **前置依赖**：无（纯脚本实现）
- **触发条件**：实际 IO pipeline 出现

### Performance 优化（VM native）

- **来源**：v0 全脚本
- **触发原因**：Hot path（如 HTTP body Base64 编解码）出现性能瓶颈
- **触发条件**：profile 显示编解码占比 > 5% 或具体 case 复现

### 跨包 static field 初始化的根本修复

- **来源**：本 spec 实施期 Decision 4 触发的 quirk
- **触发原因**：`private static string LOWER = "..."` 在被外包首次调用时为 Null；Hex 的 LOWER / Base64 的 ALPHA 都受影响
- **前置依赖**：调研 compiler / VM 中类初始化的触发时机（特别是 IrGen `__static_init__` 函数的跨包调用顺序）
- **触发条件**：影响面进一步扩散（其他 stdlib 包想用 static 字段）；或语言一致性需要

## 性能特征

JIT 路径上 byte loop 接近 native（每次 iteration 是 1-2 个 register op + CharAt 调用）。Interp 路径较慢但语义正确。

## 兼容性承诺

pre-1.0：API 形态可变，不保证 binary compat；语义保证 RFC 4648 § 4 / WHATWG UTF-8 标准对齐。
