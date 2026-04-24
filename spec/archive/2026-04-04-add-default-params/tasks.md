# Tasks: add-default-params

> 状态：🟢 已完成 | 创建：2026-04-04 | 完成：2026-04-04

## 进度概览
- [x] 阶段 1: Parser + AST
- [x] 阶段 2: TypeChecker
- [x] 阶段 3: IrGen + 测试
- [x] 阶段 4: 验证与归档

## 阶段 1: Parser + AST
- [x] 1.1 `Ast.cs` — `Param` 增加 `Expr? Default` 字段
- [x] 1.2 `TopLevelParser.cs` — `ParseParamList` 识别 `type ident = expr`

## 阶段 2: TypeChecker
- [x] 2.1 `TypeChecker.cs` — `BuildFuncType` 辅助方法，计算 `requiredCount`，统一三处签名构建
- [x] 2.2 `TypeChecker.cs` — 签名收集时对默认值 expr 做类型检查
- [x] 2.3 `TypeChecker.Exprs.cs` — call 检查：`argCount >= MinArgCount && argCount <= Params.Count`

## 阶段 3: IrGen + 测试
- [x] 3.1 `IrGen.cs` + `IrGenExprs.cs` — `_funcParams` 表 + `FillDefaults` 辅助，call site 自动补全
- [x] 3.2 `TypeCheckerTests.cs` — 新增 9 个 unit tests（默认参数 + C# 别名 + T?）
- [x] 3.3 `IrGenTests.cs` — 新增 3 个 call site 补全 unit tests
- [x] 3.4 `examples/types.z42` 修复（C# 别名变量名冲突 + nullable 支持验证）

## 阶段 4: 验证
- [x] 4.1 `dotnet build && dotnet test` — 360/360 ✅
- [x] 4.2 `./scripts/test-vm.sh` — 84/84 ✅
- [x] 4.3 `docs/design/language-overview.md` 补充默认参数语法

## 备注
- 默认值在 call site 展开，IR 函数签名不变
- 命名参数、`params` 变长参数不在此次 Scope
