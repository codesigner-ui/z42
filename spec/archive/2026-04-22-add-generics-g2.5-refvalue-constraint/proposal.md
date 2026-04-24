# Proposal: L3-G2.5 引用/值类型约束（`where T: class` / `struct`）

## Why

L3-G2.5 基类约束之后，补齐两个最常用的"分类"约束：

```z42
// 限 T 为引用类型（可为 null / 可作为 Object 基类方法使用）
T Default<T>() where T: class { return null; }

// 限 T 为值类型（允许视作 non-null / 内联存储）
void LogValue<T>(T v) where T: struct { Console.WriteLine(v); }
```

与 C# 语义一致，Rust 无对应（Rust 通过 Sized / Copy 等 trait bounds 表达类似概念，范式不同）。

## What Changes

- **语法**：`where T: class` / `where T: struct`（关键字 token `class`/`struct` 已存在，Parser 识别）
- **AST**：
  - `GenericConstraint.Constraints: List<TypeExpr>` 保持；额外用特殊 NamedType("class") / NamedType("struct") 承载？**不**这样做 — 太易混淆
  - 新增 `GenericConstraintKind` enum + 改 `GenericConstraint` 结构为 `(TypeParam, Kinds, TypeConstraints, Span)`，其中 `Kinds` 为 flag 集合
- **TypeCheck**：
  - `GenericConstraintBundle` 新增 `RequiresClass` / `RequiresStruct` flag
  - 体内：无直接影响（访问受 interface/基类约束驱动；flag 只影响调用点校验）
  - 调用点校验：typeArg 必须满足 flag（`class` → 引用类型；`struct` → 值类型）
  - 互斥：同一参数不能同时 `class` 和 `struct`
- **IrGen / VM**：无改动
- **stdlib**：无改动

## Scope

| 文件/模块 | 变更 |
|----------|------|
| `z42.Syntax/Parser/Ast.cs` | `GenericConstraint` 扩展 `Kinds: GenericConstraintKind`；新增 enum |
| `z42.Syntax/Parser/TopLevelParser.Helpers.cs` | `ParseWhereClause` 识别 `class` / `struct` keyword |
| `z42.Semantics/TypeCheck/Z42Type.cs` | `GenericConstraintBundle` 新增 `RequiresClass` / `RequiresStruct` |
| `z42.Semantics/TypeCheck/TypeChecker.cs` | `ResolveWhereConstraints` 将 flag 写入 bundle；`ValidateGenericConstraints` 校验 flag |
| `z42.Tests/TypeCheckerTests.cs` | 6 新用例 |
| `src/runtime/tests/golden/errors/30_generic_class_violation/` | `F<int>` 违反 `where T: class` |
| `src/runtime/tests/golden/errors/31_generic_struct_violation/` | `F<MyClass>` 违反 `where T: struct` |
| `docs/design/generics.md` | L3-G2.5 补充 |
| `docs/roadmap.md` | 表格状态更新 |

## Out of Scope

- `where T: notnull`：单独小迭代（与 z42 可空性语义交互更广）
- `where T: new()`：依赖 VM 运行时类型参数（L3-G3a）
- `where T: U`（裸类型参数）：低优先级，单独迭代
- 约束元数据写入 zbc：L3-G3a 一次性

## Open Questions

- [ ] z42 中"值类型"的边界：int/bool/float/double 这些基本类型 + `isStruct=true` 的 class 节点，但不是所有 Z42PrimType 都是值类型（string 是引用类型）
  - **决策**：复用 `Z42Type.IsReferenceType` 已有定义；`struct` 约束 = `!IsReferenceType(t)`
- [ ] `class` 和 `struct` 能否与 interface 约束组合？`where T: class + IDisplay` — 可以。`where T: struct + IDisplay` — 也可以
- [ ] `class` 能否与基类约束组合？`where T: Animal + class` — 冗余但可解析，以基类约束为准（class 隐含满足）
