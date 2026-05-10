# Proposal: enhance-expr-recovery

## Why

Parser 已实现声明级 + 语句级错误恢复（TopLevelParser / StmtParser 都 catch ParseException + diags + skip），并把 `parser.Diagnostics.All` 推入 pipeline 主 diags。**但 expression 级别仍然 throw 到最近的 stmt 边界**：

- `f(badArg1, goodArg2)` — bad arg 抛 ParseException，吞掉后续 arg 解析
- `var x = badExpr1 + badExpr2;` — 第一个失败就跳过整条 stmt
- ErrorExpr 节点定义在 AST 但 parser 从未创建它（review.md §3.7 视为正面设计的 "ErrorStmt/ErrorExpr 优雅恢复"，**ErrorExpr 部分实际为死代码**）

review.md §2.2 描述基于过期理解（早于声明/语句级 recovery 落地）；当前真实差距是 **expression 级别**。

不做后果：
- IDE/LSP 集成场景无法在表达式级别精细报告错误
- 函数调用 / 数组字面量 / 三元等 expression 内嵌结构的错误诊断质量较低
- review.md §3.7 的"优雅设计"评价不能完整兑现

## What Changes

- `ExprParser.Parse` 顶层入口添加 `DiagnosticBag? diags = null` 重载：传入时遇 ParseException 不冒泡，改为 `diags.Error(...)` + 创建 `ErrorExpr` 节点 + skip 到表达式边界
- 新增 `SkipToExprBoundary(cursor)` 静态 helper：跳到 `,` `)` `;` `]` `}` 或 EOF（表达式 sync points）
- `ExprParser.Atoms.cs::ParseArgList` / `ParseCallArgWithOptionalModifier` 接 DiagnosticBag，让函数调用 / 数组字面量等聚合表达式的每个子项独立 recover
- 20 个 `ExprParser.Parse(...).Unwrap(...)` 调用站点 thread bag（StmtParser / TopLevelParser 已持 bag 的现场全部传入；test path `Parser.ParseExpr()` 保持 throw 行为）
- 新增 `ParserRecoveryTests.cs` 验证多错误聚合：
  - 单 stmt 中两个 bad expr 各自产生 ErrorExpr
  - bad arg 不阻断后续 arg 解析
  - 总错误数等于预期
- TypeChecker.Exprs.cs 现有的 `case ErrorExpr` 防御代码确保新产生的 ErrorExpr 流到 TypeCheck 时不崩溃（已就位）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `src/compiler/z42.Syntax/Parser/ExprParser.cs` | MODIFY | `Parse` 入口加 DiagnosticBag overload；catch 包裹 |
| `src/compiler/z42.Syntax/Parser/ExprParser.Atoms.cs` | MODIFY | `ParseArgList` / `ParseCallArgWithOptionalModifier` thread bag |
| `src/compiler/z42.Syntax/Parser/StmtParser.cs` | MODIFY | 16 个 `ExprParser.Parse(...).Unwrap(ref cursor)` 调用站点 thread `_diags`（来自 ParseBlock 上下文） |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.cs` | MODIFY | 2 个调用站点 thread bag |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Helpers.cs` | MODIFY | 默认参数值解析 thread bag |
| `src/compiler/z42.Syntax/Parser/TopLevelParser.Types.cs` | MODIFY | 字段初始化 expr 解析 thread bag |
| `src/compiler/z42.Syntax/Parser/Parser.cs` | MODIFY | doc 更新（移除"recovery 已就位但 ErrorExpr 死代码"的隐含）|
| `src/compiler/z42.Tests/ParserRecoveryTests.cs` | NEW | multi-error 验证测试 ~6 case |
| `docs/review.md` | MODIFY | 路线图 §2.2 / §3.7 状态注记 |

**只读引用**:
- `src/compiler/z42.Syntax/Parser/Ast.cs` — `ErrorExpr` 节点定义
- `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs:256` — 现有 `case ErrorExpr` 防御处理

## Out of Scope

- ExprParser 内部 throw 站点的"递归子表达式 recovery"（如 `Pratt loop` 内部 ParseException 仍然冒泡到 Parse 顶层 catch）—— 当前单层 catch 足够（每个 `Parse()` 顶层调用 ≈ 一个 sync 边界）；递归子级 recovery 是更激进的设计，留独立 spec 触发
- 删除 `ParseException` 类（callers 仍依赖 throw 行为）
- 删除 ErrorExpr 节点（保留作为 expression 级 recovery 标记）

## Open Questions

- [ ] `ExprParser.Parse(cursor, feat, minBp = 0).OrThrow()` 在 `Parser.ParseExpr()` 公开 API 中的去留（保留 throw 给单测路径，OK？）
- [ ] `ParseCallArgWithOptionalModifier` 中 `out var x` 的 expected-identifier 检查（line 350-354）是否也应转 ErrorExpr？暂保留 throw（少数特殊路径，恢复成本高于价值）
