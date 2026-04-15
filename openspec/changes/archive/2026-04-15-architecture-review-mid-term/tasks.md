# Tasks: A1 + A7 架构审查中期改进

> 状态：🟢 已完成 | 创建：2026-04-15 | 完成：2026-04-15
> 变更类型：refactor（不改变语言语义，改善编译器内部架构）

## 变更说明

**A1（引入 SemanticModel）**：TypeChecker.Check() 由 void 改为返回 SemanticModel，包含
类型表（Classes/Funcs/EnumConstants）和 ExprTypes（Expr → Z42Type 映射）。
IrGen 接收 SemanticModel 作为可选构造参数。

**A7（IrGen 调用解析利用静态类型）**：用 SemanticModel.ExprTypes 改进
FunctionEmitter.IsReceiverClassInstance()，使其能正确识别「以参数形式传入的类实例」
（当前 _classInstanceVars 启发式仅识别以 new Expr 初始化的变量，会错误地对
类实例调用集合 builtin）。

## 文档影响
无（纯内部重构，不改变外部可见行为）

## 任务清单

- [x] 1.1 创建 openspec/changes/architecture-review-mid-term/tasks.md
- [x] 1.2 新建 `z42.Semantics/TypeCheck/SemanticModel.cs`
- [x] 1.3 修改 `TypeChecker.cs`：添加 `_exprTypes`，`Check()` 返回 `SemanticModel`
- [x] 1.4 修改 `TypeChecker.Exprs.cs`：添加 CheckExpr 包装保存 ExprTypes
- [x] 1.5 修改 `IrGen.cs`：构造函数接收 `SemanticModel?`，存为 `_semanticModel`
- [x] 1.6 修改 `FunctionEmitter.cs`：IsReceiverClassInstance 使用 SemanticModel.ExprTypes
- [x] 1.7 修改调用方：GoldenTests.cs、SingleFileCompiler.cs、PackageCompiler.cs
- [x] 2.1 dotnet build —— 无编译错误
- [x] 2.2 dotnet test —— 全绿（396/396）
- [x] 2.3 ./scripts/test-vm.sh —— 全绿（114/114，interp+jit）

## 备注

A7 的核心价值：
- 修复「类实例作为参数时 IsReceiverClassInstance 返回 false」的 bug
  → circle.Contains(pt) 当 circle 是参数型 Circle 时，当前错误走 __contains 内建
  → 修复后正确走 VCallInstr 到用户定义的 Circle.Contains 方法
- 为未来 StdlibCallIndex 模糊消解提供基础设施（当前 stdlib 无实际歧义）

ExprTypes 实现策略：
- 使用 ReferenceEqualityComparer.Instance 键入，保证 AST 节点身份正确
- TypeChecker.CheckExpr → 改名为 CheckExprCore；新建 CheckExpr 包装记录结果
- 影响面：TypeChecker 对每个表达式多一次字典写入，内存 O(AST 节点数)
