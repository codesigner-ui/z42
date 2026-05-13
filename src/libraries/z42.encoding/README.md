# z42.encoding

## 职责

byte ↔ text 编码三件套：Hex / Base64 / UTF-8。纯脚本实现（无 VM native）。

## 核心文件

| 文件 | 职责 |
|------|------|
| `src/Hex.z42` | `Std.Encoding.Hex` — Encode / EncodeUpper / Decode |
| `src/Base64.z42` | `Std.Encoding.Base64` — RFC 4648 §4 标准 Base64（含 `=` padding） |
| `src/Utf8.z42` | `Std.Encoding.Utf8` — GetBytes / GetString，严格校验 UTF-8 |

## 入口点

- `Hex.Encode(bytes)` / `Hex.EncodeUpper(bytes)` / `Hex.Decode(s)`
- `Base64.Encode(bytes)` / `Base64.Decode(s)`
- `Utf8.GetBytes(s)` / `Utf8.GetString(bytes)`

## 依赖关系

→ `z42.core`（byte / char / string / Exception / FormatException）

无 VM native；无 IR 改动。

## 错误处理

非法输入抛 `Std.FormatException`：
- Hex.Decode 奇数长度 / 非法字符
- Base64.Decode 非法字符 / 长度错误 / 内部 padding
- Utf8.GetString 截断 / overlong / surrogate / 超界 / 非法首字节

## 限制（v0）

- **不含 URL-safe Base64**（RFC 4648 §5：`-_` 表）— 留 follow-up
- **不含 UTF-16 / UTF-32 / Base32** — UTF-16 仅 Windows API 边界需要；其他低频
- **无 streaming API**（Encoder / Decoder 状态机）— P1
- **无 performance native** — 当前纯脚本；JIT 路径 byte loop 性能可接受
