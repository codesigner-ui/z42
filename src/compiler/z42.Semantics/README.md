# z42.Semantics

## 职责
语义层：对 AST 进行类型检查，并将其降低（lower）为 IR 指令序列。

## 核心文件
| 文件 | 职责 |
|------|------|
| `TypeCheck/TypeChecker.cs` | 两遍类型检查器（Pass 0：收集类型形状；Pass 1：检查函数体）|
| `TypeCheck/TypeChecker.Stmts.cs` | 语句类型检查（分部类）|
| `TypeCheck/TypeChecker.Exprs.cs` | 表达式类型推断（分部类）|
| `TypeCheck/Z42Type.cs` | 语义类型层次（`Z42IntType`、`Z42ClassType` 等）|
| `TypeCheck/TypeEnv.cs` | 词法作用域符号表 |
| `TypeCheck/BinaryTypeTable.cs` | 二元运算符类型规则表 |
| `Codegen/IrGen.cs` | 代码生成器：模块级状态、公开 API、函数分派 |
| `Codegen/FunctionEmitter.cs` | 函数级 IR 生成器：块管理、入口点、辅助方法 |
| `Codegen/FunctionEmitterStmts.cs` | 语句 + 控制流 IR 生成（分部类）|
| `Codegen/FunctionEmitterExprs.cs` | 表达式 IR 生成（分部类）|
| `Codegen/FunctionEmitterCalls.cs` | 调用 + 字符串插值 + switch 表达式 IR 生成（分部类）|

## 入口点
- `Z42.Semantics.TypeCheck.TypeChecker` — `new TypeChecker(diags, features).Check(cu)`
- `Z42.Semantics.Codegen.IrGen` — `new IrGen(stdlibIndex, features).Generate(cu)`

## 依赖关系
→ z42.Core（Diagnostics、Span、LanguageFeatures）
→ z42.Syntax（AST 节点、Lexer Token）
→ z42.IR（IrModule、指令类型）
