# Tasks: L3-G4a 泛型类实例化类型替换（call-site type substitution）

> 状态：🟢 已完成 | 创建：2026-04-22 | 完成：2026-04-22 | 类型：refactor+feat（纯 TypeChecker，无 IR/VM/zbc 改动）

**变更说明**：补全 `Z42InstantiatedType`，使 `c.Get()` / `c.field` 在 `c: Container<Animal, Dog>` 时返回实际的 Animal/Dog 而非未替换的 T/U。

**原因**：L3-G1/G2.5 已让泛型类能定义、实例化、验证约束，但方法/字段的 **返回/访问类型** 未做 T→具体类型替换 — 用户无法有意义地使用泛型类返回值。这也是 L3-G4 stdlib 泛型化（`List<int> l; int x = l.Get(0)`）的前提。

**Scope**：纯 C# TypeChecker。无 IR / VM / zbc 格式改动。

**文档影响**：`docs/design/generics.md` 更新；`docs/roadmap.md` 新增 L3-G4a 状态。

## 阶段 1: Z42InstantiatedType + 解析

- [x] 1.1 `Z42Type.cs`: 新增 `Z42InstantiatedType(Z42ClassType Definition, IReadOnlyList<Z42Type> TypeArgs)` record
- [x] 1.2 `SymbolTable.ResolveGenericType`：`new Container<Animal, Dog>` 的 GenericType → Z42InstantiatedType（当 TypeArgs 非空且 TypeArgs.Count 匹配 ClassType.TypeParams）
- [x] 1.3 `Z42Type.IsReferenceType` / `IsAssignableTo` 处理新类型（引用类型 + 同 Definition + TypeArgs 相等 → 可赋）
- [x] 1.4 `dotnet build` 全绿

## 阶段 2: 成员/调用的类型替换

- [x] 2.1 `TypeChecker.cs` 新增 `SubstituteTypeParams(Z42Type, Dict<string, Z42Type>)` 助手（递归替换 Z42GenericParamType）
- [x] 2.2 `TypeChecker.Exprs.cs` BindMemberExpr：receiver 为 Z42InstantiatedType → 字段/方法签名替换后返回
- [x] 2.3 `TypeChecker.Calls.cs` 方法调用 on Z42InstantiatedType：CheckArgTypes 用替换后的 param 类型；返回类型替换
- [x] 2.4 `TypeChecker.Exprs.cs` BindNew：`new Container<Animal, Dog>(...)` → BoundNew.Type 为 Z42InstantiatedType；ctor 参数类型也替换
- [x] 2.5 `dotnet build` 全绿；无 L3-G1/G2.5 既有测试回归

## 阶段 3: 测试

- [x] 3.1 `TypeCheckerTests.cs`: 新用例（返回 T 替换为 int / Animal；字段访问；ctor 参数校验替换）
- [x] 3.2 Golden `run/73_generic_instantiated_type/`: Box<int>().Get() + 1 能工作
- [x] 3.3 更新 golden `run/72_generic_bare_typeparam`: 原本简化的用例可加回 Get()（T=Animal 可用）
- [x] 3.4 `dotnet test` 全绿
- [x] 3.5 `./scripts/test-vm.sh` 全绿

## 阶段 4: 文档 + 验证

- [x] 4.1 `docs/design/generics.md`: L3-G4a 落地细节
- [x] 4.2 `docs/roadmap.md`: L3-G4a 独立子项 + ✅
- [x] 4.3 全绿验证

## 备注

- 无 IR / VM / zbc 改动（代码共享不变，单一 IR 服务所有实例化）
- Z42InstantiatedType 仅在编译期存在，BoundNew → IR 时仍用未实例化 class_name（IR 层代码共享）
- primitive 类型作 TypeArg 暂不处理特殊 dispatch（L3-G4 primitive interface 单独做）
- 不涉及 zbc 格式（类型替换全在 TypeChecker 层）
