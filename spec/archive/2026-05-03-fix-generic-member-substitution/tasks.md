# Tasks: 修复 generic 实例 member 链式 substitution

> 状态：🟢 已完成（INVESTIGATED — 无需实施） | 创建：2026-05-03 | 完成：2026-05-03
> **结论**：原假定的 substitution propagation bug 实为 Bug 1 (parse) + Bug 3 (equality) 的下游症状；Spec 1 + Spec 2 落地后用例自动通过。仅添加回归测试，不改 production code。

## 进度概览
- [x] 阶段 1: 探索 — 验证 substitution 是否真的有 bug
- [x] 阶段 2: 测试（保留作为回归防御）
- [x] 阶段 3: 验证 + 文档同步 + 归档

## 阶段 1: 探索
- [x] 1.1 阅读 BindMemberExpr 现实现（line 17-60，Z42InstantiatedType 分支正常）
- [x] 1.2 阅读 BuildSubstitutionMap / SubstituteTypeParams / ElemTypeOf
- [x] 1.3 写最小化测试用例覆盖原 D2b 失败模式
- [x] 1.4 全部测试通过 → 无 bug 触发
- [x] 1.5 复盘根因 → Bug 1 (parse) 让类型降级为 Z42ClassType，看似 substitution 失败实为 parse 失败
- [x] 1.6 决定不实施 production 改动；按"实现方案原则"无 bug 不补防御性代码

## 阶段 2: 测试（保留）
- [x] 2.1 NEW `src/compiler/z42.Tests/GenericMemberAccessTests.cs`（7 个测试）
  - `Array_InstantiatedElement_FieldAccess`
  - `Array_InstantiatedElement_ChainedFieldMethod`
  - `Array_NestedInstantiatedElement_FieldAccess`
  - `Direct_Instantiated_FieldAccess_Regression`
  - `Direct_Instantiated_MethodCall_Regression`
  - `D2b_ExactPattern_SubSlot_With_ActionT` —— D2b 原报错精确模式
  - `Interface_Field_TypeArg_Substitution`

## 阶段 3: 验证 + 文档 + 归档
- [x] 3.1 `dotnet build src/compiler/z42.slnx` ✅
- [x] 3.2 `dotnet test`：944+7=951/951 ✅
- [x] 3.3 `./scripts/test-vm.sh` 全绿（不受影响）
- [x] 3.4 `./scripts/build-stdlib.sh` 6/6 绿（不受影响）
- [x] 3.5 spec proposal/design 标 INVESTIGATED 状态
- [x] 3.6 移动 `spec/changes/fix-generic-member-substitution/` → `spec/archive/2026-05-03-fix-generic-member-substitution/`
- [x] 3.7 commit + push
- [x] 3.8 **D2b 解封通知**：D2b `tasks.md` 顶部 🛑 BLOCKED 改回 🟡 进行中，重新开始阶段 2

## 备注
- 三个原假定 bug 实为两个：parse + equality；substitution 一直工作正确
- 7 个测试保留作为防御性回归（防止未来 substitution / parse / equality 任何一处 regression）
- D2b 解封 — 三处生 generic 系统问题已修齐
- `SubstituteTypeParams` 对 Z42InterfaceType 的 fall-through 行为暂保留 ——
  实践中 InterfaceType.TypeArgs 在 SymbolTable.ResolveType 阶段已 substitute；
  如未来发现真实场景下漏 substitute 再开 spec 加分支
