# Proposal: `typeof(T)` 返回 `Std.Type`（C2）

> 状态：🟢 已实施（待归档）｜创建：2026-06-09｜类型：lang（AST + TypeCheck + Codegen）+ vm
> 占用子系统：`compiler` + `runtime`（[ACTIVE.md](../ACTIVE.md)）
> **注**：原 DRAFT 走 parser-desugar（option B），实施时改为 **option A：完整编译期类型解析**（见 design.md Decision 1 的修订）——`port-z42c-codegen` 已归档（2026-06-09），IrGen 活跃区约束解除，可走干净的 AST 节点路线。

## Why

0.3.x C 主线 C2。反射 MVP（C1, archive/2026-06-09-add-reflection-mvp）让 `obj.GetType()` 返回 `Std.Type`，但 **`typeof(T)` 仍返回字符串**——`typeof(int)` 此前在 parser 阶段就被 desugar 成 `LitStrExpr("int")`。两条路径不一致：`obj.GetType().Name` 是 Type、`typeof(int)` 是 string。C2 让 `typeof(T)` 也返回 `Std.Type`，与 GetType 统一。

## What Changes

- `typeof(T)` 求值为 `Std.Type` 对象（而非字符串）。`typeof(int).Name == "int"`、`typeof(Foo).FullName`、`typeof(Foo).GetFields()` 等。
- **实现走完整编译期解析**：新增 `TypeofExpr` AST 节点 → TypeChecker 绑定为 `BoundTypeof`（结果类型解析为 `Std.Type` 类）→ FunctionEmitter 把目标类型 emit 成**限定名**（用户类经 `QualifyClassName` → `Demo.Point`），再发 `__typeof` builtin 调用。
- runtime `__typeof` builtin 复用反射 `make_type_from_name`：限定名命中主模块 `type_registry` → **带真句柄**的 Type（成员可枚举）；基础类型 → synthetic Type（规范化别名 i32→int）。

## 与原 DRAFT（parser desugar）的关键差异

| 维度 | 原 DRAFT（option B） | 实施（option A） |
|------|--------------------|-----------------|
| 路线 | parser 把 `typeof(T)` desugar 成 `Std.Type.__Of("name")` 调用 | `TypeofExpr` AST → `BoundTypeof` → FunctionEmitter codegen |
| 触及 | 仅 `ExprParser.Atoms.cs` | parser + TypeCheck + Codegen + 全套 BoundExpr visitor |
| 主模块用户类 | **name-only 退化**（无句柄，`GetFields()` 空）| **带真句柄**（emit 限定名 → `make_type_from_name` 命中主模块 registry → 成员可枚举）✓ |
| 解锁原因 | `port-z42c-codegen` 活跃，避开 IrGen | `port-z42c-codegen` 已归档，IrGen 约束解除 |

> option A 严格优于 B：编译期把用户类型解析成限定名传给运行时，消除了 B 的"主模块用户类型退化"已知限制（golden 验证 `typeof(Point).GetFields().Length == 2`）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Syntax/Parser/Ast.cs` | MODIFY | 新增 `TypeofExpr(TypeExpr Target, Span)` sealed record |
| `src/compiler/z42.Syntax/Parser/ExprParser.Atoms.cs` | MODIFY | `ParseTypeof`：返回 `TypeofExpr`（不再 desugar 成 LitStr）|
| `src/compiler/z42.Semantics/Bound/BoundExpr.cs` | MODIFY | 新增 `BoundTypeof(Z42Type Target, Z42Type Type, Span)` |
| `src/compiler/z42.Semantics/Bound/BoundExprVisitor.cs` | MODIFY | `VisitTypeof` 派发 + abstract 方法 + Walker 默认 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` | MODIFY | `TypeofExpr` case：解析目标类型 + 结果类型 `Type`（短名经 prelude 解析为 Std.Type 类）→ `BoundTypeof` |
| `src/compiler/z42.Semantics/TypeCheck/FlowAnalyzer.cs` | MODIFY | `VisitTypeof => default`（无子表达式）|
| `src/compiler/z42.Semantics/TypeCheck/ClosureEscapeAnalyzer.cs` | MODIFY | `VisitTypeof => default` |
| `src/compiler/z42.Semantics/Lowering/BoundExprRewriter.cs` | MODIFY | `VisitTypeof => t`（叶子，identity）|
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` | MODIFY | `VisitTypeof`：目标类型 → 限定名 ConstStr → `BuiltinInstr("__typeof")` |
| `src/compiler/z42.Pipeline/BoundDumper.cs` | MODIFY | `VisitTypeof` 打印 `BoundTypeof target=...` |
| `src/compiler/z42.Tests/BoundVisitorTests.cs` | MODIFY | SeenVisitor `VisitTypeof => Mark(t)` |
| `src/runtime/src/corelib/reflection.rs` | MODIFY | `builtin_typeof`：args[0] string → `make_type_from_name` |
| `src/runtime/src/corelib/mod.rs` | MODIFY | 注册 `("__typeof", reflection::builtin_typeof)` |
| `src/tests/types/typeof.z42` | MODIFY | golden：`typeof(int).Name`、`typeof(Point).GetFields()`、与 GetType 一致 |
| `docs/design/language/reflection.md` | MODIFY | typeof→Type 移出 Deferred；新增 `var x = obj.GetType()` 限制 |
| `docs/spec/changes/ACTIVE.md` | MODIFY | 释放 compiler+runtime 锁 |

**只读引用**：
- `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs::VisitDefault`（`default(T)` 先例：Alloc/Emit/Intern/QualifyClassName 用法）
- `src/compiler/z42.Semantics/TypeCheck/SymbolTable.cs::ResolveType`（NamedType 解析 + prim fallback）
- `src/runtime/src/corelib/reflection.rs::make_type_from_name`（复用）

## Out of Scope / 已知限制

- **`var x = obj.GetType()` 不带属性派发能力**：`var` 从 `GetType()` 的**导入返回类型**推断，该返回类型当前未解析为带属性的 Type 类 → `x.Name` 为 null。**显式 `Type x = obj.GetType()`**（注解直接解析为 Type 类）正常。属于反射导入签名解析的 pre-existing 限制，与 typeof 无关（typeof 的 `var tp = typeof(Point)` 正常，因 `BoundTypeof.Type` 已是解析好的 Std.Type 类）。记 reflection.md Deferred。
- 不引入新 IR 指令（`__typeof` 复用既有 BuiltinInstr lowering）。

## Open Questions

- 无（实施完成，golden + 1543 C# 测试全绿）。
