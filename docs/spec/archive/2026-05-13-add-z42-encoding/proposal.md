# Proposal: Add z42.encoding Standard Library Package

## Why

z42 当前没有任何 byte ↔ text 编码 API。任何要做的事情都被堵：
- 网络协议解析（HTTP header / cookie / WebSocket frame）
- 配置文件 / FFI marshaling（base64 嵌入二进制）
- Hash / 加密输出（输出 hex）
- Cross-language data exchange（UTF-8 byte boundary）

roadmap M7 stdlib P0 列表 #4。纯脚本实现，**不依赖任何未实现语言能力**，可立刻落地。与并行进行的 `add-std-process` 完全无冲突（不改 corelib / 不改 z42.io / 不引入 native）。

刚落地的 `fix-numeric-cast-lowering` (2026-05-13) 直接受益：Base64 / Hex / UTF-8 大量使用 `byte → int` / `int → byte` 显式转换，没有 cast lowering 之前不可行。

## What Changes

新增 `z42.encoding` 包（L1 层），3 个静态类：

1. **`Std.Encoding.Hex`** — Hex 字符串编解码
2. **`Std.Encoding.Base64`** — RFC 4648 §4 标准 Base64（不含 URL-safe §5 变体）
3. **`Std.Encoding.Utf8`** — z42 string ↔ UTF-8 byte[] 双向转换

**纯脚本实现**：底层只用现有 `byte[]` / `char[]` / `string` + bitwise ops + 新的 numeric cast。无 VM 原生新增，无 zbc 版本 bump。

无 namespace 冲突（`Std.Encoding.*` 当前未占用）。

## Scope（允许改动的文件）

**NEW**
| 文件 | 说明 |
|---|---|
| `src/libraries/z42.encoding/z42.encoding.z42.toml` | 包 manifest |
| `src/libraries/z42.encoding/README.md` | 包 README |
| `src/libraries/z42.encoding/src/Hex.z42` | `Std.Encoding.Hex` Encode / Decode + `EncodeUpper` 变体 |
| `src/libraries/z42.encoding/src/Base64.z42` | `Std.Encoding.Base64` Encode / Decode（RFC 4648 §4，含 `=` padding） |
| `src/libraries/z42.encoding/src/Utf8.z42` | `Std.Encoding.Utf8` GetBytes(string) / GetString(byte[]) |
| `src/libraries/z42.encoding/tests/hex.z42` | Hex 单元测试 |
| `src/libraries/z42.encoding/tests/base64.z42` | Base64 单元测试（含 RFC 4648 §10 test vectors） |
| `src/libraries/z42.encoding/tests/utf8.z42` | UTF-8 测试（ASCII / 多字节 / 中文 / emoji surrogate-pair） |
| `docs/design/stdlib/encoding.md` | 包设计文档（API 矩阵 + 决策记录 + Deferred 段）|

**MODIFY**
| 文件 | 说明 |
|---|---|
| `src/libraries/z42.workspace.toml` | `default-members` 加 `z42.encoding` |
| `scripts/build-stdlib.sh` | `LIBS=(...)` 数组加 `z42.encoding`（影响产物校验列表 + flat view 复制）|
| `scripts/build-stdlib.sh` | namespace index `index.json` heredoc 加 `Std.Encoding → z42.encoding.zpkg` |
| `src/libraries/README.md` | 包列表加 z42.encoding 行 |
| `docs/design/stdlib/roadmap.md` | P0 表移除 z42.encoding 行；如需 deferred 项加 Backlog Index |
| `docs/design/stdlib/organization.md` | 现状包列表加 z42.encoding |

**只读引用**
- `src/libraries/z42.core/src/String.z42` — 理解 `CharAt(int)` / `FromChars(char[])` / `Length` 接口
- `src/libraries/z42.math/src/Math.z42` — script-first 风格参考

## Out of Scope（v0 不做，留 follow-up）

- **URL-safe Base64**（RFC 4648 §5：`-` 代替 `+`，`_` 代替 `/`，可选无 padding）
- **Base32**（RFC 4648 §6） / **Base85 / ASCII85**
- **UTF-16 / UTF-32**：z42 字符串本身 UTF-8 存储；UTF-16 仅 Windows API 边界需要
- **Streaming API**：当前所有 API 一次性 byte[] → string 或 反向；流式（`Encoder` / `Decoder` 状态机）留 P1
- **Encoding error handling 策略选项**（C# `EncoderFallback`）：v0 直接抛 `Std.FormatException`
- **Performance optimization**：Base64 / UTF-8 当前每 byte 做寄存器 ops；future 可加 VM 原生 `__b64_encode` / `__b64_decode` / `__utf8_*` 加速

均记入 `docs/design/stdlib/encoding.md` Deferred 段 + `docs/roadmap.md` Deferred Backlog Index。

## Open Questions

无（设计决策见 design.md）。
