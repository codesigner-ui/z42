# Tasks: TypeChecker 职责分离

**变更说明：** 将 TypeChecker 流程分析独立为 FlowAnalyzer，创建 SymbolTable 显式化阶段边界
**原因：** 消除隐式耦合，为 Phase 2 泛型约束求解独立 pass 铺路
**文档影响：** z42.Semantics/README.md

> 状态：🟢 已完成 | 创建：2026-04-17 | 完成：2026-04-17

## 实施步骤

- [x] 1.1 提取 FlowAnalyzer（可达性分析 + 确定赋值分析，完全独立）
- [x] 1.2 创建 SymbolTable 数据类（readonly 快照：classes/interfaces/funcs/enums + 查询方法）
- [x] 1.3 Check() 显式化阶段边界：Pass 0（collect）→ SymbolTable 快照 → Pass 1（bind）
- [x] 1.4 IsSubclassOf / ImplementsInterface 委托给 SymbolTable
- [x] 1.5 验证全绿 + 文档同步

## 验证

- [x] dotnet build — 0 errors
- [x] dotnet test — 396 passed
- [x] test-vm.sh — 114 passed

## 备注

- BuildFuncType 在 Pass 0 中调用 BindExpr 处理默认值，Pass 0/1 存在交织
- 完整的类级别拆分（SymbolCollector / BodyBinder 独立类）留作未来迭代
- FlowAnalyzer 已完全独立，无 TypeChecker 状态依赖
