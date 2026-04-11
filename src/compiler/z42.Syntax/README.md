# z42.Syntax

## 职责
语法层：将源代码字符串转换为 AST（CompilationUnit）。包含 Lexer（词法分析）和 Parser（语法分析）。

## 核心文件
| 文件 | 职责 |
|------|------|
| `Lexer/TokenKind.cs` | Token 类型枚举 |
| `Lexer/Token.cs` | Token 数据结构（Kind + Text + Span）|
| `Lexer/Lexer.cs` | 通用执行引擎，无业务规则 |
| `Lexer/TokenDefs.cs` | 关键字表、符号表、类型关键字集合 |
| `Lexer/LexCombinators.cs` | 词法组合子原语 |
| `Lexer/LexRules.cs` | 数字/字符串规则声明 |
| `Parser/Core/TokenCursor.cs` | 不可变 Token 游标 |
| `Parser/Core/ParseResult.cs` | 解析结果类型（成功/失败）|
| `Parser/Core/Combinators.cs` | Parser 组合子库 |
| `Parser/Ast.cs` | 所有 AST 节点类型（sealed record）|
| `Parser/Parser.cs` | 公开入口 + `ParseException` |
| `Parser/TypeParser.cs` | 类型表达式解析 |
| `Parser/ExprParser.cs` | Pratt 表达式解析 |
| `Parser/StmtParser.cs` | 语句解析 |
| `Parser/TopLevelParser.cs` | 顶层声明（类、函数、枚举、接口）|

## 入口点
- `Z42.Syntax.Lexer.Lexer` — `new Lexer(src, file).Tokenize()`
- `Z42.Syntax.Parser.Parser` — `new Parser(tokens).ParseCompilationUnit()`

## 依赖关系
→ z42.Core（Span、DiagnosticBag、LanguageFeatures）
