# Proposal: add-z42c-source-spans — z42c AST/Bound 携 source span → DBUG section

> 状态：DRAFT（待 User 审批）｜子系统锁：z42c（port-z42c-zbc-writer 归档后接力）

## Why

z42c 自举 `.zbc` 写入器已功能完整（ZW-1A–1E），但**全面 byte-identical 被 DBUG 阻塞**：C# z42c 对任何有语句体的函数 emit DBUG section（源码行表 LineTable + 局部变量表），而 z42c 的 AST/Bound 树不携 source span（codegen 期刻意延后）。不做：① `.zbc` 逐字节对账永远只能停在 `empty`（self-hosting 0.3.x 退出标准无法达成）；② z42c 的诊断永远是 `<sem>` 占位（无行号列号，dogfood 排错体验差）。

## What Changes

- z42c.syntax：**全 AST 节点携 Span**（Expr 16 / Stmt 14 / Decl 9 / TypeExpr 3），Parser 在每个构造点填入（取首 token 行/列；Token 已有 line/col）
- z42c.semantics：**Bound 节点携 Span**（binder 从 AST 透传）；TypeChecker 诊断用真实 span（替换 `_noSpan()` 占位）
- z42c.semantics（codegen）：`TrackLine` 机制（每语句记 blockIdx/instrIdx/line/file-basename/column，同行去重）→ IrFunction.LineTable；LocalVarTable（局部名→reg id）
- z42c.ir：`IrLineEntry` + IrFunction 加 LineTable/LocalVarTable + ZbcWriter **DBUG section**（flags.HasDebug + 第 9 section）+ file-basename intern
- 验证升级：**byte-identical gate 扩展到有语句体函数**——`int F(){return 5;}` 等对 C# 同源产物逐字节；xtask e2e 加 byte-compare 步（z42c vs C# driver 同源 diff）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/z42c/z42c.syntax/src/Ast.z42` | MODIFY | Expr 节点加 Span 字段 + ctor 参数 |
| `src/z42c/z42c.syntax/src/Stmt.z42` | MODIFY | Stmt 节点加 Span |
| `src/z42c/z42c.syntax/src/Decl.z42` | MODIFY | Decl 节点加 Span |
| `src/z42c/z42c.syntax/src/TypeExpr.z42` | MODIFY | TypeExpr 节点加 Span |
| `src/z42c/z42c.syntax/src/Parser.z42` | MODIFY | 全构造点填 span（首 token 行/列 + file） |
| `src/z42c/z42c.semantics/src/Bound.z42` | MODIFY | Bound 节点加 Span |
| `src/z42c/z42c.semantics/src/TypeChecker.z42` | MODIFY | binder 透传 span；诊断用真实 span（删 `_noSpan` 占位用法） |
| `src/z42c/z42c.semantics/src/EmitContext.z42` | MODIFY | + `_lastLine`/TrackLine/LineTable/LocalVar 收集 |
| `src/z42c/z42c.semantics/src/FunctionEmitter.z42` | MODIFY | EmitStmt 入口 TrackLine + 出 IrFunction 时带表 |
| `src/z42c/z42c.semantics/src/ExprEmitter.z42` | MODIFY | （如需）表达式级行跟踪对齐 C# |
| `src/z42c/z42c.semantics/src/IrGen.z42` | MODIFY | source file 名穿线（basename） |
| `src/z42c/z42c.ir/src/IrModule.z42` | MODIFY | IrLineEntry/LocalVarEntry + IrFunction 加两表 |
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcWriter.z42` | MODIFY | DBUG section + flags.HasDebug + intern 行表串 |
| `src/z42c/z42c.ir/src/BinaryFormat/ZbcFormat.z42` | MODIFY | （如需）DBUG 相关常量 |
| `src/z42c/z42c.semantics/tests/zbc/zbc_tests.z42` | MODIFY | + 有语句体函数 byte-identical golden（对 C# fixture） |
| `src/z42c/z42c.syntax/tests/parser/parser_tests.z42` | MODIFY | + span 断言用例 |
| `scripts/xtask_compiler_z42.z42` | MODIFY | e2e 加 byte-compare 步（z42c vs C# 同源 .zbc diff） |
| `src/z42c/z42c.ir/README.md` / `src/z42c/z42c.syntax/README.md` / `src/z42c/z42c.semantics/README.md` | MODIFY | 同步 |
| `docs/design/compiler/self-hosting.md` | MODIFY | DBUG 落地后状态更新 |

**只读引用**：C# `src/compiler/z42.Semantics/Codegen/FunctionEmitter.Helpers.cs`（TrackLine/BaseName 权威）、`z42.IR/BinaryFormat/ZbcWriter.cs`（BuildDbugSection）、`z42c.core/src/Span.z42`（已有 Span 类型，不改）。

## Out of Scope

- C# 编译器 / runtime / stdlib 任何改动（纯 z42c 侧镜像）
- LocalVarTable 之外的调试体验（断点/单步——VM 侧已有）
- 插值串/lambda 等 z42c 前端未接特性的 span
- `--dump-ast` 输出格式变化（Dump 不打印 span，既有断言全保）

## Open Questions

- [ ] Q1：Span 放节点**尾参**（镜像 C# `Span Span` 末位参数约定）还是独立 `SetSpan`？（推荐尾参，见 design D1）
- [ ] Q2：Bound 层是逐节点携带（镜像 C#）还是只在 BoundStmt 携带（DBUG 最小需求）？（推荐逐节点，见 design D2）
