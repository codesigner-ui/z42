# Design: z42.encoding MVP

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│ User code                                                       │
│   var enc = Base64.Encode(bytes);                               │
│   var dec = Base64.Decode("Zm9v");                              │
│   var u8  = Utf8.GetBytes("中文");                              │
└────────────────────┬────────────────────────────────────────────┘
                     │
                     ▼
┌─────────────────────────────────────────────────────────────────┐
│ z42.encoding (z42 .z42 sources — pure script)                   │
│   Std.Encoding.Hex      — char[16] table + bitwise              │
│   Std.Encoding.Base64   — char[64] table + bit-packing          │
│   Std.Encoding.Utf8     — code point → 1-4 byte encode/decode   │
└────────────────────┬────────────────────────────────────────────┘
                     │ uses
                     ▼
┌─────────────────────────────────────────────────────────────────┐
│ z42.core: byte[] / char[] / string / int / bitwise ops          │
│ (no VM native; no new IR instruction)                           │
└─────────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: 纯脚本 vs VM native

**问题**：Base64 / UTF-8 在 BCL / Rust std 都是 hand-optimized native；z42 该走脚本还是 native？

**选项**：
- A — VM native（`__b64_encode` 等）：性能好，但增加 corelib 表 + 跨语言锁定语义
- **B（选）— 纯脚本**：贴近 z42 stdlib "script-first" 原则；性能可接受（JIT 路径上 byte loop 接近 native）；后续 hot path 真出现性能问题再单独 spec 加 native

**决定**：B。Script-first 文化对齐 + 减少 corelib 注册压力 + 不引入跨语言 ABI。

### Decision 2: API 形状 — Static class vs 实例

C# / Java 风格 `Convert.ToBase64String(bytes)` 静态方法；Rust 的 `base64::encode` free function。

**选项**：
- A — 实例化 `var enc = new Base64Encoder(); enc.Encode(b)`：支持后续 streaming / options，但 v0 无需
- **B（选）— `Base64.Encode(bytes)` 静态方法**：与 C# `Convert` / Java `Base64` static 风格一致，与 z42 已有 `Math.Abs` / `Assert.Equal` 同款

**决定**：B。Streaming API 留 follow-up。

### Decision 3: 错误策略 — 抛 `Std.FormatException`

**问题**：非法输入（Hex 奇数长度、Base64 非法字符、UTF-8 截断）该如何报错？

**选项**：
- A — 返回 nullable / Result<T,E>：但 z42 当前无 Result（L3 deferred）
- B — 静默截断 / 替换字符：易 mask bug
- **C（选）— 抛 `Std.FormatException`**：与 C# `Convert.FromBase64String` / `Encoding.UTF8.GetString` 行为一致

**决定**：C。需 `Std.FormatException` 类。检查 z42.core 是否已有；若无则本 spec 同时加。

> **实施期 verify**：检查 `src/libraries/z42.core/src/Exceptions/` 是否有 FormatException。若无，本 spec 加最小骨架（继承 Exception）。

### Decision 4: 字符表为 z42 source const

Base64 alphabet / Hex digit 表用 z42 `const string` 或字段：
```z42
private static string B64_ALPHA = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
private static string HEX_LOWER = "0123456789abcdef";
```

VM 上 string CharAt 走 native `__str_char_at` 是 O(1)，可接受。

Decode 用反向查表 `byte[256]`，初始化时填入 0xFF（哨兵）+ 64 个有效索引。

### Decision 5: Base64 不含 URL-safe 变体（RFC 4648 §5）

v0 只做 §4 标准变体（`+/` 表，`=` padding）。URL-safe（`-_` 表，可省 padding）作为 deferred。

理由：现有用例 80% 是 §4；URL-safe 加一个 `Base64Url` 类即可，scope 独立、不影响 §4 实现。

### Decision 6: UTF-8 严格校验 vs 宽松解码

**问题**：z42 string 是 Rust `String`（已经 valid UTF-8）；用户 `byte[]` 来源可能不可信。

**选项**：
- A — 严格：拒绝 overlong / surrogate 编码 / 截断 / 非法首字节
- B — 宽松：替换为 `U+FFFD`
- **C（选）— 严格抛错**：与 C# 默认 `EncoderExceptionFallback` 一致；用户要宽松行为可后续加 `Utf8.TryGetString(bytes, out result)` overload

**决定**：C。

**实施细节**：
- 1-byte 序列：`0xxxxxxx`（U+0000 ~ U+007F）
- 2-byte 序列：`110xxxxx 10xxxxxx`（U+0080 ~ U+07FF；overlong 检查：解码值 ≥ 0x80）
- 3-byte 序列：`1110xxxx 10xxxxxx 10xxxxxx`（U+0800 ~ U+FFFF；overlong ≥ 0x800；surrogate U+D800~U+DFFF 拒）
- 4-byte 序列：`11110xxx 10xxxxxx 10xxxxxx 10xxxxxx`（U+10000 ~ U+10FFFF；overlong ≥ 0x10000；超界拒）
- 续字节缺失 / 首字节模式不匹配 → `Std.FormatException`

### Decision 7: 包依赖图

`z42.encoding → z42.core`（只需基础类型 + Exception）。不依赖 z42.io / z42.text / 其他。

