# Spec: Expression-level Parser Error Recovery

## ADDED Requirements

### Requirement: ExprParser 顶层入口接受 DiagnosticBag

Parser 提供 `ExprParser.Parse(cursor, feat, minBp = 0, DiagnosticBag? diags = null)` 重载。当 `diags == null` 时保持当前行为（错误 throw）；当 `diags != null` 时启用 expression 级别 recovery。

#### Scenario: bag == null 保持 throw 行为（向后兼容）

- **WHEN** caller 用 `ExprParser.Parse(cursor, feat).Unwrap(ref cursor)` 解析含 ParseException 触发条件的表达式
- **THEN** 行为与本 spec 落地前完全一致（throw ParseException）

#### Scenario: bag != null 时单错误转 ErrorExpr

- **WHEN** caller 传入 DiagnosticBag 解析 `5 +` （右操作数缺失）
- **THEN** `diags.HasErrors == true`；返回 `ParseResult.Ok` 含 `ErrorExpr` 节点；cursor 推进到 sync 边界（`;` / `)` / 等）；不抛异常

#### Scenario: 调用方继续解析剩余 input

- **WHEN** caller 持 bag 在循环中解析多个表达式（如 `var x = 5+;` 后接 `var y = 1+1;`）
- **THEN** 第一个表达式产生 ErrorExpr + 1 个 diag，第二个表达式正常解析；总 diags 数 = 1（不是 2）

### Requirement: 函数调用 / 数组字面量等聚合表达式的子项独立恢复

`ExprParser.Atoms::ParseArgList` 和 `ParseCallArgWithOptionalModifier` 在持 bag 时让每个 arg 独立 recover；单个 bad arg 不阻断后续 arg 解析。

#### Scenario: 函数调用中间 arg 失败

- **WHEN** caller 持 bag 解析 `f(1, badtok, 3)`（badtok 是非表达式 token）
- **THEN** `args` 列表含 3 个元素：`IntLit(1)` / `ErrorExpr` / `IntLit(3)`；`diags` 含 1 个 error；后续 cursor 推进到 `f(...)` 的 `)` 之后

#### Scenario: 多个 bad arg 独立报错

- **WHEN** caller 持 bag 解析 `f(bad1, bad2, ok)`
- **THEN** `args` 含 3 个元素：`ErrorExpr` / `ErrorExpr` / `Expr(ok)`；`diags` 含 2 个 error

### Requirement: SkipToExprBoundary helper 定义 expression sync points

新增静态 helper `SkipToExprBoundary(TokenCursor cursor) -> TokenCursor`，跳到下一个表达式 sync 边界。

**Sync token set**: `,` `)` `]` `;` `}` `EOF`

#### Scenario: 跳过单个 bad token

- **WHEN** cursor 当前指向 bad token，下一个 token 是 `,`
- **THEN** `SkipToExprBoundary(cursor)` 返回 cursor 位置在 `,` 处（不消费 sync 本身）

#### Scenario: 嵌套括号不被穿越

- **WHEN** cursor 在 `f(bad, (inner_bad,) outer)` 内 `bad` 处
- **THEN** `SkipToExprBoundary(cursor)` 返回 cursor 在第一个 `,` 处（不进入嵌套 `(...)`）；嵌套 sync 由 caller 的 ArgList 循环管控

### Requirement: Pipeline 持续传递 expression 级 diags

整条 pipeline（SingleFileCompiler / PackageCompiler / PipelineCore）已在收尾时把 `parser.Diagnostics.All` 推入主 DiagnosticBag。新产生的 expression 级 errors 自动随之向 pipeline 上游冒泡。

#### Scenario: 编译报告含多个 expression 级 errors

- **WHEN** 编译一个含 `void Main() { var x = 5+; var y = ; }` 的源文件
- **THEN** 编译失败；diagnostics 含至少 2 个 parser-level errors（一处 `5+` 缺右操作数 + 一处 `=` 后缺表达式）

## Pipeline Steps

受影响的 pipeline 阶段：
- [x] Lexer — 无变更
- [x] Parser / AST — `ExprParser` 入口 + 调用站点
- [x] TypeChecker — 已通过 `case ErrorExpr` 防御接收新 ErrorExpr 节点（不需要新代码）
- [ ] IR Codegen — 不应到达（pipeline 在 parser HasErrors 后 bail）
- [ ] VM interp — 不应到达
