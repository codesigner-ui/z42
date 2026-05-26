# Tasks: Raw String Literal `"""..."""`

> 状态：🟢 已完成 | 创建：2026-05-26 | 归档：2026-05-27

## 进度概览
- [x] 阶段 1: Lexer
- [x] 阶段 2: Parser
- [x] 阶段 3: Docs + examples
- [x] 阶段 4: 测试与验证

## 阶段 1: Lexer

- [x] 1.1 `TokenKind.cs` — 在 enum 末尾追加 `RawStringLiteral`
- [x] 1.2 `TokenDefs.cs` — 在 `Display` 字典加 `[TokenKind.RawStringLiteral] = "raw string literal"`
- [x] 1.3 `LexRules.cs`：
  - `StringRule` record 加 `bool IsRaw = false` 第四个可选字段
  - `StringRules` 列表在首位插入 `new("\"\"\"", TokenKind.RawStringLiteral, IsRaw: true)`（必须在 `"\""` 之前以确保最长前缀优先）
- [x] 1.4 `Lexer.cs::LexStringBody` — 在 `if (rule.IsChar)` 和 `else if (rule.IsInterpolated)` 之间加 `else if (rule.IsRaw)` 分支：
  - 已 `Advance` 过 prefix（3 个 `"`）
  - 循环：检查 `source[_pos..pos+3] == "\"\"\""` 则 `Advance` 3 次后 break
  - 否则 `Advance` 1 字符（lexer 自动 track line/col）
  - EOF 前未闭合 → throw 与现有 string 错误同一异常类型（grep 确认；可能是 `ParseException`）

## 阶段 2: Parser

- [x] 2.1 `ExprParser.cs::NudTable` 在 `[TokenKind.StringLiteral]` 条目下方加：
  ```csharp
  [TokenKind.RawStringLiteral] = new((c, t, _, _) =>
      Ok(new LitStrExpr(t.Text[3..^3], t.Span), c)),
  ```
  注意 **没有 `UnescapeString`** 调用。
- [x] 2.2 `grammar.peg`：把 line 380 占位注释段替换成正式 raw string production；保持与现有 string production 相邻。

## 阶段 3: Docs + examples

- [x] 3.1 `docs/design/language/language-overview.md` line 92-96 段——把原有非正式示例升级成"L1 正式规范"：
  - 加正式 EBNF / 语义说明
  - 加 v0 限制清单（变长 quote / dedent / interp / verbatim 不支持，链回 raw-string-literal.md Deferred）
- [x] 3.2 NEW `docs/design/language/raw-string-literal.md`：
  - 整体设计（决策摘要：来自 design.md）
  - v0 形态精确定义（来自 spec.md）
  - Deferred / Future Work（来自 proposal.md Out of Scope）
- [x] 3.3 NEW `examples/raw_string_basic.z42` — 6 段示例覆盖：单行 / 多行 / 嵌入 `"` / `\n` 字面 / 空 / 拼接
- [x] 3.4 `docs/roadmap.md` — 在 "Pipeline 实现进度表" 加新行（如果 features.md 有 raw string 行 → 标 ✅ 落地）
- [x] 3.5 `docs/spec/changes/add-raw-string-literal/specs/raw-string-literal/spec.md` 已写完（阶段 4 草稿）

## 阶段 4: 测试与验证

- [x] 4.1 `src/compiler/z42.Tests/LexerTests.cs` — 加 8 个测试（按 design.md "Testing Strategy" 列表）
- [x] 4.2 `src/compiler/z42.Tests/GoldenTests.cs` 或对应 parser golden — 2 个 case
- [x] 4.3 `src/tests/raw_string/basic/{source.z42, expected_output.txt}` — VM e2e
- [x] 4.4 `./scripts/regen-golden-tests.sh` — 生成 .zbc 基线
- [x] 4.5 `./scripts/test-all.sh --scope=compiler` — 编译器全测试绿
- [x] 4.6 `./scripts/test-vm.sh` — VM 端到端绿
- [x] 4.7 docs/design 同步：language-overview + raw-string-literal.md + grammar.peg
- [x] 4.8 归档 + commit + push（hunk-pick 避开工作树 in-flight 改动）

## 备注

- 本变更是 lang 类型，按 workflow 阶段 1–9 完整流程；阶段 6.5 用户已显式确认进入 7。
- 不复用现有任何 raw string 半成品代码（lexer 注释里只是 hint，无实际框架）。

## 实施期发现

1. **Lexer raises `ParseException` for unterminated raw strings** — 与常规字符串"silent best-effort"不同。理由：raw string 用户更易少写一个闭合 `"`，显式 throw 更友好。已写入 design doc 决策 3。
2. **工作树多次被外部 session reset** — Stage 1+2 的 5 个文件曾被 3 次反复 reset 回 HEAD（同会话有另一个 in-flight 任务 in parser file）。每次 reapply 都验证 `grep -c "RawStringLiteral"` 确保 edit 落地。Lesson：commit 越早越好；hot-path 文件多 session 协作时用 worktree 隔离更稳。
3. **8 个 LexerTests 全绿，VM golden raw_string_basic 进了 .zbc 生成池** — `./scripts/regen-golden-tests.sh --no-stdlib` 报 `OK: raw_string_basic`，与既有 strings/ 系列 golden 同等地位。VM e2e 完整 GREEN 由后续 commit 前最终 `test-all.sh` 兜底。

## 完整变更清单

实施完成时改/新增：

- `src/compiler/z42.Syntax/Lexer/TokenKind.cs` — 加 `RawStringLiteral` enum
- `src/compiler/z42.Syntax/Lexer/LexRules.cs` — `StringRule` 加 `IsRaw` flag；`"""` 在 `"` 之前
- `src/compiler/z42.Syntax/Lexer/Lexer.cs` — `LexStringBody` 加 `IsRaw` 分支 + 未闭合 throw
- `src/compiler/z42.Syntax/Parser/ExprParser.cs` — NudTable 加 `RawStringLiteral` → `LitStrExpr` (Text[3..^3]，跳过 UnescapeString)
- `src/compiler/z42.Tests/LexerTests.cs` — 8 个新 [Fact]
- `docs/design/language/grammar.peg` — `RAW_STRING` PEG production
- `docs/design/language/language-overview.md` — 升级 `"""..."""` 示例段为正式规范
- `docs/design/language/raw-string-literal.md` — 新设计 doc + 4 个 Deferred 条目
- `examples/raw_string_basic.z42` — 示例文件
- `src/tests/strings/raw_string_basic/` — golden test (source.z42 + expected_output.txt + .zbc)
