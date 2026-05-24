# z42.json — JSON RFC 8259 reader / writer

> 落地版本：2026-05-15（add-z42-json，blocker fixed by fix-instance-method-binding-receiver-aware 同日）
> 包路径：`src/libraries/z42.json/`
> 命名空间：`Std.Json`（值类型） + `Std`（异常类）

## 职责

- Parse 任意 JSON 文本 → 值树（root 可以是任一 7 种 JSON 值类型）
- Serialise 值树 → JSON 文本（compact / pretty 两种模式）

与 [z42.toml](toml.md) 镜像设计，覆盖完整 RFC 8259 而不是 subset —— JSON 比 TOML 简单，没有 datetime / multiline / hex-int 等需要 defer 的特性。

## 架构

```
                            +----------------------+
        Std.Json.JsonValue  |  Public API          |
        ── (entry points)   |  Parse / Stringify /
        +-----------+        |  StringifyPretty /
        |           |        |  Of* / Is* / As* /
        v           v        |  Get / Set / ...
+----------------+  +----------------+
|  JsonParser    |  |  JsonWriter    |
|  tokenizer +   |  |  compact +     |
|  recursive     |  |  pretty modes  |
|  descent +     |  |                |
|  surrogate     |  |                |
|  pair handling |  |                |
+----------------+  +----------------+

  Std.JsonException  ← thrown by parser / writer / wrong-kind accessors
  (in Std, not Std.Json — for `: Exception` resolution)
```

## Stream overloads（2026-05-24 add-stream-overloads-to-format-parsers）

In addition to the string entry points, `JsonValue` exposes Stream
overloads so callers can pipe a `Std.IO.FileStream` /
`MemoryStream` / `ProcessOutputStream` / compressed stream straight
in or out (UTF-8):

| 方法 | 签名 |
|------|------|
| `JsonValue.ParseStream` | `(Std.IO.Stream) → JsonValue` — drain + decode via `StreamReader`; src not closed |
| `JsonValue.WriteTo` | `(Std.IO.Stream, JsonValue) → void` — compact JSON encoded via `StreamWriter`; dest not closed |
| `JsonValue.WriteToPretty` | `(Std.IO.Stream, JsonValue) → void` — pretty-printed variant |

Naming note: the Stream variant is **`ParseStream`** (not an overload
of `Parse`) because z42's overload resolution can't reliably
distinguish `Parse(string)` from `Parse(Stream)` at call sites; same
limitation applies across Toml / Yaml. Rename can be lifted once the
compiler handles type-distinct arity-1 overloads.

## 值表示（Discriminated Union）

同 `Std.Toml.TomlValue` 同款 discriminated-union（z42 无 ADT）：单 `_kind: int` + 多个 typed 槽位：

| Kind | 字段 |
|------|------|
| 0 Null   | (none) |
| 1 Bool   | `_bool: bool` |
| 2 Long   | `_long: long` |
| 3 Double | `_double: double` |
| 4 String | `_str: string` |
| 5 Array  | `_arrayItems: JsonValue[]` + `_arrayCount: int` |
| 6 Object | `_objectKeys: string[]` + `_objectValues: JsonValue[]` + `_objectCount: int` |

公开 API 用 `IsNull()` / `AsString()` 等谓词 + 解构方法。**不暴露 KIND_* 常量** —— 静态字段 read-path 当前返回 0 default（已知限制）。

### 关键设计决策

| Decision | 选项 | 决定 | 理由 |
|----------|------|------|------|
| 1. 值表示 | (a) discriminated union 单类 / (b) abstract base + 子类 | (a) | 无 ADT 时唯一可行，与 TomlValue / Stdio 同模式 |
| 2. 整数 / 浮点存储 | (a) 统一 double / (b) split long + double | (b) | i64 整数 > 2^53 在 double 中失精；与 TOML 同 |
| 3. 字段集合 | (a) `List<T>` / `Dictionary<K,V>` / (b) 原始 `T[]` + 计数 | (b) | z42 类字段不支持泛型类型参数 |
| 4. 异常类放置 | (a) `Std.Json.JsonException` / (b) `Std.JsonException` | (b) | 同 TomlException 模式 —— 让 `: Exception` 同 namespace 解析 |
| 5. 错误恢复 | (a) fail-fast / (b) error list | (a) | v0 简单；多错误聚合留 future |
| 6. AsDouble on Long | (a) 拒绝 / (b) 自动提升 | (b) | JSON 中 long ↔ double 自然，方便用户 |
| 7. Duplicate keys | (a) reject / (b) last-wins | (b) | 匹配 serde_json / Python json；RFC 8259 不指定 |
| 8. Integer overflow | (a) reject / (b) fallback to f64 | (b) | 匹配 serde_json lossy 行为 |
| 9. Empty input | (a) → null / (b) reject | (b) | RFC 8259 要求至少一个 value |
| 10. Trailing comma | (a) allow / (b) reject | (b) | RFC 8259 严格语法 |

## 支持的语法

全部 RFC 8259。包括：

- 7 种 value 类型
- 完整 string escape 集（`\b \f \n \r \t \" \\ \/ \uXXXX`）
- Surrogate pair（`\uD83D\uDE00` → 单 supplementary 字符）via z42 char 32-bit codepoint
- 数字：optional `-`，integer / fraction / exponent
- 嵌套任意深度

## 不支持（与 RFC 8259 一致或显式 Deferred）

### json-future-comments-json5
- **来源**：JSON5 扩展（comments / trailing commas / unquoted keys）
- **触发原因**：先做严格 RFC；JSON5 独立 spec
- **当前 workaround**：用 z42.toml 或预处理剥注释

### json-future-schema
- **来源**：JSON Schema 风约束
- **前置依赖**：lambda / 反射（L3+）

### json-future-jsonpath
- **来源**：JSONPath / JSONPointer 查询
- **触发原因**：独立工具，与 JSON parser 解耦
- **触发条件**：build-driver / debug protocol 场景实际需要时

### json-future-streaming
- **来源**：增量 reader / writer for huge JSON
- **触发原因**：v0 整文档加载够用；流式独立 spec
- **当前 workaround**：分块手处理

### json-future-order-preservation
- **来源**：保留原始 key 插入顺序 + 注释
- **触发原因**：v0 stringify 按插入顺序，但 round-trip 不保 comment
- **同 TOML**：与 toml-future-key-order-preservation 一起做

### json-future-nan-inf
- **来源**：JSON5 / 老 Python json 容忍 `NaN` / `Infinity`
- **触发原因**：RFC 8259 不允许；z42.json 严格遵守
- **当前 workaround**：null 或专用 sentinel string

## 与 z42.toml / 其他 stdlib 的交互

- **z42.toml**：两个包独立工作，无 dep 关系；用户可在同一 z42 程序中同时使用。Bug fixed in fix-instance-method-binding-receiver-aware（2026-05-15）—— 之前因为方法名冲突导致 dispatch 错位。
- **z42.text**：JsonWriter 用 `StringBuilder` 累积输出。
- **z42.core**：基础类型 + Exception 基类 + Std namespace。

## 与 build-driver 的关系

JSON 在 build-driver 不直接 critical（versions.toml 是 TOML），但下游使用场景：
- `cargo metadata --format-version=1` 输出 JSON
- LSP / DAP debug protocol JSON-RPC
- 任何 REST API call

完整 build-driver 路线见 [roadmap.md](roadmap.md) Deferred Backlog Index → "z42 build-driver prerequisites"。
