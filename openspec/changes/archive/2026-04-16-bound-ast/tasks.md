# Tasks: A4 Bound AST — 表达式节点携带类型信息

> 状态：🟢 已完成 | 创建：2026-04-15 | 完成：2026-04-16
> 变更类型：refactor（不改变语言语义 / IR 格式 / golden tests）

## 变更说明

引入 `BoundExpr` / `BoundStmt` 层次结构，TypeChecker 输出携带类型的 Bound 节点，
FunctionEmitter 直接消费 BoundBlock，消除 ExprTypes 字典查找和 _classInstanceVars 启发式。

## 设计决策（已确认）

- `BoundLitInt` 类型：传入 `expectedType?`，若为具体整数类型则使用，否则 Int/Long 按值范围
- `BoundCallKind`：Free / Static / Instance / Virtual / Unresolved（stdlib/builtin 保留在 IrGen 解析）
- `_classInstanceVars`：完全移除，替换为 `BoundVarDecl.VarType is Z42ClassType`
- `FillDefaults`：保留在 FunctionEmitter，通过 `_gen._funcParams` 补全默认参数
- Golden tests：全部不变（IR 语义不变）

## 任务清单

### 阶段 1：新建 Bound 类型

- [x] 1.1 新建 `z42.Semantics/Bound/BoundExpr.cs`（22 种 BoundExpr + BoundCallKind）
- [x] 1.2 新建 `z42.Semantics/Bound/BoundStmt.cs`（14 种 BoundStmt）

### 阶段 2：TypeChecker 改造

- [x] 2.1 `TypeChecker.Exprs.cs`：`CheckExprCore` → `BindExpr`（返回 BoundExpr）
- [x] 2.2 `TypeChecker.Stmts.cs`：`CheckStmt/Block` → `BindStmt/Block`（返回 BoundStmt/Block）
- [x] 2.3 `TypeChecker.cs`：移除 `_exprTypes`，`Check()` 填充 `_boundBodies`
- [x] 2.4 `SemanticModel.cs`：移除 `ExprTypes`，新增 `BoundBodies`

### 阶段 3：FunctionEmitter 改造

- [x] 3.1 `FunctionEmitter.cs`：入口改为接收 `BoundBlock`，移除 `_classInstanceVars`
- [x] 3.2 `FunctionEmitterExprs.cs`：`EmitExpr(Expr)` → `EmitExpr(BoundExpr)`
- [x] 3.3 `FunctionEmitterStmts.cs`：`EmitStmt(Stmt)` → `EmitStmt(BoundStmt)`
- [x] 3.4 `FunctionEmitterCalls.cs`：适配 `BoundCall`

### 阶段 4：IrGen 适配

- [x] 4.1 `IrGen.cs`：`EmitMethod/EmitFunction` 改为传 `BoundBlock` 给 FunctionEmitter

### 阶段 5：文档 & 验证

- [x] 5.1 更新 `z42.Semantics/README.md`（新增 Bound/ 目录说明）
- [x] 5.2 `dotnet build` —— 无编译错误
- [x] 5.3 `dotnet test` —— 全绿（396/396）
- [x] 5.4 `./scripts/test-vm.sh` —— 全绿（114/114）

## 文档影响

无（纯内部重构，不改变外部可见行为）
