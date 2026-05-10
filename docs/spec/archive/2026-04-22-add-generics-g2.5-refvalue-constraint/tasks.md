# Tasks: L3-G2.5 引用/值类型约束（`where T: class` / `struct`）

> 状态：🟢 已完成 | 创建：2026-04-22 | 完成：2026-04-22

## 进度概览
- [x] 阶段 1: AST + Parser ✅
- [x] 阶段 2: TypeChecker ✅
- [x] 阶段 3: 测试 ✅
- [x] 阶段 4: 文档 + 验证 ✅

## 阶段 1: AST + Parser ✅

- [x] 1.1 `Ast.cs`: 新增 `GenericConstraintKind` `[Flags]` enum（None/Class/Struct）
- [x] 1.2 `Ast.cs`: `GenericConstraint` 加 `Kinds: GenericConstraintKind = None`
- [x] 1.3 `TopLevelParser.Helpers.cs`: `ParseWhereClause` + `ParseOneConstraint` 识别 `class` / `struct` keyword 转为 flag
- [x] 1.4 `dotnet build` 全绿

## 阶段 2: TypeChecker ✅

- [x] 2.1 `Z42Type.cs`: `Z42ClassType` 增加 `IsStruct: bool = false`
- [x] 2.2 `Z42Type.cs`: `GenericConstraintBundle` 增加 `RequiresClass` / `RequiresStruct`
- [x] 2.3 `SymbolCollector.Classes.cs`: 传入 `cls.IsStruct` 到 Z42ClassType（第二遍最终构造）
- [x] 2.4 `TypeChecker.cs` `ResolveWhereConstraints`：
    - 将 `GenericConstraint.Kinds` 翻译到 bundle flag
    - 互斥校验：`class + struct` 同时 → 报错
- [x] 2.5 `TypeChecker.cs` `ValidateGenericConstraints`：
    - `IsClassArg` / `IsStructArg` helper
    - 分别校验 RequiresClass / RequiresStruct
- [x] 2.6 `dotnet build` 全绿；L3-G2 / G2.5 基类测试零回归（487/487）

## 阶段 3: 测试 ✅

- [x] 3.1 TypeCheckerTests.cs: 6 个新用例
    - Generic_ClassConstraint_Reference_Ok
    - Generic_ClassConstraint_Primitive_Error
    - Generic_StructConstraint_Primitive_Ok
    - Generic_StructConstraint_RefType_Error
    - Generic_ClassAndStruct_Exclusive_Error
    - Generic_ClassAndInterface_Combo_Ok
- [x] 3.2 Error goldens：
    - `errors/30_generic_class_violation`
    - `errors/31_generic_struct_violation`
    - `errors/32_generic_class_struct_exclusive`
- [x] 3.3 `dotnet test` 496/496 ✅（487 + 6 TC + 3 error goldens）
- [x] 3.4 `./scripts/test-vm.sh` 136/136 ✅（无变化 — 纯编译期校验）

## 阶段 4: 文档 + 验证 ✅

- [x] 4.1 `docs/design/generics.md`: L3-G2.5 引用/值约束小节
- [x] 4.2 `docs/roadmap.md`: class ✅ / struct ✅
- [x] 4.3 全绿验证：dotnet build + cargo build + dotnet test + test-vm.sh

### Spec 覆盖矩阵

| Scenario | 实现位置 | 验证方式 |
|---|---|---|
| class 约束通过 | IsClassArg | TC Generic_ClassConstraint_Reference_Ok |
| class + interface 组合 | 成员查找走接口 | TC Generic_ClassAndInterface_Combo_Ok |
| 值类型违反 class | IsClassArg 返回 false | TC Generic_ClassConstraint_Primitive_Error, errors/30 |
| struct 约束通过（primitive） | IsStructArg Z42PrimType 分支 | TC Generic_StructConstraint_Primitive_Ok |
| struct 约束通过（用户 struct） | IsStructArg Z42ClassType.IsStruct 分支 | 由 Z42ClassType.IsStruct 覆盖 |
| 引用类型违反 struct | IsStructArg 返回 false | TC Generic_StructConstraint_RefType_Error, errors/31 |
| 互斥报错 | ResolveWhereConstraints | TC Generic_ClassAndStruct_Exclusive_Error, errors/32 |
| class + interface 组合 | 校验路径并列 | TC |
| struct + interface 组合 | 校验路径并列 | spec 已定义 |
| 基类与 class 混用 | 冗余但允许 | spec 已定义 |

## 备注

- `Z42ClassType` 加 `IsStruct` 字段：1 处构造点（SymbolCollector.Classes.cs 第二遍），其他构造点默认值 false 不影响
- `Z42Type.IsReferenceType` 已把 string 归为引用类型，因此 `where T: struct` 自动拒绝 string
- `struct` + `interface` 组合未做特殊处理（如 primitive 实现 interface 需要 L3-G4 stdlib 泛型化）；当前 stdlib 无 primitive interface 实现 → 实际可用性有限，但语法和语义一致
