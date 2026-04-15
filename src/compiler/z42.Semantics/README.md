# z42.Semantics

## 职责
语义层：对 AST 进行类型检查，并将其降低（lower）为 IR 指令序列。

## 核心文件
| 文件 | 职责 |
|------|------|
| `TypeCheck/TypeChecker.cs` | 两遍类型检查器（Pass 0：收集类型形状；Pass 1：绑定函数体为 BoundBlock）|
| `TypeCheck/TypeChecker.Stmts.cs` | 语句绑定（`BindStmt/Block` 返回 BoundStmt/BoundBlock）|
| `TypeCheck/TypeChecker.Exprs.cs` | 表达式绑定（`BindExpr` 返回携带 Z42Type 的 BoundExpr）|
| `TypeCheck/SemanticModel.cs` | TypeChecker 输出：`BoundBodies: Dictionary<FunctionDecl, BoundBlock>` |
| `TypeCheck/Z42Type.cs` | 语义类型层次（`Z42IntType`、`Z42ClassType` 等）|
| `TypeCheck/TypeEnv.cs` | 词法作用域符号表 |
| `TypeCheck/BinaryTypeTable.cs` | 二元运算符类型规则表 |
| `Bound/BoundExpr.cs` | 携带类型的表达式绑定节点（25 种，含 BoundCallKind 枚举）|
| `Bound/BoundStmt.cs` | 绑定语句节点（14 种 BoundStmt + BoundBlock）|
| `Codegen/IrGen.cs` | 代码生成器：模块级状态、公开 API、函数分派 |
| `Codegen/FunctionEmitter.cs` | 函数级 IR 生成器：块管理、入口点、辅助方法 |
| `Codegen/FunctionEmitterStmts.cs` | 语句 + 控制流 IR 生成（分部类，消费 BoundStmt）|
| `Codegen/FunctionEmitterExprs.cs` | 表达式 IR 生成（分部类，消费 BoundExpr）|
| `Codegen/FunctionEmitterCalls.cs` | 调用 + 字符串插值 + switch 表达式 IR 生成（分部类）|
| `Codegen/FunctionEmitterRaw.cs` | 原始 AST 表达式发射器（用于静态字段初始化、默认参数）|

## 入口点
- `Z42.Semantics.TypeCheck.TypeChecker` — `new TypeChecker(diags, features).Check(cu)` → `SemanticModel`
- `Z42.Semantics.Codegen.IrGen` — `new IrGen(stdlibIndex, features, semanticModel).Generate(cu)` → `IrModule`

## 依赖关系
→ z42.Core（Diagnostics、Span、LanguageFeatures）
→ z42.Syntax（AST 节点、Lexer Token）
→ z42.IR（IrModule、指令类型）
