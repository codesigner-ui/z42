# Proposal: Add Default Parameter Values

## Why
`examples/oop.z42` 和 OOP 风格代码广泛使用 C# 默认参数（`void Draw(string color = "black")`）。
当前编译器在遇到 `=` 时报 parse error，导致所有含默认值的接口/方法定义无法编译。
Phase 1 目标是 C# 9-12 语法全覆盖，默认参数是其中不可缺少的一部分。

## What Changes
- `Param` AST 节点增加 `Expr? Default` 字段
- Parser：`ParseParamList` 识别 `type ident = expr` 形式
- TypeChecker：
  - 签名收集时记录参数默认值类型
  - 调用点检查允许省略尾部有默认值的参数
- IrGen：调用点补全省略的参数（将默认值 expr codegen 插入参数列表）
- 无 VM 变更：默认值在编译期展开，IR 层看到的始终是完整参数列表

## Scope（允许改动的文件/模块）
| 文件/模块 | 变更类型 | 说明 |
|-----------|---------|------|
| `Parser/Ast.cs` | 修改 | `Param` 增加 `Expr? Default` |
| `Parser/TopLevelParser.cs` | 修改 | `ParseParamList` 解析 `= expr` |
| `TypeCheck/Z42Type.cs` | 修改 | `Z42FuncType.Params` → 携带默认值信息 |
| `TypeCheck/TypeChecker.cs` | 修改 | 签名收集时存 default expr 类型 |
| `TypeCheck/TypeChecker.Exprs.cs` | 修改 | 调用点参数计数允许 >= 必填数 |
| `Codegen/IrGenExprs.cs` | 修改 | call site 补全省略参数 |
| `z42.Tests/TypeCheckerTests.cs` | 新增 | 默认值场景测试 |
| `z42.Tests/IrGenTests.cs` | 新增 | 调用点补全测试 |

## Out of Scope
- 命名参数（`Draw(color: "red")`）—— Phase 1 不做
- `params` 变长参数 —— Phase 1 不做
- 默认值为复杂表达式（new、方法调用）—— Phase 1 支持字面量和简单表达式

## Open Questions
- 无
