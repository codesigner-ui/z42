# Tasks: 架构审查近期改进（A3/A8/A9）

**变更说明：** 实施架构审查报告中的近期三项改进
**原因：** 防止状态污染（A3）、统一特性门控（A9）、防止模块合并冲突（A8）
**文档影响：** z42.Semantics/README.md 更新

> 状态：🟢 已完成 | 创建：2026-04-15

## 进度概览
- [x] A9: LanguageFeatures 传入 TypeChecker + IrGen
- [x] A8: merge_modules 幂等合并
- [x] A3: IrGen FunctionEmitter 拆分

## A9: LanguageFeatures 传入 TypeChecker + IrGen
- [x] 9.1 TypeChecker 构造函数接受 LanguageFeatures 参数
- [x] 9.2 IrGen 构造函数接受 LanguageFeatures 参数
- [x] 9.3 GoldenTests 传递 LanguageFeatures
- [x] 9.4 README.md 入口点文档更新

## A8: merge_modules 幂等合并
- [x] 8.1 merge_modules 对 ClassDesc 按 name 去重（first wins）
- [x] 8.2 merge_modules 对 Function 按 name 去重（first wins）
- [x] 8.3 修复 merge_tests.rs 编译错误（补全 type_registry/is_static/max_reg）
- [x] 8.4 添加 merge_deduplicates_classes_by_name 测试
- [x] 8.5 添加 merge_deduplicates_functions_by_name 测试

## A3: IrGen FunctionEmitter 拆分
- [x] 3.1 创建 FunctionEmitter.cs（13 个函数级字段 + 块管理 + 入口点）
- [x] 3.2 创建 FunctionEmitterStmts.cs（语句 emit，从 IrGenStmts.cs 迁移）
- [x] 3.3 创建 FunctionEmitterExprs.cs（表达式 emit，从 IrGenExprs.cs 拆分）
- [x] 3.4 创建 FunctionEmitterCalls.cs（调用 + 插值 + switch expr 拆分）
- [x] 3.5 IrGen.cs 精简为模块级状态 + 委托调用 FunctionEmitter
- [x] 3.6 删除 IrGenStmts.cs / IrGenExprs.cs
- [x] 3.7 更新 Semantics README.md

## 验证
- [x] V.1 dotnet build —— 0 错误 0 警告
- [x] V.2 cargo build —— 成功
- [x] V.3 dotnet test —— 395 passed
- [x] V.4 ./scripts/test-vm.sh —— 114 passed (interp 57 + jit 57)
