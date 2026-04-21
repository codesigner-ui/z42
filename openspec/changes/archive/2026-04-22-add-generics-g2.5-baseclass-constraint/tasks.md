# Tasks: L3-G2.5 基类约束（`where T: BaseClass`）

> 状态：🟢 已完成 | 创建：2026-04-22 | 完成：2026-04-22

## 进度概览
- [x] 阶段 1: Z42GenericParamType 重构 + 基类约束解析 ✅
- [x] 阶段 2: 成员查找 + 调用点校验扩展 ✅
- [x] 阶段 3: 测试 ✅
- [x] 阶段 4: 文档 + 验证 ✅

## 阶段 1: Z42GenericParamType 重构 + 基类约束解析 ✅

- [x] 1.1 `Z42Type.cs`: `Z42GenericParamType.Constraints` 拆分为 `InterfaceConstraints` + `BaseClassConstraint`
- [x] 1.2 `Z42Type.cs`: 新增 `GenericConstraintBundle(BaseClass?, Interfaces)` 统一载体
- [x] 1.3 `SymbolTable.cs`: `LookupActiveTypeParamConstraints` 返回 `GenericConstraintBundle`；`MakeTypeParam` 封装
- [x] 1.4 `TypeChecker.cs` `ResolveWhereConstraints`：接受 Z42ClassType 或 Z42InterfaceType；校验"基类唯一" + "基类首位"
- [x] 1.5 `TypeChecker.cs` `_funcConstraints` / `_classConstraints` value 类型 → `GenericConstraintBundle`
- [x] 1.6 `dotnet build` 全绿；L3-G2 8 个测试零回归

## 阶段 2: 成员查找 + 调用点校验扩展 ✅

- [x] 2.1 `TypeChecker.Exprs.cs` BindMemberExpr：受约束 T 先查基类字段/方法，后查接口；字段存储的 T 兜底查 active scope
- [x] 2.2 `TypeChecker.Calls.cs` 方法调用：同上；基类方法命中 → VCall 用基类名作 ReceiverClass
- [x] 2.3 `TypeChecker.cs` `TypeSatisfiesInterface` 保留；新增 `TypeSatisfiesClassConstraint`（用 `IsSubclassOf`）
- [x] 2.4 `ValidateGenericConstraints`：循环校验 bundle.BaseClass + bundle.Interfaces
- [x] 2.5 `dotnet build` 全绿

## 阶段 3: 测试 ✅

- [x] 3.1 TypeCheckerTests.cs: TC9-TC15（7 个新用例）
    - Generic_BaseClass_FieldAccess_Ok / MethodCall_Ok / AndInterface_Combo_Ok
    - Generic_CallSite_SubclassSatisfies_Ok / SiblingClass_Error
    - Generic_MultipleBaseClasses_Error / BaseClassNotFirst_Error
- [x] 3.2 Golden run `71_generic_baseclass/`：Animal + Dog + `Introduce<T>` 打印 legs + Describe → `4\ndog`
- [x] 3.3 Error golden `28_generic_non_subclass`：F<Vehicle> 违反 Animal 约束
- [x] 3.4 Error golden `29_generic_baseclass_not_first`：基类非首位
- [x] 3.5 `dotnet test` 487/487 ✅
- [x] 3.6 `./scripts/regen-golden-tests.sh` 更新 zbc
- [x] 3.7 `./scripts/test-vm.sh` 136/136 ✅（interp 68 + jit 68）

## 阶段 4: 文档 + 验证 ✅

- [x] 4.1 `docs/design/generics.md`: L3-G2.5 基类约束小节
- [x] 4.2 `docs/roadmap.md`: L3-G 进度表 G2.5 → 🟡（基类 ✅ / 其他范式 📋）
- [x] 4.3 `dotnet build` + `cargo build` 全绿
- [x] 4.4 `dotnet test` 487/487 ✅
- [x] 4.5 `./scripts/test-vm.sh` 136/136 ✅
- [x] 4.6 Spec 10 个 scenario 全覆盖

### Spec 覆盖矩阵

| Scenario | 实现位置 | 验证方式 |
|---|---|---|
| 单基类约束 | ResolveWhereConstraints | TC9 Generic_BaseClass_FieldAccess_Ok |
| 基类 + 接口组合 | 同上 | TC11 Generic_BaseClassAndInterface_Combo_Ok |
| 多基类报错 | ResolveWhereConstraints (多 Z42ClassType 分支) | TC14 Generic_MultipleBaseClasses_Error |
| 基类不在首位报错 | 同上 | TC15 Generic_BaseClassNotFirst_Error, errors/29 |
| 基类字段访问 | BindMemberExpr bundle.BaseClass.Fields | TC9, golden run/71 |
| 基类方法调用 | BindCall bundle.BaseClass.Methods | TC10, golden run/71 (Describe) |
| 基类方法 + 接口方法优先级 | 查找顺序：BaseClass → Interfaces | TC11 |
| 子类满足约束 | TypeSatisfiesClassConstraint + IsSubclassOf | TC12 |
| 同类满足约束 | TypeSatisfiesClassConstraint 同名分支 | TC12 (Animal) |
| 非子类违反约束 | 同上 | TC13, errors/28 |

## 备注

- 拆字段重构：`Constraints: List<Z42InterfaceType>?` → `InterfaceConstraints + BaseClassConstraint`（分别精确表达 0..n 接口 / 0..1 基类）
- `LookupActiveTypeParamConstraints` 由返回 list 改为返回 bundle，调用点需迁移 — grep 覆盖到所有 2 个站点
- 基类方法调用命中后 BoundCall 的 ReceiverClass 用基类名，IR 里走 `vcall @BaseClass.Method`；运行时看 T 实际对象的 vtable，与单态化代码一致
- 用户问题"rust 约束父类"：Rust 无继承，无此概念；C# 有 `where U: T`（裸类型参数）对应 → 已登记到 roadmap 低优先级范式
