# Tasks: `typeof(T)` 返回 `Std.Type`（C2）

> 状态：🟢 已完成（待归档）｜创建：2026-06-09｜类型：lang(AST+TypeCheck+Codegen) + vm
> 占用子系统：`compiler` + `runtime`（ACTIVE.md；**不占 stdlib**——option A 无 Type.z42 改动）
> **路线修订**：DRAFT 为 parser-desugar（option B）；实施改 option A（完整编译期解析），因 port-z42c-codegen 已归档解除 IrGen 约束。

## 进度概览
- [x] 阶段 1: AST + 全套 BoundExpr visitor 骨架
- [x] 阶段 2: TypeCheck + Codegen
- [x] 阶段 3: runtime builtin
- [x] 阶段 4: 测试 + 验证
- [x] 阶段 5: 文档 + 归档

## 阶段 1: AST + visitor 骨架
- [x] 1.1 `Ast.cs`：新增 `TypeofExpr(TypeExpr Target, Span)` sealed record
- [x] 1.2 `ExprParser.Atoms.cs::ParseTypeof`：返回 `TypeofExpr`（不再 desugar 成 LitStr）
- [x] 1.3 `BoundExpr.cs`：新增 `BoundTypeof(Z42Type Target, Z42Type Type, Span)`
- [x] 1.4 `BoundExprVisitor.cs`：`VisitTypeof` 派发 + abstract + Walker 默认
- [x] 1.5 全套 visitor 加 `VisitTypeof`：FlowAnalyzer / ClosureEscapeAnalyzer（`=> default`）、BoundExprRewriter（`=> t`）、BoundDumper、BoundVisitorTests.SeenVisitor

## 阶段 2: TypeCheck + Codegen
- [x] 2.1 `TypeChecker.Exprs.cs`：`TypeofExpr` case → 解析 target + 结果类型 `Type`（短名，prelude 解析为 Std.Type 类）→ `BoundTypeof`
- [x] 2.2 `FunctionEmitterExprs.cs::VisitTypeof`：目标类型 → 限定名（用户类 `QualifyClassName`）ConstStr → `BuiltinInstr("__typeof")`
- [x] 2.3 `dotnet build src/compiler/z42.slnx` 0 error

## 阶段 3: runtime
- [x] 3.1 `corelib/reflection.rs`：`builtin_typeof(ctx, args)` —— args[0] string → `make_type_from_name`
- [x] 3.2 `corelib/mod.rs`：注册 `("__typeof", reflection::builtin_typeof)`
- [x] 3.3 `cargo build`（debug + release）编过

## 阶段 4: 测试 + 验证
- [x] 4.1 `src/tests/types/typeof.z42`：基础类型 `.Name`、用户类 `GetFields().Length==2`、与 GetType 一致（显式 `Type` 注解）
- [x] 4.2 `dotnet test`（C# GoldenTests 权威）：238/238 golden、1543/1543 全量
- [x] 4.3 `xtask test`（vm / cross-zpkg / lib）—— 刷新 `.z42/bin/z42vm` 至带 `__typeof` 的 fresh release
- [x] 4.4 spec scenarios 逐条确认

## 阶段 5: 文档 + 归档
- [x] 5.1 `docs/design/language/reflection.md`：typeof→Type 移出 Deferred + 文档化；新增 `var x = obj.GetType()` 限制（reflection-future-gettype-var-inference）
- [x] 5.2 proposal/design/spec/tasks 改写为 option A
- [x] 5.3 归档 + ACTIVE.md 释放 compiler+runtime + commit + push

## 备注
- **路线变更（DRAFT B → 实施 A）**：port-z42c-codegen 2026-06-09 归档，IrGen 约束解除，User 裁决走 option A。option A 编译期 emit 限定名 → 用户类带真句柄，消除 option B 的 name-only 退化限制。
- **不占 stdlib 锁**：option A 直接 emit `__typeof` builtin，无 `Std.Type.__Of` extern，无 Type.z42 改动。
- **新限制**（非 typeof 引入，pre-existing 反射）：`var x = obj.GetType()` 的 var 推断不带属性派发（GetType 导入返回类型未解析为 Type 类）；显式 `Type x = obj.GetType()` 正常。记 reflection.md Deferred。
