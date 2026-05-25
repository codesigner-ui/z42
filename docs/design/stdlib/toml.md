# z42.toml — TOML 1.0 subset reader / writer

> 落地版本：2026-05-14（add-z42-toml）
> 包路径：`src/libraries/z42.toml/`
> 命名空间：`Std.Toml`（值类型）+ `Std`（异常类）
> 设计驱动：[stdlib roadmap](roadmap.md) → "z42 build-driver prerequisites"

## 职责

- Parse TOML 文本 → 值树（`TomlValue` 根表）
- Serialise 值树 → canonical TOML 文本

直接服务于 **z42 build-driver 自举**：`scripts/_lib/versions.sh` 等当前
shell out 到 `python3 -c 'import tomllib; ...'` 的代码可以迁移成纯 z42
脚本读 `versions.toml` / `*.z42.toml`。

## 架构

```
                            +---------------+
        Std.Toml.TomlValue  |  Public API   |
        ── (entry points)   |  Parse, Stringify, Of*, Is*, As*, Get/Set
        +-----------+
        |           |
        v           v
+----------------+  +----------------+
|  TomlParser    |  |  TomlWriter    |
|  tokenizer +   |  |  canonical     |
|  recursive     |  |  emit          |
|  descent       |  |                |
+----------------+  +----------------+

  Std.TomlException  ← thrown by parser / writer / wrong-kind accessors
  (in Std namespace, not Std.Toml — for `: Exception` resolution)
```

## Stream overloads（2026-05-24 add-stream-overloads-to-format-parsers）

| 方法 | 签名 |
|------|------|
| `TomlValue.ParseStream` | `(Std.IO.Stream) → TomlValue` — UTF-8 drain + decode; src not closed |
| `TomlValue.WriteTo` | `(Std.IO.Stream, TomlValue) → void` — canonical TOML, UTF-8; dest not closed; root must be a Table |

