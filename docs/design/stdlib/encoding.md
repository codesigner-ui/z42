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

### `Std.Encoding.Utf8`

| 方法 | 签名 | 备注 |
|---|---|---|
| `GetBytes` | `(string) → byte[]` | 把 z42 char (Unicode scalar) 编码为 1-4 byte UTF-8 序列 |
| `GetString` | `(byte[]) → string` | 严格校验：overlong / surrogate / 截断 / 超界 抛 `Std.FormatException` |

## 决策记录

### Decision 1: 纯脚本实现，不引入 VM native

性能可接受（JIT 路径 byte loop 接近 native）；语义透明；不增加 corelib 表压力。若 hot path 出现性能问题再独立 spec 加 `__b64_encode` 等 native。

### Decision 2: 静态类 API（C# `Convert` / Java `Base64` 风格）

无需实例化；与 z42 已有 `Math.Abs` / `Assert.Equal` 风格一致。Streaming API（`Encoder` / `Decoder` 状态机）作为 P1 deferred。

### Decision 3: 错误统一抛 `Std.FormatException`

匹配 C# `Convert.FromBase64String` / `Encoding.UTF8.GetString` 默认行为。z42.core 已有 `FormatException` 类。

### Decision 4: 字符表内联在方法内，不用 static field

z42 当前 stdlib 跨包 static field 初始化时机有 quirk —— 当类被从外包首次触发时，static field 可能仍是 Null。本 spec 采用方法内 `string alpha = "..."` 局部变量 sidestep。Decode 用 if-else 范围检查（不依赖反向查表）。

> 跨包 static field 初始化的根本修复留作独立 follow-up（影响面更广，本 spec 不承担）。

### Decision 5: v0 不含 URL-safe Base64（RFC 4648 §5）

仅 §4 标准变体（`+/` 表 + `=` padding）。§5（`-_` 表，可选无 padding）独立 spec 落地（`Base64Url` 类）。

### Decision 6: UTF-8 严格校验

拒绝 overlong / surrogate (U+D800-U+DFFF) / 超界 (>U+10FFFF) / 截断 / 非法首字节。匹配 C# 默认 `EncoderExceptionFallback`。宽松解码（替换 U+FFFD）作为 follow-up overload。

## 实施期发现

- **静态字段跨包初始化 quirk**（Decision 4 触发）：z42.encoding 中的 `private static string LOWER = "..."` 在 Hex.Encode 被 z42.math.tests/* 调用时为 Null。验证方式：直接 z42vm 跑 zbc 看 stack trace（`__str_char_at: arg 0 expected string, got Null`）。
- 解决：方法内局部变量。

## Deferred / Future Work

### URL-safe Base64 (RFC 4648 §5)

- **来源**：本 spec v0 范围排除
- **触发原因**：现有用例 80% 是 §4；URL-safe 主要用于 JWT / URL query string
- **前置依赖**：无
- **触发条件**：用户实际场景出现 `-_` 表需求；或第三方包要求

### Base32 (RFC 4648 §6) / Base85 / ASCII85

- **来源**：本 spec v0 范围排除
- **触发原因**：极低频；Base32 用于 OTP 密钥，Base85 用于 Adobe / PostScript 历史格式
- **前置依赖**：无
- **触发条件**：实际用户需求

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
