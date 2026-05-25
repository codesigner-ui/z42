# Tasks: add Std.Encoding.Base32 (RFC 4648 §6)

> 状态：🟢 已完成 | 创建：2026-05-25 | 归档：2026-05-25
> 类型：feat (new sibling class in existing namespace) | Spec 类型：minimal mode

**变更说明**：在 `Std.Encoding` 命名空间下新增 `Base32` 静态类，RFC 4648 §6
标准变体。字母表 = `A-Z 2-7`（32 字符）。5-byte 块 → 8-char 输出；padding `=`
按 RFC 表对齐。

**原因**：
- TOTP / HOTP 密钥（RFC 6238 / 4226）默认 Base32 编码（用户输入友好：无歧义字符 0/O、1/I/l 不在表里）
- Bitcoin / Lightning Network 部分协议使用
- Onion address (Tor v3) 也用 Base32

**API**：

```z42
namespace Std.Encoding;
public static class Base32 {
    public static string Encode(byte[] bytes);
    public static byte[] Decode(string s);
}
```

**Encode 行为**：
- 输入 0 byte → 输出 `""`
- 每 5 byte → 8 char；尾部 1/2/3/4 byte → 2/4/5/7 char + 6/4/3/1 个 `=`
- 字母表严格 RFC 4648 §6（uppercase only）

**Decode 行为**：
- 长度必须是 8 的倍数（含 padding）— 否则抛 `FormatException`
- padding 计数必须是 {0, 1, 3, 4, 6}（RFC 4648 §6 表 3）— 其他抛
- 大写严格：`a-z` 不接受（rejection FormatException）— 用户可先 `s.ToUpper()`
- 非字母表字符（如 `0` / `O` / `1` / `I`）抛 `FormatException`

**算法（位级 packing）**：
```
5 bytes (40 bits) packed into long; extract 8 × 5-bit groups → alpha char.
Tail bytes shift-left to align top; pad chars to fill 8-char block.
Decode reverses: collect 8 × 5-bit groups into 40-bit block; emit
(5 - pad_to_byte_map) bytes.
```

| 尾部 byte 数 | char 数 | `=` padding 数 |
|---|---|---|
| 1 | 2 | 6 |
| 2 | 4 | 4 |
| 3 | 5 | 3 |
| 4 | 7 | 1 |

**Out of scope（follow-up）**：
- Crockford Base32（不同字母表 `0-9A-HJKMNP-TV-Z`；去 I/L/O/U；用于 Stripe ID
  / ULID）— `encoding-future-crockford-base32`
- Lenient lowercase 输入（current strict uppercase 解码）— 用户 ToUpper() 可绕过
- Base32 hex 变体 RFC §7（`0-9A-V`）— 极低频；`encoding-future-base32-hex`

**文档影响**：
- `encoding.md`：`Base32 (RFC 4648 §6)` Deferred → ✅ landed，API 矩阵新增段；
  保留 `Base85 / ASCII85 / UTF-16 / streaming` Deferred 项
- `roadmap.md`：更新 Deferred Index 行（剥离 Base32）

## Tasks

- [x] 1.1 NEW `src/libraries/z42.encoding/src/Base32.z42` — Encode + Decode + decodeChar 私有
- [x] 2.1 NEW `tests/base32.z42` — 23 tests (RFC 4648 §10 encode + decode vectors, all-byte round-trip, TOTP reference key, error paths: length-not-mod-8 / padding=2 / padding=5 / non-alphabet char / lowercase, output-length-mod-8 invariant)
- [x] 3.1 `encoding.md`: flip Base32 Deferred → ✅ landed + 新增 `Std.Encoding.Base32` API 矩阵段 + 新增 `encoding-future-crockford-base32` / `encoding-future-base32-hex` Deferred 条目
- [x] 3.2 `roadmap.md` Deferred Index：strike Base32 + 注 "UTF-16 / streaming / Crockford / Base32-hex / Base85 仍延后"
- [x] 4.1 GREEN (encoding lib 23/23 new Base32 tests pass + 全 6 encoding 测试文件 100%) + archive + commit + push

## 备注

并行 session 的 z42.net HttpServer WIP 让 `scripts/build-stdlib.z42` 全 stdlib
预构建步骤失败（`HttpServer.z42(81,23): cannot assign HttpRequest to
HttpRequest`），间接导致 `test-stdlib.sh` 在 "Preparing tooling" 阶段挂掉。
本变更通过手动跑 z42c + z42-test-runner against encoding 子集验证（21 个 stdlib
member 包括 z42.encoding 编译成功，z42.net 是唯一 fail member）。GREEN
isolated to z42.encoding ✓；纯 z42 source-only change，与并行 Rust VM /
HttpServer WIP 解耦。
