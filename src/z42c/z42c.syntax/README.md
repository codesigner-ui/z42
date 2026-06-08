# z42c.syntax

## 职责
镜像 C# [z42.Syntax](../../compiler/z42.Syntax/README.md)：语法层（Lexer 词法 + Parser 语法 → AST）。**前端基本成型**：手写 Lexer + Pratt 表达式 + 递归下降语句/声明（class 继承 + virtual `Dump()` 出 s-expression，受限写法）。占位 `SyntaxSkeleton` 暂留（下游未移植子系统仍引用）。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/TokenKind.z42` | 147 个 token 类型常量（int；镜像 C# enum TokenKind）|
| `src/Token.z42` | 词法 token（Kind / Text / Span）|
| `src/Lexer.z42` | 手写词法器：trivia 跳过 + 标识符/关键字 + 数字（十进制/hex/bin + `_` 分隔 + 小数/指数 + 后缀）+ 字符串/字符/raw `"""`/插值 `$"` + 全符号最长匹配 + EOF；`DecodeString` 转义解码助手 |
| `src/TypeExpr.z42` | 类型表达式 AST（TypeExpr + virtual Dump；NamedType/ArrayType/NullableType；Dump 复刻规范 type-text）|
| `src/Ast.z42` | 表达式 AST（Expr + virtual Dump；字面量/标识符/一元/二元含位运算·??·is·as/成员/调用/索引/赋值·复合/三目/new；is·as·new 的类型为 TypeExpr）|
| `src/Stmt.z42` | 语句 AST（Stmt + virtual Dump；expr/var-decl/return/if/while/block/break/continue/throw/foreach/for/do-while/switch/try-catch-finally）|
| `src/Decl.z42` | 声明 AST（CompilationUnit/Using/Class·Struct·Interface[Kind 区分]/Enum+EnumMember/Record/Delegate/Field/Method[IsFree=顶层 func]/Property/Param/ParamList/Attr+AttributedDecl + Dump）|
| `src/Parser.z42` | Pratt 表达式（含后缀/赋值/三目/is·as/new）+ 递归下降语句（含 for/switch/try）+ 顶层声明（class·struct·interface/enum/record/delegate/顶层 func/field/method/ctor/property + 泛型形参 `<T>`/where + 前置 attribute `[X]`）|
| `src/SyntaxSkeleton.z42` | **过渡占位**：semantics/pipeline/driver 仍引用；各自移植时移除 |

## 入口点
`Z42.Syntax.Parser`（`new Parser(src,file)`）：`ParseExpression()` → `Expr` / `ParseStatement()` → `Stmt` / `ParseCompilationUnit()` → `CompilationUnit`（均 `.Dump()` 出 s-expression）；`Z42.Syntax.Lexer`：`Tokenize()` → `TokenCount()`/`TokenAt(i)`。
测试：`tests/{lexer 17, parser 20, stmt 15, decl 24}`（共 76），经 `xtask test compiler-z42`。
进行中（incr 6d）：TypeExpr AST 替代类型文本字符串——6d-1 已切 is/as/new/var-decl/foreach；6d-2 切声明位；6d-3 where 约束结构化。
待移植（incr 6e+）：byte-identical 对账；lambda；Visitor（并入后端）；转义 `\0`/`\uXXXX` 解码。

## 依赖关系
→ z42c.core。stdlib 自动可用。
