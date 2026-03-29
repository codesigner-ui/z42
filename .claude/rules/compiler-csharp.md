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

## Parser 架构（Superpower 风格，手写组合子）

**不引入外部 parser combinator 库**（为自举保留）；参考 Datalust/Superpower 设计思路手写实现。

### 核心类型（`Parser/Core/`）

| 类型 | 职责 |
|------|------|
| `TokenCursor` (readonly struct) | 不可变游标；`Advance/Peek` 返回新游标，lookahead 直接用游标而非 `_pos+i` 算术 |
| `ParseResult<T>` (readonly struct) | 成功: `Value + Remainder`；失败: `Error + ErrorSpan`；`.Unwrap(ref cursor)` 升级为异常 |
| `Parser<T>` (delegate) | `TokenCursor → ParseResult<T>` 纯函数委托 |
| `P` (static class) | 组合子：`Token / Or / Many / Optional / SeparatedBy / Between / Then / Select / Lazy` |

### 解析策略

- **递归下降 + Pratt 表达式**：顶层声明和语句用递归下降，表达式用 Pratt（NudTable / LedTable）
- **可恢复失败**：`Or` 分支检查 `ParseResult<T>.IsOk`，失败不消耗游标
- **不可恢复错误**：`.OrThrow()` / `.Unwrap(ref cursor)` 将失败升级为 `ParseException`
- **Feature gate**：`feat.IsEnabled("xxx")`；未启用时 `throw new ParseException(...)`
- **不吞错误**：expression 解析失败必须 throw，不做 panic-mode recovery

### 运算符优先级（Pratt binding power）

```
10  赋值（right-assoc）        20  三目 / ??
30  ||                          40  &&
44/46/48  |/^/& (bitwise)      50  == !=
60  < <= > >= is as            65  << >> (bitwise)
70  + -                        80  * / %
85  switch expr (postfix)      90  ++ -- call . [] (postfix)
```

### 文件职责

| 文件 | 职责 |
|------|------|
| `Core/TokenCursor.cs` | 不可变游标 |
| `Core/ParseResult.cs` | 结果类型 + `Unwrap(ref cursor)` |
| `Core/P.cs` | 组合子（`Parser<T>` delegate + 扩展方法） |
| `TypeParser.cs` | 类型表达式，暴露 `Parser<TypeExpr> TypeExpr` 属性供组合子使用 |
| `ExprParser.cs` | Pratt loop + NudTable + LedTable（NudFn/LedFn 显式传 cursor） |
| `StmtParser.cs` | 语句解析（含 `ParseBlock`，返回 `ParseResult<BlockStmt>`） |
| `TopLevelParser.cs` | 顶层声明；lookahead 用 `cursor.SkipWhile(...)` 而非 `_pos+i` |
| `Parser.cs` | 公开入口 + `ParseException` 定义 |

## 错误消息

- 格式：`` expected `(` but got `{token}` ``
- 面向语言使用者，不用内部枚举名（`` expected `(` `` 而非 `expected LParen`）