### Decision 8: 测试位于 stdlib lib 路径

z42.encoding/tests/*.z42 走 `./scripts/test-stdlib.sh` 标准 [Test] dogfood 框架（与 z42.math / z42.text 同款）。

## Implementation Notes

### Hex 实现骨架

```z42
namespace Std.Encoding;

public static class Hex {
    private static string LOWER = "0123456789abcdef";
    private static string UPPER = "0123456789ABCDEF";

    public static string Encode(byte[] bytes) {
        return encodeWith(bytes, LOWER);
    }

    public static string EncodeUpper(byte[] bytes) {
        return encodeWith(bytes, UPPER);
    }

    private static string encodeWith(byte[] bytes, string alpha) {
        if (bytes.Length == 0) return "";
        char[] result = new char[bytes.Length * 2];
        int i = 0;
        while (i < bytes.Length) {
            int b = (int)bytes[i];
            result[i * 2]     = alpha.CharAt((b >> 4) & 0x0F);
            result[i * 2 + 1] = alpha.CharAt(b & 0x0F);
            i = i + 1;
        }
        return String.FromChars(result);
    }

    public static byte[] Decode(string hex) {
        if (hex.Length == 0) return new byte[0];
        if ((hex.Length & 1) != 0) {
            throw new FormatException("odd-length hex string");
        }
        byte[] result = new byte[hex.Length / 2];
        int i = 0;
        while (i < hex.Length / 2) {
            int hi = digitValue(hex.CharAt(i * 2));
            int lo = digitValue(hex.CharAt(i * 2 + 1));
            result[i] = (byte)((hi << 4) | lo);
            i = i + 1;
        }
        return result;
    }

    private static int digitValue(char c) {
        if (c >= '0' && c <= '9') return (int)c - (int)'0';
        if (c >= 'a' && c <= 'f') return (int)c - (int)'a' + 10;
        if (c >= 'A' && c <= 'F') return (int)c - (int)'A' + 10;
        throw new FormatException($"invalid hex character '{c}'");
    }
}
```

### Base64 实现骨架

标准编码：每 3 byte → 4 char，末尾 padding。

```z42
public static class Base64 {
    private static string ALPHA =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    public static string Encode(byte[] bytes) {
        if (bytes.Length == 0) return "";
        int outLen = ((bytes.Length + 2) / 3) * 4;
        char[] result = new char[outLen];
        int srcI = 0;
        int dstI = 0;
        while (srcI + 3 <= bytes.Length) {
            int b0 = (int)bytes[srcI];
            int b1 = (int)bytes[srcI + 1];
            int b2 = (int)bytes[srcI + 2];
            int triple = (b0 << 16) | (b1 << 8) | b2;
            result[dstI]     = ALPHA.CharAt((triple >> 18) & 0x3F);
            result[dstI + 1] = ALPHA.CharAt((triple >> 12) & 0x3F);
            result[dstI + 2] = ALPHA.CharAt((triple >> 6) & 0x3F);
            result[dstI + 3] = ALPHA.CharAt(triple & 0x3F);
            srcI = srcI + 3;
            dstI = dstI + 4;
        }
        // padding
        int rem = bytes.Length - srcI;
        if (rem == 1) {
            int b0 = (int)bytes[srcI];
            result[dstI]     = ALPHA.CharAt((b0 >> 2) & 0x3F);
            result[dstI + 1] = ALPHA.CharAt((b0 << 4) & 0x3F);
            result[dstI + 2] = '=';
            result[dstI + 3] = '=';
        } else if (rem == 2) {
            int b0 = (int)bytes[srcI];
            int b1 = (int)bytes[srcI + 1];
            result[dstI]     = ALPHA.CharAt((b0 >> 2) & 0x3F);
            result[dstI + 1] = ALPHA.CharAt(((b0 << 4) | (b1 >> 4)) & 0x3F);
            result[dstI + 2] = ALPHA.CharAt((b1 << 2) & 0x3F);
            result[dstI + 3] = '=';
        }
        return String.FromChars(result);
    }

    public static byte[] Decode(string s) {
        // ... build reverse lookup, validate, decode 4 char → 3 byte ...
    }
}
```

### UTF-8 实现骨架

```z42
public static class Utf8 {
    public static byte[] GetBytes(string s) {
        // 第一次扫描：算总字节数
        // 第二次扫描：填充
    }

    public static string GetString(byte[] bytes) {
        // 滚动 byte index；对每个起始字节判 1/2/3/4 字节序列；
        // 解码出 codepoint；overlong / surrogate / 截断校验；
        // 累积 char[]，最后 String.FromChars
    }
}
```

## Testing Strategy

详见 spec.md "Testing Strategy" 段。重点：
- Hex：往返 + 边界（空 / 奇数 / 大写 / 非法字符）
- Base64：**RFC 4648 §10 标准 test vectors** 双向验证 + 错误条件
- UTF-8：4 个长度类别（ASCII / 2 / 3 / 4 byte）双向 + 截断 / overlong / surrogate 错误
- 跨包：现有 stdlib 不依赖本包；新包加入后 build-stdlib 7 库全绿
- GREEN：./scripts/test-all.sh 通过（除 add-std-process WIP pre-existing 阻塞外）
