# z42.Compiler — 编译器核心管线

## 职责

从源码字符串到 `IrModule` 的完整变换：Lexer → Parser → TypeChecker → Codegen。
只依赖 `z42.IR`，不依赖 `z42.Project` 或 `z42.Driver`。

## 管线概览

```
源码字符串
  ↓  Lexer/        规则驱动的 tokenizer（LexCombinators + TokenDefs）
Token 流
  ↓  Parser/       手写组合子 + Pratt 表达式解析 → AST（sealed record 节点）
CompilationUnit（AST）
  ↓  TypeCheck/    两遍检查：Pass 0 收集类型形状，Pass 1 检查函数体
类型检查后的 AST
  ↓  Codegen/      生成 SSA IrModule，含寄存器分配和块标签
IrModule
```

## 子目录与核心文件

### Lexer/
| 文件 | 职责 |
|------|------|
| `Token.cs` / `TokenKind.cs` | Token 结构体 + Kind 枚举 |
| `TokenDefs.cs` | 关键字表、符号映射等静态元数据 |
| `LexCombinators.cs` / `LexRules.cs` | 组合子：基础匹配原语 + 数字/字符串规则 |
| `Lexer.cs` | 通用执行引擎，调用 `Tokenize()` 返回 `List<Token>` |

### Parser/
| 文件 | 职责 |
|------|------|
| `Ast.cs` | 所有 AST 节点类型（`sealed record`） |
| `TopLevelParser.cs` | 顶层声明：命名空间、类、函数、枚举、接口 |
| `StmtParser.cs` | 语句：if/while/for/foreach/return/try-catch 等 |
| `ExprParser.cs` | Pratt 表达式解析（运算符优先级与结合性） |
| `TypeParser.cs` | 类型标注解析 |
| `Parser.cs` | 入口，返回 `CompilationUnit` |

### Parser/Core/
| 文件 | 职责 |
|------|------|
| `TokenCursor.cs` | 不可变 Token 流游标（`Peek`、`Advance`、`Expect`） |
| `ParseResult.cs` | `ParseResult<T>` — 解析结果 discriminated union |
| `Combinators.cs` | 通用组合子：`Many`、`Optional`、`SeparatedBy` 等 |

### TypeCheck/
| 文件 | 职责 |
|------|------|
| `TypeChecker.cs` | 入口 + Pass 0（收集枚举常量、类型形状、函数签名） |
| `TypeChecker.Stmts.cs` | Pass 1 语句检查（partial class） |
| `TypeChecker.Exprs.cs` | Pass 1 表达式类型推断（partial class） |
| `Z42Type.cs` | `Z42Type` 类型层次（Primitive/Class/Array/Nullable/Error） |
| `TypeEnv.cs` | 符号表：变量作用域、类型注册、函数签名查找 |
| `BinaryTypeTable.cs` | 二元运算符类型规则表 |
| `BuiltinTable.cs` | 内置函数签名表 |

### Codegen/
| 文件 | 职责 |
|------|------|
| `IrGen.cs` | 核心状态机、公开 API、基本块管理（`StartBlock`/`EndBlock`）、寄存器分配 |
| `IrGenStmts.cs` | 语句和控制流指令生成（partial class） |
| `IrGenExprs.cs` | 表达式求值和指令生成（partial class） |

### Diagnostics/
| 文件 | 职责 |
|------|------|
| `Diagnostic.cs` | 诊断数据类型：错误码、消息、位置、严重级别 |
| `DiagnosticBag.cs` | 收集器：`Add`、`HasErrors`、格式化输出 |
| `DiagnosticCatalog.cs` | 所有错误码集中定义（`Z42001`–`Z42xxx`） |

### Features/
| 文件 | 职责 |
|------|------|
| `LanguageFeatures.cs` | 特性枚举 + 各 profile（phase1/phase2）下的启用集合 |

## 设计约束

不引入外部 parser combinator 库（为最终自举保留）；错误通过 `DiagnosticBag` 累积，用 `Z42Type.Error` 哨兵继续检查而非中止。
