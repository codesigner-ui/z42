# Tasks: BoundTree Rewriter framework (F2.5 Phase 1)

> 状态：🟢 已完成 | 创建：2026-06-02 | 完成：2026-06-02 | 类型：refactor（infrastructure-only，不改任何对外行为；现有 callers 不受影响）
> 来源：[`docs/review.md`](../../../review.md) Part 6 F2.5

## 变更说明

新增 `BoundExprRewriter` + `BoundStmtRewriter` 抽象基类，作为后续 BoundTree → BoundTree
lowering pass 的基础设施。每个 Visit 默认实现 = **递归 rewrite 所有 children**：
- 若所有 children `ReferenceEquals` 原值 → 返回 input 原节点
- 任一 child 被替换 → 用 record `with { ... }` 重建节点

子类只 override 自己关心的节点。

本 phase **不迁移任何现有 lowering**（foreach / interpolated-string / switch 仍在
FunctionEmitter 内 inline emit）—— 只建框架。Phase 2（独立 spec）再迁移。

## 原因

review.md F2.5 P1：当前 BoundTree → emit 直接进 FunctionEmitter，所有 lowering
混在 emit 代码里。L3 引入 async / lambda lifting 时复杂语义改写没有清晰边界，
会污染 IR emit。先建 Rewriter 框架（Roslyn `LocalRewriter` / Clang `RecursiveASTVisitor`
对应物），未来 lowering pass 走该框架，每个 pass 是一个独立 `BoundExprRewriter` /
`BoundStmtRewriter` 子类。

## 文档影响

- `docs/review.md` F2.5 状态更新（Phase 1 done）
- 不动 IR / VM / 语言语义

## Scope（允许改动的文件）

| 文件 | 变更类型 | 说明 |
|---|---|---|
| `src/compiler/z42.Semantics/Lowering/BoundExprRewriter.cs` | NEW | 30 个 VisitXxx default impl，按记录 children 递归重建 |
| `src/compiler/z42.Semantics/Lowering/BoundStmtRewriter.cs` | NEW | 16 个 VisitXxx default impl + 虚 `RewriteExpr` hook + `RewriteBlock` helper |
| `src/compiler/z42.Tests/BoundRewriterTests.cs` | NEW | 单元测试：identity rewriter / single-node 替换 / 嵌套替换 / block 重建 |
| `docs/review.md` | MODIFY | F2.5 状态更新 |

只读引用：
- `src/compiler/z42.Semantics/Bound/BoundExpr.cs` / `BoundStmt.cs` — 节点定义
- `src/compiler/z42.Semantics/Bound/BoundExprVisitor.cs` / `BoundStmtVisitor.cs` — Visitor 基类

## 任务

- [x] 0.1 NEW spec `tasks.md`
- [x] 1.1 NEW `BoundExprRewriter.cs` —— 30 visit methods, identity default per record
- [x] 1.2 NEW `BoundStmtRewriter.cs` —— 16 visit methods + `RewriteExpr` hook + `RewriteBlock` helper
- [x] 1.3 NEW `BoundRewriterTests.cs` —— 11 单元测试 (identity / 替换 / 嵌套 / Block 重建 / 组合 composition)
- [x] 1.4 VERIFY `dotnet build` clean + `dotnet test src/compiler/z42.Tests` 1455 全过
- [x] 1.5 MODIFY `review.md` F2.5 标 🟡 Phase 1 done
- [x] 1.6 归档 + commit + push

## 备注

### Pre-existing 测试 flake（非本 spec 引入，记入跟踪）

第一次 test-all.sh 跑到 dotnet test 阶段时 `PropertyTests.Parser_NeverCrashes_Random`
失败 1 次（FsCheck seed `(5975754578008530561,3724985247076472555)`，
shrunk 到输入 `""""`（4 个 double quote）—— 推测是 raw-string-literal 的
lexer 在 3 个 quote 起 + 不足 3 个 quote 终结时的 unterminated 路径
throws 而非 graceful error）。

- **不是本 spec 引入**：本 spec 仅添加 `z42.Semantics/Lowering/*` 新文件 +
  新测试，对 parser / lexer 路径零修改
- **重跑全过**（第二次 dotnet test 1455/1455 ✅），是 seed-dependent flake
- **跟踪**：建议后续独立 spec `fix-raw-string-lex-unterminated` 修 lexer 在
  `""""` 输入时不 throw（可改为返回 ErrorToken）。本 spec 不阻塞，提交。


## 设计要点

### 文件大小

为防止 BoundExprRewriter.cs 单文件超 500 行硬限（30 个 method × 平均 8 行 ≈ 240 行 + 文件头），
分块组织：方法按字典序排列 + 区段注释；估算 < 350 行。BoundStmtRewriter 估算 < 250 行。

### 复合节点（嵌套 BoundExpr 在 BoundStmt 内）

`BoundStmtRewriter` 通过虚 `RewriteExpr(BoundExpr) -> BoundExpr` hook 处理 stmt 内
的 BoundExpr child（默认 identity）。子类若需 expr rewrite，override 该 hook 转发
到组合的 `BoundExprRewriter`。

### IReadOnlyList 重建

`BoundCall.Args` / `BoundArrayLit.Elements` / `BoundInterpolatedStr.Parts` 等列表
child：用 helper `RewriteList<T>(IReadOnlyList<T>, Func<T, T>)` 统一处理 "any
child changed → rebuild list, else return original"。

### Why NOT inherit both visitors

C# 不支持多基类，review.md F2.5 草稿里的
`BoundTreeRewriter : BoundExprVisitor<BoundExpr>, BoundStmtVisitor<BoundStmt>` 不能
literal 实现。改用**两个独立抽象基类** + 子类需要双向时**显式组合**（pass 通常只 lowering
一个方向，组合是少数情况）。
