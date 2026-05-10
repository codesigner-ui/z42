# Tasks: Introduce BoundExprVisitor / BoundStmtVisitor

> 状态：🟢 已完成 | 创建：2026-05-10 | 完成：2026-05-10
> 类型：refactor（不改行为，纯架构提取）
> 关联：[docs/review.md](../../../docs/review.md) Part 2 §2.1 + Part 1 §1.1 + §1.3

## 进度概览

- [x] 阶段 1: Visitor 基础设施（NEW 文件）
- [x] 阶段 2: 单元测试 + GREEN 基线
- [x] 阶段 3: CollectClassRefs 迁移（最小验证）
- [x] 阶段 4: FlowAnalyzer 3 处迁移
- [x] 阶段 5: ClosureEscapeAnalyzer 3 处迁移
- [x] 阶段 6: EmitBoundStmt 迁移
- [x] 阶段 7: EmitExpr 迁移（最大）
- [x] 阶段 8: 文档同步 + 归档

---

## 阶段 1: Visitor 基础设施

- [x] 1.1 NEW [src/compiler/z42.Semantics/Bound/BoundExprVisitor.cs](../../../src/compiler/z42.Semantics/Bound/BoundExprVisitor.cs):
  - `public readonly record struct Unit;`（void-substitute）
  - `public abstract class BoundExprVisitor<TResult>` + 27 个 `protected abstract VisitXxx`（按 design.md Decision 5 命名）
  - 基类 `Visit(BoundExpr e)` switch dispatch
  - `public abstract class BoundExprWalker : BoundExprVisitor<Unit>` + 默认 leaf=no-op / interior=recurse 实现
- [x] 1.2 NEW [src/compiler/z42.Semantics/Bound/BoundStmtVisitor.cs](../../../src/compiler/z42.Semantics/Bound/BoundStmtVisitor.cs):
  - `BoundStmtVisitor<TResult>` + 17 个 abstract（每个 BoundStmt 子类一个）
  - `BoundStmtWalker : BoundStmtVisitor<Unit>` 默认实现
  - Walker 在递归到 `BoundBlock.Stmts` / `BoundIf.Then|Else` / `BoundWhile.Body` 等子树时正确遍历
- [x] 1.3 `dotnet build src/compiler/z42.slnx` —— 无编译错误

## 阶段 2: 单元测试 + GREEN 基线

- [x] 2.1 NEW [src/compiler/z42.Tests/BoundVisitorTests.cs](../../../src/compiler/z42.Tests/BoundVisitorTests.cs):
  - `Visit_AllConcreteBoundExprTypes_DispatchesCorrectly`：反射 + 测试 visitor 计数器
  - `Visit_AllConcreteBoundStmtTypes_DispatchesCorrectly`
  - `Walker_BoundBinaryWithTwoLiterals_VisitsThreeTimes`
  - `Walker_BoundIfNested_RecursesAllStmts`
- [x] 2.2 `dotnet test` 全绿（含新测试 + 757 旧测试）
- [x] 2.3 `./scripts/test-vm.sh` 全绿（baseline 不变）

## 阶段 3: CollectClassRefs 迁移（最小验证）

