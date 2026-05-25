# Tasks: add Std.Encoding.Base64Url (RFC 4648 §5)

> 状态：🟢 已完成 | 创建：2026-05-25 | 归档：2026-05-25
> 类型：feat (new sibling class in existing namespace) | Spec 类型：minimal mode

**变更说明**：在 `Std.Encoding` 命名空间下新增 `Base64Url` 静态类，提供 RFC 4648 §5
（URL- and Filename-safe Base64）的编/解码。字母表把 `+/` 替换成 `-_`，
默认输出无 padding（与 JWT / RFC 7515 Appendix C 对齐；RFC 4648 §3.2 允许）。

**原因**：现有 `Std.Encoding.Base64` 走 §4 标准变体，输出含 `+/=` 在 URL
query / cookies / filename 里需要 percent-escape。JWT / OAuth / S3 presigned URL
等都用 url-safe 变体；目前用户得自己 string-replace + 去 padding，啰嗦且 round-trip
容易写错。

**API**：

```z42
namespace Std.Encoding;
public static class Base64Url {
    public static string Encode(byte[] bytes);   // unpadded url-safe output
    public static byte[] Decode(string s);       // permissive: accepts padded or unpadded
}
```

**Encode 行为**：
- 输出始终无 `=` padding（移除尾部 `=`；与 JWT / Go `RawURLEncoding` / Python `base64.urlsafe_b64encode().rstrip(b'=')` 等价）
- 字母表用 `-` (62) / `_` (63) 替换标准的 `+` / `/`
- 实现策略：调用 `Base64.Encode` 拿到标准输出，再 char-translate + trim padding
  （比重复一份算法清晰；性能开销在 stdlib 用例中可忽略）

**Decode 行为**：
- 接受 padded 或 unpadded 输入（用户经常拿到的 url-safe 串两种都有）
- 严格拒绝标准字母表特有字符（`+` / `/`）— 出现即抛 `FormatException`，
  提示 "use Std.Encoding.Base64 for standard variant"
- 实现策略：char-translate (`-` → `+`, `_` → `/`)，按需 pad `=` 到 4 倍数，
  调用 `Base64.Decode`

**错误**：所有非法输入抛 `Std.FormatException`（与现有 `Base64` 一致）

**Out of scope（follow-up）**：
- `EncodeWithPadding(byte[])` — 极少数场景需要 padded URL-safe 输出；用户可
  自己 `.PadRight(((s.Length + 3) / 4) * 4, '=')`，先不加 API
- VM-native 加速（与 standard Base64 共享 deferred 条目）

**文档影响**：
- `docs/design/stdlib/encoding.md`：`URL-safe Base64` Deferred → ✅ landed，
  API 矩阵新增 `Std.Encoding.Base64Url` 子段
- `docs/roadmap.md` Deferred Index：移除 `URL-safe Base64 / Base32 / ...` 索引行 或
  改为只指 `Base32 / Base85 / ASCII85 / UTF-16 / Streaming` 剩余项

## Tasks

- [x] 1.1 NEW `src/libraries/z42.encoding/src/Base64Url.z42` — Encode + Decode
- [x] 2.1 NEW `tests/base64url.z42` — 20 tests (RFC 4648 §10 vectors + 字母表差异 + padded/unpadded round-trip + JWT round-trip + wrong-variant rejection + all-byte coverage)
- [x] 3.1 `encoding.md`: flip `URL-safe Base64` Deferred → ✅ landed + 新增 `Std.Encoding.Base64Url` API 矩阵段
- [x] 3.2 `roadmap.md` Deferred Index：strike URL-safe Base64 + 注 "Base32 / UTF-16 / streaming 仍延后"
- [x] 4.1 GREEN (stdlib scope: 131/131 files; 20/20 new base64url tests pass) + archive + commit + push

## 备注

GREEN 限定 `--scope=stdlib`。`--scope=full` 仍被并行 session 的 Rust VM
UDP / OOM WIP 阻塞（`udp_sockets` / `next_udp_socket_id` 字段添加未在所有
`VmCore::new` callsite 同步），与本变更（纯 z42 stdlib 源码，零 Rust 改动）
解耦。

z42 编译器 quirk 回顾：`byte[] _ = ...` 用 `_` 作为变量名解析失败 `E0201:
unexpected token ]`；改名为 `discard` 即过（无需 spec 变更，z42 语法当前
不允许 `_` 作为标识符）。
