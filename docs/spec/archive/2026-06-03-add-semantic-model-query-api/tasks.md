# Tasks: SemanticModel query API (F2.3 Phase 1)

> 状态：🟢 已完成 | 创建：2026-06-03 | 完成：2026-06-03 | 类型：refactor（query API 添加，不改 TypeCheck 语义）
> 来源：[`docs/review.md`](../../../review.md) Part 6 F2.3

## 变更说明

把现有 `SemanticModel`（TypeChecker 输出的纯数据容器）扩展出一组**按需查询
API**，先实现两个不依赖 ISymbol (F2.2) 的方法：

```csharp
public BoundExpr? GetBoundExpression(Expr astNode);   // O(1) via internal map
public Z42Type?  GetExpressionType(Expr astNode);     // 上面结果的 .Type
```

TypeChecker 在 `BindExpr` 入口集中记录 Expr→BoundExpr 映射（reference-equality
keyed，因为 Expr 是 `sealed record` 默认结构相等会冲突）；SemanticModel
持有该 dict 提供查询。

## 原因

review.md F2.3：Roslyn 的 `SemanticModel.GetTypeInfo / GetSymbolInfo /
GetDeclaredSymbol` 是 IDE / Analyzer 类工具的核心 API。当前 z42 的
SemanticModel 只暴露**整棵 BoundTree**，调用方要按 AST node 找 BoundExpr 必须
自己 walk。Phase 1 添加 2 个查询方法解决这层不对称——是后续 IDE 集成 /
F2.2 ISymbol / F2.3 Phase 2 lazy binding 的前提。

完整 Phase 1（review.md 列出 5 方法）的另外 3 个：
- `GetSymbol(Expr) -> ISymbol?` — 需 F2.2 ISymbol，**阻塞，不做**
- `GetDeclaredSymbol(Item) -> ISymbol?` — 同上
- `GetDiagnostics(Span) -> IEnumerable<Diagnostic>` — 需 SemanticModel 持
  DiagnosticBag 引用 + Span 过滤；**独立 spec 后续**（DiagnosticBag 当前
  生命周期由 PipelineCore 持，不进 SemanticModel）

## 文档影响

- `docs/review.md` F2.3 状态：标 🟡 Phase 1 (2 of 5 methods) done

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` | MODIFY | 把 `BindExpr` 改为 `BindExpr` (wrapper) + `BindExprCore` (现有 switch body)；wrapper 写 `_exprBindings[expr] = bound` |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs` | MODIFY | 加 `private readonly Dictionary<Expr, BoundExpr> _exprBindings = new(ReferenceEqualityComparer.Instance);`；构造 SemanticModel 时传入 |
| `src/compiler/z42.Semantics/TypeCheck/SemanticModel.cs` | MODIFY | 加 `ExpressionBindings` property + `GetBoundExpression` / `GetExpressionType` 方法 + 构造方法新参数 |
| `src/compiler/z42.Tests/SemanticModelQueryTests.cs` | NEW | 单元测试：lookup hit / lookup miss / type access / 跨多 expressions |
| `docs/review.md` | MODIFY | F2.3 状态更新 |

只读引用：
- `src/compiler/z42.Semantics/Bound/BoundExpr.cs` — `Type` 字段定义
- `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` — `Z42Type` 体系
- `src/compiler/z42.Syntax/Parser/Ast.cs` — `Expr` 节点 hierarchy

## 设计要点

### ReferenceEqualityComparer 必需

`Expr` 是 C# `sealed record`，`==` / `GetHashCode()` 是 structural。两个不同
位置的相同字面量 `42` 会 hash 到同一 bucket → dict collision，查不到的
expression。用 `ReferenceEqualityComparer.Instance` 强制 identity equality。

### 单一记录点

`TypeChecker.BindExpr` 是所有 Expr → BoundExpr 转换的**唯一入口**（20 处
内部递归调用 + 外部 caller 都走它）。一处 wrapper 就够。`BindIdent`,
`BindCall`, `BindBinary` 等 helper 都通过 `BindExpr` 递归——结果同步进 dict。

### 不改 SemanticModel 现有字段

新增是**纯加法**：现有 `BoundBodies` / `BoundDefaults` / 等字段保留；新增
`ExpressionBindings` 字段 + 2 方法。后向兼容。

## 任务

- [x] 0.1 NEW spec `tasks.md`
- [x] 1.1 MODIFY `TypeChecker.Exprs.cs` 拆 `BindExpr` 为 wrapper + `BindExprCore`
- [x] 1.2 MODIFY `TypeChecker.cs` 加 `_exprBindings` field + 构造 SemanticModel 传 `exprBindings:`
- [x] 1.3 MODIFY `SemanticModel.cs` 加 `ExpressionBindings` property + `GetBoundExpression` + `GetExpressionType` + 构造参数
- [x] 1.4 NEW `z42.Tests/SemanticModelQueryTests.cs` 6 单元测试（lit / binary / unbound miss / reference-equality keying / 嵌套 / ExpressionBindings property）
- [x] 1.5 VERIFY `dotnet test` 1459/1459 pass（filter exclude IncrementalBuildIntegrationTests）
- [x] 1.6 MODIFY `review.md` F2.3 标 🟡 Phase 1 partial (2/5 done)
- [x] 1.7 归档 + commit + push
