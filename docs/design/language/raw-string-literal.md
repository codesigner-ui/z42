# Raw String Literal `"""..."""`

> 落地：2026-05-26 by `add-raw-string-literal`。
> 用户视角概览见 [language-overview.md §3.4](language-overview.md)；本文档面向实现者 + 边界场景作者。

## 设计目标

让用户写"原样的多行文本"时不需要 `+ "\n" +` 拼接 + `\"` 转义 — 解锁 heredoc 风格脚本（TOML / JSON / Markdown 模板 emit）的 z42 自托管写法。

## 语法

```ebnf
RAW_STRING = "\"\"\"" (!"\"\"\"" .)* "\"\"\""
```

- 开始/结束分隔符：固定 3 个 `"`
- 内容：任意 UTF-8 字节，除连续 3 个 `"`（即首次出现 `"""` 即闭合）
- 不解析任何转义序列（`\n` / `\t` / `\\` / `\"` / `\uXXXX` 全部字面保留）
- 换行符（LF / CRLF）字面保留，不规范化

## 语义

| 源代码 | 字面值 (LitStrExpr.Value) |
|--------|--------------------------|
| `"""hello"""` | `hello` |
| `"""""""` 注：6 个 `"` | `` (empty string) |
| `"""he said "hi"."""` | `he said "hi".` |
| `"""multi\nline"""`  | `multi\nline`（11 个字节，含 `\` 和 `n` 字符）|
| `"""<LF>x<LF>y<LF>"""` | `<LF>x<LF>y<LF>`（每个 LF 保留）|

> 注：表中 `<LF>` 是真实 0x0A 字节；表里特殊符号写法是为了可读，源代码就是字面的换行。

## Pipeline

| 阶段 | 实现位置 | 行为 |
|------|---------|------|
| Lexer | [`Lexer/Lexer.cs::LexStringBody`](../../../src/compiler/z42.Syntax/Lexer/Lexer.cs) | `IsRaw` 分支：找 `"""` 闭合，扫到 EOF 未找到则 throw `ParseException` |
| Token | [`Lexer/TokenKind.cs`](../../../src/compiler/z42.Syntax/Lexer/TokenKind.cs) | 新 `TokenKind.RawStringLiteral` |
| Lex 规则 | [`Lexer/LexRules.cs::StringRules`](../../../src/compiler/z42.Syntax/Lexer/LexRules.cs) | 列首位 `("\"\"\"", RawStringLiteral, IsRaw: true)` — 必须在 `"\""` 之前以保证最长前缀优先 |
| Parser | [`Parser/ExprParser.cs::s_nudTable`](../../../src/compiler/z42.Syntax/Parser/ExprParser.cs) | `t.Text[3..^3]` 剥分隔符，**不**调用 `UnescapeString` |
| AST | `LitStrExpr`（已存在）| 与普通 string 完全共用，TypeChecker / IR / VM 无任何变化 |

## 关键决策（来自原 spec design.md）

1. **独立 TokenKind** 而非 flag — 与既有 `StringLiteral` / `InterpolatedStringLiteral` 风格一致；parser 分发无运行时分支
2. **最长前缀优先** — `StringRules` 把 `"""` 放在 `"` 之前；first-match 算法靠条目顺序保证
3. **未闭合显式 throw** — 与常规 string "silent best-effort" 行为故意不一致；raw string 用户更容易少写一个 `"` 闭合，显式错误更友好
4. **不解析转义** — 字面就是字面；想要 `\n` 解码用普通 `"\n"`
5. **不 auto-trim leading/trailing newline** — 与 indent dedent 是同一组关注点；一起做更内聚（v0 推迟）

## Deferred / Future Work

按 ROI 大致排序：

### `raw-string-future-indent-dedent`

- **来源**：C# 11 / Swift / Kotlin `"""` 都自动剥共同前缀缩进
- **触发原因**：让 raw string 在缩进的代码块里"对齐缩进 + 自动去缩进"，写起来更优雅
- **前置依赖**：本变更 v0 落地后用户反馈 / 实际脚本移植反馈
- **触发条件**：≥3 个独立调用方手动 `.TrimIndent()` / `.Replace("\n    ", "\n")` 时合并
- **当前 workaround**：写 raw string 时左对齐到列 0，或后处理调用 `.Trim()`

### `raw-string-future-interpolation`

- **来源**：C# 11 `$"""..."""` 在 raw string 里嵌 `{expr}`
- **触发原因**：raw 字面里如果想插值（如 emit 模板含变量），目前必须 `+` 拼或两段
- **触发条件**：脚本移植中出现"raw heredoc + 少量变量插值"的 pattern ≥ 3 次
- **当前 workaround**：拆成 `"""prefix"""+ var + """suffix"""` 三段

### `raw-string-future-variable-quote-count`

- **来源**：C# 11 允许 `""""..."""""`（任意 ≥3 的 quote 数，闭合数对应）使 raw 内容可含 `"""`
- **触发原因**：v0 内容不可含 3 个连续 `"`；如果用户真要 emit 含 `"""` 的文本（极少见），现在做不到
- **触发条件**：实际 bug 报告
- **当前 workaround**：把这 3 个 `"` 拆成两段 raw string 用 `+` 拼

### `raw-string-future-auto-trim-edge-newlines`

- **来源**：C# 11 `"""\nhi\n"""` = `"hi"`（auto 剥首尾 newline 当作 visual aid）
- **触发原因**：写多行 raw 时第一行常想要"内容从下行开始"，不带头部 newline
- **触发条件**：与 indent dedent 一起 (二者通常一起做)
- **当前 workaround**：调用方 `.Trim('\n')`

### `raw-string-future-single-quote-variant`

- **来源**：Python 同时支持 `"""..."""` 和 `'''...'''`
- **触发原因**：交替使用便于在 quote 较密集时减少 escape
- **触发条件**：暂无；z42 现在统一 `"`，不引入备选
- **决定**：**永久不做**（保持单一形式）

## 与现有特性的关系

| 特性 | 关系 |
|------|------|
| `"..."` 普通 string | 完全正交；raw 不替代 |
| `$"..."` 插值 string | v0 raw 不支持插值前缀；想要插值用普通 `$"..."` |
| `@"..."` C# verbatim | **不引入**；`"""..."""` 覆盖同样用例 |
| `'...'` char literal | char 不影响 |
| `Std.Text.StringBuilder` | raw string 作为 builder 入参完全可用 |
