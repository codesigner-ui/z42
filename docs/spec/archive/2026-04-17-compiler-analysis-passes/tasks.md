# Tasks: 编译器分析 Pass — 可达性 + 确定赋值 + IrGen 去重

> 状态：🟢 已完成 | 创建：2026-04-17 | 完成：2026-04-17
> 变更类型：refactor（增强 TypeChecker 语义分析，不改语言语义）

## 变更说明

1. **可达性分析**：非 void 函数所有路径必须 return，否则报 E0403
2. **确定赋值分析**：变量在所有前驱路径上赋值后才可读，否则报错
3. **IrGen 去 AST 重遍历**：消费 SemanticModel 替代从 CompilationUnit 重建 7 个字典

## 任务清单

### 任务 1：可达性分析 + MissingReturn
- [ ] 1.1 TypeChecker 新增 `CheckReturns(BoundBlock, Z42Type retType)` 方法
- [ ] 1.2 对非 void 函数调用，检查所有路径是否到达 return
- [ ] 1.3 发射 `E0403 MissingReturn` 错误
- [ ] 1.4 新增 golden error test 覆盖

### 任务 2：确定赋值分析
- [ ] 2.1 TypeChecker 新增 `CheckDefiniteAssignment(BoundBlock)` 或在 BindStmt 中追踪
- [ ] 2.2 对声明无初始化的局部变量，追踪是否在所有路径上赋值
- [ ] 2.3 读取未确定赋值的变量时报错
- [ ] 2.4 新增 golden error test 覆盖

### 任务 3：IrGen 消费 SemanticModel
- [ ] 3.1 从 SemanticModel 读取 class methods/fields/base 替代 AST 遍历
- [ ] 3.2 从 SemanticModel 读取 enum constants、function params 替代 AST 遍历
- [ ] 3.3 删除 IrGen.Generate() 中的 AST 遍历代码
- [ ] 3.4 确保 396 + 114 测试全绿

### 验证
- [ ] 4.1 `dotnet build` —— 无编译错误
- [ ] 4.2 `dotnet test` —— 全绿
- [ ] 4.3 `./scripts/test-vm.sh` —— 全绿

## 文档影响
无（纯内部分析增强，不改语言语义）