- [x] 3.1 MODIFY [src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs:493](../../../src/compiler/z42.Semantics/Codegen/FunctionEmitter.cs#L493):
  - `CollectClassRefs` 改为 `private sealed class ClassRefScanner : BoundExprWalker`
  - Scanner 持有 `_classNames` / `_self` / `_refs` 字段，override `VisitMember` / `VisitIdent` 等关键节点
  - 删除原 switch 实现
- [x] 3.2 `dotnet test` 全绿
- [x] 3.3 commit: `refactor(compiler): introduce-bound-visitor S3 — migrate CollectClassRefs to walker`

## 阶段 4: FlowAnalyzer 3 处迁移

- [x] 4.1 MODIFY [src/compiler/z42.Semantics/TypeCheck/FlowAnalyzer.cs:111](../../../src/compiler/z42.Semantics/TypeCheck/FlowAnalyzer.cs#L111):
  - `AlwaysReturns(BoundStmt)` 改为 `BoundStmtVisitor<bool>` 子类
- [x] 4.2 MODIFY [src/compiler/z42.Semantics/TypeCheck/FlowAnalyzer.cs:258](../../../src/compiler/z42.Semantics/TypeCheck/FlowAnalyzer.cs#L258):
  - 第二处 BoundExpr switch 改 visitor
- [x] 4.3 MODIFY [src/compiler/z42.Semantics/TypeCheck/FlowAnalyzer.cs:377](../../../src/compiler/z42.Semantics/TypeCheck/FlowAnalyzer.cs#L377):
  - 第三处 BoundExpr switch 改 visitor
- [x] 4.4 `dotnet test` 全绿
- [x] 4.5 commit: `refactor(compiler): introduce-bound-visitor S4 — migrate FlowAnalyzer dispatches`

## 阶段 5: ClosureEscapeAnalyzer 3 处迁移

- [x] 5.1 MODIFY [src/compiler/z42.Semantics/TypeCheck/ClosureEscapeAnalyzer.cs:66](../../../src/compiler/z42.Semantics/TypeCheck/ClosureEscapeAnalyzer.cs#L66):
  - 第一处 BoundStmt switch（`AnalyzeStmt`）改 visitor
- [x] 5.2 MODIFY [src/compiler/z42.Semantics/TypeCheck/ClosureEscapeAnalyzer.cs:123](../../../src/compiler/z42.Semantics/TypeCheck/ClosureEscapeAnalyzer.cs#L123):
  - 第二处 BoundStmt switch
- [x] 5.3 MODIFY [src/compiler/z42.Semantics/TypeCheck/ClosureEscapeAnalyzer.cs:200](../../../src/compiler/z42.Semantics/TypeCheck/ClosureEscapeAnalyzer.cs#L200):
  - BoundExpr switch
- [x] 5.4 `dotnet test` 全绿
- [x] 5.5 commit: `refactor(compiler): introduce-bound-visitor S5 — migrate ClosureEscapeAnalyzer dispatches`

## 阶段 6: EmitBoundStmt 迁移

- [x] 6.1 MODIFY [src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.cs:36](../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterStmts.cs#L36):
  - `EmitBoundStmt` 改为 `private sealed class IrEmitStmtVisitor : BoundStmtVisitor<Unit>` nested in FunctionEmitter
  - Visitor 通过 `_outer` 字段访问 emitter 私有 helpers
  - `EmitBoundStmt(stmt)` 变为 `_stmtVisitor.Visit(stmt)`
- [x] 6.2 `dotnet test` + `./scripts/test-vm.sh` 全绿（GREEN 验证 IR 字节级一致：选 1–2 个 golden test 抓 `--dump-ir` 比对前后输出）
- [x] 6.3 commit: `refactor(compiler): introduce-bound-visitor S6 — migrate EmitBoundStmt to visitor`

## 阶段 7: EmitExpr 迁移（最大）

- [x] 7.1 MODIFY [src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs:13](../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs#L13):
  - `EmitExpr` 改为 `private sealed class IrEmitExprVisitor : BoundExprVisitor<TypedReg>`
  - 27 个 case 分散到 27 个 `VisitXxx` 方法（仍在同文件 nested class 内）
  - 复杂 case 委托到 outer 的 `Emit*` partial helper（`EmitBoundCall` / `EmitBoundBinary` / 等已存在）
- [x] 7.2 `dotnet test` 全绿
- [x] 7.3 `./scripts/test-vm.sh` 全绿（含字节级 IR 一致性抽查）
- [x] 7.4 commit: `refactor(compiler): introduce-bound-visitor S7 — migrate EmitExpr to visitor`

## 阶段 8: 文档同步 + 归档

- [x] 8.1 MODIFY [docs/design/compiler-architecture.md](../../../docs/design/compiler-architecture.md):
  - 增段 "Bound tree visitor pattern" — 列为正面设计
  - 说明：基类 switch + abstract Visit；新增 BoundExpr 节点的 5 步流程（节点 record → 加 abstract → 编译期失败提示所有 visitor 子类 override）
- [x] 8.2 MODIFY [src/compiler/z42.Semantics/README.md](../../../src/compiler/z42.Semantics/README.md):
  - 在 "核心文件" 增 `Bound/BoundExprVisitor.cs` / `Bound/BoundStmtVisitor.cs` 入口说明
- [x] 8.3 MODIFY [docs/review.md](../../../docs/review.md):
  - Part 2 §2.1 状态从 📋 改为 🟢 2026-05-10
  - "立项建议优先级" 移除 `introduce-bound-visitor`
  - 修订记录追加同步条目
- [x] 8.4 tasks.md 状态改 🟢 已完成
- [x] 8.5 移动 `docs/spec/changes/introduce-bound-visitor/` → `docs/spec/archive/2026-05-10-introduce-bound-visitor/`
- [x] 8.6 commit: `docs+spec(compiler): introduce-bound-visitor — archive + sync architecture doc`
- [x] 8.7 push origin main

## 备注

### 中间状态约束
- 每个阶段独立 commit，commit 前 `dotnet test` + `./scripts/test-vm.sh` 全绿
- 任一阶段 GREEN 失败 → 停下排查根因（参见 workflow.md 修复必须从根因出发），不跳过

### 不解决的问题（follow-up spec 处理）
- `FunctionEmitter.cs` 529 LOC 超 500 硬限 → 独立 `split-function-emitter` spec
- AST→Bound 转换（TypeChecker.Exprs/Stmts）的 visitor 化 → 独立 `introduce-ast-visitor` spec
- Source generator 自动派发 → pre-1.0 不引入

### 风险
- **风险 1**：迁移 EmitExpr 时漏改某个 case 导致行为变化 → 缓解：visitor 基类的 abstract 强制全部 override，编译期就会失败；额外保险是 GREEN 抓 1–2 个 golden test 的 `--dump-ir` 字节比对
- **风险 2**：Walker 默认递归对某子类不正确 → 缓解：每个迁移阶段独立 commit + 全绿验证；发现问题局部 override 即可
- **风险 3**：性能回归 → 缓解：visitor + 虚方法在 .NET JIT 下与直接 switch 性能差距通常 < 5%；Bound 树规模小，编译时长不敏感。如发现明显回归（>10%）再评估