See [`json.md` Stream overloads](json.md#stream-overloads2026-05-24-add-stream-overloads-to-format-parsers)
for the rationale on the `ParseStream` naming.

## 值表示（Discriminated Union）

z42 没有 ADT / sum type，用单 `_kind: int` + 多个 typed 槽位：

| Kind | 字段 |
|------|------|
| 0 String | `_str: string` |
| 1 Long   | `_long: long` |
| 2 Double | `_double: double` |
| 3 Bool   | `_bool: bool` |
| 4 Array  | `_arrayItems: TomlValue[]` + `_arrayCount: int` |
| 5 Table  | `_tableKeys: string[]` + `_tableValues: TomlValue[]` + `_tableCount: int` |

公开 API 用 `IsString()` / `AsString()` 等谓词 + 解构方法。**不暴露
KIND_* 常量** —— 静态字段 read-path 当前返回 0 default（已知限制），
谓词方法绕开此限制。

### 关键设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. 值类型表示 | (a) discriminated union 单类 / (b) abstract base + 多个子类 | (a) | 单类简化 `Get` chain，z42 ADT 未就绪时唯一可行方案，与 Stdio.z42 同模式 |
| 2. 整数 / 浮点存储 | (a) 统一 double / (b) split long + double | (b) | TOML i64 整数高于 2^53 在 double 中会失精；分开存避免精度丢失 |
| 3. 字段集合类型 | (a) `List<T>` / `Dictionary<K,V>` / (b) 原始 `T[]` + 计数 | (b) | z42 类字段的泛型类型参数当前被 parser 静默丢弃；用 Process.z42 同款原始数组 + 计数 workaround |
| 4. 异常类放置 | (a) `Std.Toml.TomlException` / (b) `Std.TomlException` | (b) | 放 `Std` 让 `: Exception` 通过同 namespace lookup 正确解析（避免 `Std.Toml.Exception` undefined function bug） |
| 5. 错误恢复 | (a) fail-fast / (b) error list | (a) | v0 简单；error list 需要先设计 `ErrorList` 收集机制（独立 spec） |
| 6. AoT 实现 | (a) tokenizer 区分 `[[` / `[` / (b) parser context 判断 | (a) | tokenizer 是位置敏感的，分流简单 |
| 7. 重复 key | (a) reject / (b) last-wins | (a) | 匹配 TOML 1.0 spec "Keys" 段；catch bugs early |
| 8. Stringify 顺序 | (a) sorted / (b) preserve insertion | (a) | v0 不保留 comment / order；canonical 输出便于 round-trip 测试 |

## 支持的语法（TOML 1.0 子集）

| 语法 | v0 | 说明 |
|------|----|------|
| `key = "value"` | ✅ |  |
| `key = 42` | ✅ | i64 via `long.Parse` |
| `key = 3.14` / `1.5e10` | ✅ |  |
| `key = true` / `false` | ✅ |  |
| `key = "a"` `key2 = 'b'` (basic / literal strings) | ✅ |  |
| `\n` `\t` `\r` `\"` `\\` `\u00E9` `\U0001F600` escapes | ✅ | unicode within BMP via `(char)v` cast |
| `[section]` / `[a.b.c]` | ✅ |  |
| `a.b = 1` (dotted key) | ✅ |  |
| `xs = [1, 2, 3]` / `[[1, 2], [3, 4]]` (arrays, nested) | ✅ |  |
| `inline = { k = v }` (inline table) | ✅ |  |
| `[[exe]]` (array of tables) | ✅ |  |
| `# comment` (line / trailing) | ✅ |  |
| `nan` / `inf` / `+inf` / `-inf` 浮点字面量 | ✅ |  |

## 不支持的语法（Deferred）

### toml-future-datetime
- **来源**：TOML 1.0 spec — 4 datetime types
- **触发原因**：z42 `DateTime` 当前只是 Unix-ms（无日历分解），TOML 需要
  完整 RFC 3339 / local-date / local-time 表示
- **前置依赖**：z42.time 日历分解 + 时区处理
- **触发条件**：build-driver / 用户配置真要用 datetime 字段时

### toml-future-multiline-string
- **来源**：TOML 1.0 spec — `"""…"""` 和 `'''…'''`
- **触发原因**：配置文件少用；先做高频语法
- **当前 workaround**：用 `\n` 转义在单行 basic string 中

### ~~toml-future-non-decimal-int~~ — **✅ 已落地 2026-05-25 (add-toml-numeric-bases-and-underscores)**

Shipped: TOML 1.0 §integer non-decimal prefixes `0x` (hex, A-F /
a-f), `0o` (octal, 0-7), `0b` (binary, 0-1). No leading sign on
non-decimal literals (per spec). Manual base parse (since `long.Parse`
only handles decimal). Same change ships underscore separators.

### ~~toml-future-underscore-separator~~ — **✅ 已落地 2026-05-25 (add-toml-numeric-bases-and-underscores)**

Shipped: TOML 1.0 underscore digit separators (`1_000_000` /
`0xDEAD_BEEF` / `0b1010_0101`). Validation per spec: `_` must be
between two digits (no leading / trailing / consecutive `_`).
`StripUnderscores` helper produces the clean literal for `long.Parse`
/ `double.Parse`. Works in decimal int, float mantissa, exponent
mantissa, and all three non-decimal bases.

24 tests cover both features: hex/oct/bin upper/lower/mixed case,
i64-max hex, file-mode octal, underscores in each base, underscores
in float mantissa, in arrays, validation rejection (leading /
trailing / double underscore in each base), plain decimal preserved.

### toml-future-key-order-preservation
- **来源**：dev workflow（git diff 友好）
- **触发原因**：v0 用 `Dictionary` 风 parallel arrays，不保留插入顺序
- **触发条件**：用户反馈 round-trip 改变文件结构

### toml-future-comment-preservation
- **来源**：需要 Tomlyn-style "DOM with trivia"
- **触发原因**：复杂，需要重新设计 AST 节点

### toml-future-stringify-formatting
- **来源**：pretty-print, custom indent, custom array layout
- **当前**：单 canonical 输出
- **触发条件**：用户场景需要可控输出格式

### toml-future-schema-validation
- **来源**：JSON Schema 风约束
- **前置依赖**：反射或 lambda（L3+）

## 与 build-driver 的交接

未来 `scripts/_lib/versions.sh` 重写为 z42：

```z42
using Std.Toml;
using Std.IO;

var manifest = TomlValue.Parse(File.ReadAllText("versions.toml"));
string rustVer = manifest.Get("build").Get("rust").AsString();
// ... 调用 cargo build --release ... via Std.IO.Process
```

完整 build-driver 路线见 [roadmap.md](roadmap.md) Deferred Backlog Index
→ "z42 build-driver prerequisites"。
