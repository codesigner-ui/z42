# Proposal: Triple-quoted raw string literal `"""..."""`

## Why

z42 当前只支持 `"..."`（带 `\n`/`\t`/`\x` 等转义解析）和 `$"..."`（同上 + `{expr}` 插值）。需要写"原始多行字符串"（heredoc / template）的场景被迫退化成：

```z42
string manifest = "[package]\n"
                + "name = \"" + name + "\"\n"
                + "version = \"" + ver + "\"\n"
                + "[contents]\n"
                + "libs = [" + libs + "]\n";
```

每个 `\n` 都要手动拼，每个 `"` 都要 `\"` 转义，模板越长越不可读。在 [scripts/_lib/package_helpers.sh](../../../../scripts/_lib/package_helpers.sh)（641 LOC）这种 heredoc-heavy 脚本里，光 `pkg_emit_manifest` 一个函数就有 5 段 bash heredoc，逐段移植成 z42 拼接字符串后体积会膨胀 2–3 倍，可读性下降到不可维护。

`language-overview.md:92-96` 早已展示三引号 raw 字面量作为目标语法：

```z42
string json = """
    {
      "name": "zora",
      "version": 1
    }
    """;
```

但 lexer 至今没实现，注释里写 "To add verbatim strings: ..."。本变更**实现 `"""..."""` 最小集**，让上面的语法真能编译。

不做会怎样：
- [scripts/_lib/](../../../../scripts/_lib/) 下 ~1500 LOC bash heredoc-heavy 工具脚本的 z42 移植被迫拼字符串，可读性塌方
- HTTP server / TOML writer / JSON writer 等 emit 模板的库代码维护成本高
- `language-overview.md` 文档与实现不一致（已展示语法 → 实际不可用），新用户踩坑

本变更**只实现 v0 最小子集**：单一固定 3-quote 分隔符、无转义解析、无 indent dedent、无插值。C# 11 高级特性（变长 quote 数、缩进剥离、`$"""..."""` 插值）一律 defer 给后续 spec。

## What Changes

1. **Lexer**：识别 `"""` 作为字符串开始，扫描直到下一个 `"""`，emit 新 token `RawStringLiteral`
2. **Parser**：新 nud 处理 `RawStringLiteral` token → `LitStrExpr`（剥 3+3 个 quote，**不**跑 `UnescapeString`）
3. **TokenKind**：新增 `RawStringLiteral`
4. **Grammar**：grammar.peg 加 raw string production
5. **docs**：`language-overview.md` 把现有占位升级成正式规范，新 design doc `docs/design/language/raw-string-literal.md` 沉淀决策
6. **examples + tests**：单元 + golden + 现有 1 个 examples/raw_string_basic.z42

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Lexer/TokenKind.cs` | MODIFY | 加 `RawStringLiteral` enum 值 |
| `src/compiler/z42.Syntax/Lexer/TokenDefs.cs` | MODIFY | `Display` 字典加 RawStringLiteral 显示名 |
| `src/compiler/z42.Syntax/Lexer/LexRules.cs` | MODIFY | `StringRule` 加 `IsRaw` bool 字段；`StringRules` 在 `"\""` 前插入 `"\"\"\""` 条目 |
| `src/compiler/z42.Syntax/Lexer/Lexer.cs` | MODIFY | `LexStringBody` 加 raw 分支：扫到下一个 `"""`，无 escape 处理 |
| `src/compiler/z42.Syntax/Parser/ExprParser.cs` | MODIFY | NudTable 加 `RawStringLiteral` 入口，`Text[3..^3]`，不调用 `UnescapeString` |
| `src/compiler/z42.Tests/LexerTests.cs` | MODIFY | 加 8 个 raw string 用例（单行 / 多行 / 内嵌 `"` / 内嵌 `""` / 内嵌 `\n` 原样 / 空 / 未闭合错误） |
| `src/compiler/z42.Tests/GoldenTests.cs` 或新 golden case | NEW | parser golden test 一个 |
| `src/compiler/z42.Syntax/Lexer/grammar.peg` | MODIFY | 加 raw string production；删除"To add"注释 |
| `docs/design/language/language-overview.md` | MODIFY | 把当前的非正式示例改成正式规范段（含 v0 限制说明） |
| `docs/design/language/raw-string-literal.md` | NEW | 完整设计 doc + Deferred 段（C# 11 高级特性） |
| `examples/raw_string_basic.z42` | NEW | 1 个示例文件 |
| `docs/roadmap.md` | MODIFY | 删除 `feedback_pragmatic_feature_adoption` 标识下"raw strings 缺"的 implicit deferred 标记（若有） |

**只读引用：**
- `src/compiler/z42.Syntax/Parser/ExprParser.Atoms.cs` — 参考 `UnescapeString` / `InterpolatedString` parse 风格
- `scripts/_lib/package_helpers.sh:418-530` — 验证 v0 raw string 形态足够 emit manifest.toml 风格 heredoc

## Out of Scope（明确推迟）

- **可变长 quote 数 `""""..."""""`**（C# 11 让用户用 `""""` 包含 `"""`）— 设计意义有限，等真有需求再加
- **Indent dedent**（C# 11 按 closing `"""` 缩进剥共同前缀空格）— 复杂、有争议、好几个 design 选择，独立 spec
- **`$"""..."""`插值**（raw string 套 `{expr}`）— 对 z42 当前用户场景非必须，独立 spec
- **`@"..."` verbatim**（C# 旧式单引号 raw）— 与 `"""..."""` 重复，pre-1.0 不引入备选
- **Single-quoted `'''...'''`**（Python 风）— 同上，统一用 `"""`

## Open Questions

- [ ] **首尾自动 trim newline？** Python `"""\nhi\n"""` = `"\nhi\n"`（保留），C# 11 `"""\nhi\n"""` = `"hi"`（自动剥首尾 newline 当作 visual aid）。v0 决策：**完全字面，不 trim**（最小行为，可由用户手动 `.TrimStart('\n')` 处理；自动 trim 是 indent dedent 的子集，与 dedent 一起做更内聚）
- [ ] **空 raw string `""""""`（6 个 quote 连写）合法？** v0 决策：**合法**，等同 `""`（empty string）。Lexer 见到 `"""` 后立刻又见 `"""` 即闭合 → empty token text。
- [ ] **CRLF newlines 是否标准化为 LF？** v0 决策：**字面保留**（如果源文件用 CRLF，raw string 里就是 CRLF）。统一规范化是 source-pipeline 级问题，不在 raw string 范畴。
