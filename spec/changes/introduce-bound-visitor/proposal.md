# Proposal: Introduce BoundExprVisitor / BoundStmtVisitor

## Why

[docs/review.md](../../../docs/review.md) Part 2 §2.1 + Part 1 §1.1 + §1.3：编译器中所有 Bound 树遍历都是手写 `switch (expr) { case BoundXxx: ... }` 直接拼装，**没有共用的 visitor 框架**。这导致：

- 每加一个新 `BoundExpr` 节点要同步改 10 处 dispatch（Codegen / FlowAnalyzer / ClosureEscapeAnalyzer），漏改一处编译器静默退化
- [FunctionEmitterExprs.cs](../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs) 单方法 `EmitExpr` 274 行；同一巨型 switch 模式在 3 个文件复制粘贴
- M6→M7 期间还要做 `impl-dump-ast`，没有 visitor 框架则要再写第 11 处手写 switch；有 visitor 后 dump-ast 就是一个 `BoundExprVisitor<string>` 子类，几乎免费
- M7 反射 / R-series 后续还会带来更多树遍历需求（serialize / canonical hash / equality），visitor 是它们的共同基础

**不做会怎样**：M7 启动后每加一个反射相关 pass 都重写一次 switch；`FunctionEmitter*` 文件继续膨胀；新 BoundExpr 节点漏改 dispatch 的 ICE 风险持续存在。

## What Changes

- 新增 `BoundExprVisitor<TResult>` 抽象基类（在 `z42.Semantics/Bound/` 下），`Visit(BoundExpr)` 在基类做 switch dispatch，每个 `BoundXxx` 对应一个 `protected abstract VisitXxx(BoundXxx)`
- 新增 `BoundStmtVisitor<TResult>` 同模式
- 提供 `BoundExprWalker`（void 遍历）+ `BoundStmtWalker` 默认实现，子类只需 override 关心的节点
- 迁移 5 个调用点（不动行为）：
  - `FunctionEmitter.CollectClassRefs` (BoundExpr scan, smallest)
  - `FlowAnalyzer.AlwaysReturns` + 2 expr 辅助
  - `ClosureEscapeAnalyzer.AnalyzeStmt` + 2 expr 辅助
  - `FunctionEmitter.EmitExpr` (BoundExpr→IR, biggest)
  - `FunctionEmitter.EmitBoundStmt` (BoundStmt→IR)
- `docs/design/compiler-architecture.md` 增段落把 visitor 模式记为**正面设计**

**不做**：
- AST 树（`Syntax.Parser.Expr/Stmt`）的 visitor —— TypeChecker.Exprs/Stmts 的 AST→Bound 转换是另一棵树，留给独立 spec
- `FunctionEmitter.cs` 529 LOC 超限拆分（visitor 迁移不解决，留 follow-up `split-function-emitter` spec）
- 任何行为/性能变化（纯 refactor，所有 dispatch 都变成"基类 switch + 虚方法"，性能差异可忽略）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Semantics/Bound/BoundExprVisitor.cs` | NEW | `BoundExprVisitor<T>` + `BoundExprWalker` 基类 |
| `src/compiler/z42.Semantics/Bound/BoundStmtVisitor.cs` | NEW | `BoundStmtVisitor<T>` + `BoundStmtWalker` 基类 |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs` | MODIFY | `CollectClassRefs` 改用 visitor |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` | MODIFY | `EmitExpr` switch 改为 `IrEmitExprVisitor` 子类 |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.cs` | MODIFY | `EmitBoundStmt` 改为 `IrEmitStmtVisitor` 子类 |
| `src/compiler/z42.Semantics/TypeCheck/FlowAnalyzer.cs` | MODIFY | 3 处 switch 改 visitor |
| `src/compiler/z42.Semantics/TypeCheck/ClosureEscapeAnalyzer.cs` | MODIFY | 3 处 switch 改 visitor |
| `src/compiler/z42.Tests/BoundVisitorTests.cs` | NEW | 单元测试：每个 BoundXxx 都被 dispatch；遗漏节点编译期失败 |
| `docs/design/compiler-architecture.md` | MODIFY | 增段："Bound tree visitor pattern"（正面设计） |
| `src/compiler/z42.Semantics/README.md` | MODIFY | 增 visitor 入口指引 |

**只读引用**：

- `src/compiler/z42.Semantics/Bound/BoundExpr.cs` — 列出全部节点种类
- `src/compiler/z42.Semantics/Bound/BoundStmt.cs` — 同上
- `docs/review.md` Part 1 §1.1 / §1.3 + Part 2 §2.1 — 立项依据

## Out of Scope

- AST visitor（TypeChecker.Exprs / Stmts 的 AST→Bound 转换）
- `FunctionEmitter.cs` 529 LOC 拆分（独立 follow-up spec）
- Source generator 自动生成 visitor（手写够用，pre-1.0 不引外部代码生成依赖）
- 性能优化（visitor 与 switch 性能差异不在本次衡量范围）

## Open Questions

- [ ] **visitor 与 partial class 的交互**：`FunctionEmitter` 是 internal partial sealed class；emit visitor 子类需要访问 emitter 私有状态（`_locals` / `_ctx` / `Emit` / `Alloc`）。两个选项：(a) visitor 是 emitter 的 nested private class；(b) visitor 是独立类，构造函数注入 emitter 引用。Decision 留给 design.md。
- [ ] **是否同时引入 BoundStmtWalker 和 BoundExprWalker** 的 void 遍历版本？还是只在需要时再加？倾向只引入需要的（CollectClassRefs / FlowAnalyzer / ClosureEscapeAnalyzer 都是 walker 形态；emit 是 visitor<TypedReg> / visitor<void>）
