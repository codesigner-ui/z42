# Tasks: fix-string-literal-escape

> 状态：🟡 进行中 | 创建：2026-04-26 | 类型：fix (parser bug)

> **变更说明**：z42 字符串字面量 `"\n"` / 字符字面量 `'\n'` 现在被原样保留（2 个字符或反斜杠）；
> 标准 C# / Rust 行为是处理转义序列。修复 parser 在构造 `LitStrExpr` / `LitCharExpr` 时
> 对常见转义做替换：`\n` → 0x0A，`\t` → 0x09，`\r` → 0x0D，`\\` → `\`，`\"` → `"`，
> `\'` → `'`，`\0` → 0。
>
> **原因**：Lexer 在源代码扫描时正确跳过转义字符（避免把 `\"` 当作字符串结束），
> 但 Parser 取 `t.Text[1..^1]` 后**没有解码**转义。结果 `Console.WriteLine("a\nb")`
> 输出 `a\nb`（4 字符）而非 `a` + newline + `b`。这是 Parser bug，与文档描述的字符串
> 字面量语义不符。
>
> **触发场景**：z42.text Script-First StringBuilder 改写（依赖 `"\n"` 实际是
> newline）暴露了该缺陷；任何用户代码用 `"\n" / "\t"` 都会受影响。
>
> **文档影响**：
>   - `src/compiler/z42.Syntax/Parser/ExprParser.cs:215-216` 加 unescape helper
>   - `src/compiler/z42.Tests/LexerTests.cs` / `ParserTests.cs` 加转义测试
>   - `docs/design/language-overview.md`（如果有 string literal 章节）确认/同步

- [x] 1.1 在 ExprParser 加 `UnescapeString(string raw)` helper
- [x] 1.2 StringLiteral nud 调用 unescape
- [x] 1.3 CharLiteral nud 调用 unescape（含 `'\n'` → '\n' 等）
- [x] 1.4 InterpolatedString 文本片段也走 unescape（如有需要）
- [x] 1.5 单元测试：`"\n"` → 1 char；`"a\tb"` → 3 chars；`"\\"` → 1 char `\`
- [x] 1.6 全绿验证：dotnet test / regen golden / test-vm
- [x] 1.7 commit + push
