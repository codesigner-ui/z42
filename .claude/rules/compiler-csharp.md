---
paths:
  - "src/compiler/**/*.cs"
---

# C# 编译器开发规范

## AST 节点

- 所有 AST 节点必须是 `sealed record`，继承对应的抽象基类（`Item`、`Expr`、`Stmt`、`TypeExpr`、`Pattern`）
- 每个节点末位参数必须是 `Span Span`，用于错误报告
- 节点不包含语义信息（类型、作用域等），这些由 TypeCheck 阶段填充

## TokenKind

- 新增 token 时，同时在 `Lexer.cs` 的 `Keywords` 字典或符号分支中处理
- 关键字用 `TokenKind.Xxx`（PascalCase），符号用描述名（`LtEq`, `FatArrow` 等）
- `Underscore` 是独立 token，不要把 `_` 识别为 identifier

## Parser

- 使用递归下降，不使用 parser combinator 库
- 优先级从低到高：赋值 → 逻辑或 → 逻辑与 → 相等 → 比较 → 加减 → 乘除 → 一元 → 后缀 → 主表达式
- 解析失败必须抛出 `ParseException(message, span)`，包含准确的行列信息
- 不要吞掉错误后继续（panic-mode recovery 留到后续阶段）

## 错误消息

- 格式：`expected X but got Y (text) at line:col`
- 面向语言使用者，不用内部枚举名（"Expected `->` but got `>`" 而非 "Expected Arrow but got Gt"）
