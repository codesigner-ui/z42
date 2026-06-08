# Tasks: port-z42c-syntax — z42c.syntax 真实移植

> 状态：🟡 进行中 | 创建：2026-06-08 | 子系统锁：z42c（见 ACTIVE.md，与 port-z42c-core 同属自举顺序续作）
> **变更说明：** 把 C# `z42.Syntax`（Lexer / Parser / AST）用 z42 重写，替换 SyntaxSkeleton 占位。
> **类型：** port（实现既有行为，受限写法）。架构见 [self-hosting.md](../../../design/compiler/self-hosting.md)。

## increment 1（Lexer — ✅ 已完成）
- [x] `TokenKind.z42`（全 147 token 常量，int；镜像 C# enum）
- [x] `Token.z42`（Kind/Text/Span）
- [x] `Lexer.z42`（手写扫描：trivia 跳过[空白/换行/`//`/`/* */`] + 标识符/关键字[全表 ~90] + int/float + 字符串/字符[raw lexeme] + 全符号最长匹配 + EOF；typed array 输出 + DiagnosticBag）
- [x] 10 单测（标识符/关键字/`_` token/int+float/`a.b` 非浮点/字符串/符号最长匹配/token 序列/注释跳过/行列追踪）
- [x] 验证：`xtask test compiler-z42` → 2 units 21 cases（core 11 + lexer 10）全绿

## increment 2（Parser 表达式 + AST — ✅ 已完成）
- [x] `Ast.z42`：Expr 基类 + virtual Dump()；IntLit/FloatLit/StringLit/BoolLit/NullLit/Ident/Unary/Binary（class 继承 + override，受限写法）
- [x] `Parser.z42`：Pratt 优先级爬升（|| 30 / && 40 / ==·!= 50 / 比较 60 / +- 70 / */% 80 / 一元前缀 85）+ 括号分组 + 错误恢复
- [x] 9 单测（字面量/优先级/括号/左结合/逻辑优先级/比较+算术/一元/混合）→ tests/parser/
- [x] 验证：`xtask test compiler-z42` → 3 units 30 cases（core 11 + lexer 10 + parser 9）全绿

## increment 3（postfix/赋值 + 语句 — ✅ 已完成）
- [x] 后缀表达式：`.` 成员 / `()` 调用（变参）/ `[]` 索引（Pratt 循环最紧）+ 赋值 `=`（右结合，bp 10）
- [x] AST 新节点：MemberExpr/IndexExpr/CallExpr/AssignExpr（Ast.z42）+ Stmt 层级（Stmt.z42：ExprStmt/VarDeclStmt/ReturnStmt/IfStmt/WhileStmt/BlockStmt）
- [x] 语句递归下降：block `{}` / `if`-`else` / `while` / `return [e];` / var-decl（`var`|类型|自定义类型 标识符 = expr;）/ expr-stmt
- [x] 13 + 7 单测（postfix/assign 4 例补 parser；语句 7 例新单元 tests/stmt/）
- [x] 验证：`xtask test compiler-z42` → 4 units 41 cases 全绿（core 11 + lexer 10 + parser 13 + stmt 7）

## increment 4（顶层声明 — ✅ 已完成）
- [x] AST：CompilationUnit / UsingDecl / ClassDecl / FieldDecl / MethodDecl / Param（Decl.z42，class 继承 + Dump）
- [x] 递归下降：`ParseCompilationUnit`（file-scoped namespace + using + class）/ `_parseClass`（修饰符 + 基类/接口列表）/ `_parseMember`（field / method / ctor 消歧）/ 参数列表
- [x] 类型文本解析：限定名 `a.b.c` + 泛型 `<...>`（深度计数含 `>>` 嵌套）+ 数组 `[]` + nullable `?`
- [x] 修饰符：public/private/protected/internal/static/abstract/sealed/override/virtual/extern/async/new
- [x] 10 单测（namespace+using / 空类 / 修饰符+基类 / 字段 / 方法 / 构造器 / 抽象无体 / 泛型+数组 / 嵌套泛型 / 完整类）→ tests/decl/
- [x] 验证：`xtask test compiler-z42` → 5 units 51 cases 全绿（core 11 + lexer 10 + parser 13 + stmt 7 + decl 10）

## increment 5a（控制流 + 三目 — ✅ 已完成）
- [x] 语句：`foreach (type name in iter) body` / `break;` / `continue;` / `throw e;`（Stmt.z42 + dispatch）
- [x] 表达式：三目 `?:`（右结合 bp 20，正确低于 ?: 之上运算符、高于赋值）（Ast.z42 TernaryExpr）
- [x] 5 单测（ternary 4 例 + foreach/break-continue/throw/ternary-in-stmt）；验证 → 5 units 56 cases 全绿

## increment 5b（表达式运算符补全 — ✅ 已完成）
- [x] 位运算 & ^ | << >>（bp 48/46/44/65，左结合）+ `??`（bp 25 右结合）+ `is`/`as`（bp 60，右侧类型）+ 复合赋值 += -= *= /= %= &= |= ^=（AssignExpr 加 Op）+ `new T(args)`（ObjNewExpr）
- [x] AST 新节点 IsExpr/AsExpr/ObjNewExpr；6 单测（bitwise/??/is·as/compound-assign/new）
- [x] 验证：`xtask test compiler-z42` → 5 units 61 cases 全绿（core 11 + lexer 10 + parser 19 + stmt 11 + decl 10）

## increment 6a（语句补全 — ✅ 已完成）
- [x] AST：ForStmt（三段可省 + bool 标志）/ DoWhileStmt / SwitchStmt + SwitchCase / TryCatchStmt + CatchClause（Stmt.z42，受限写法：typed array + count、bool 标志替 nullable）
- [x] 递归下降：`_parseFor`（init 走 ParseStatement 消费 `;`；三段任意可省）/ `_parseDoWhile` / `_parseSwitch`（case/default + 空分支贯穿）/ `_parseTry`（catch 类型/变量可省 + 裸 catch + finally）+ dispatch
- [x] 4 单测（for[含三段全省/init-as-expr] / do-while[单语句+block] / switch[多 case+default / 空分支贯穿] / try[type+var / type-only+finally / 裸 catch]）→ tests/stmt/
- [x] 验证：`xtask test compiler-z42` → 5 units **65 cases** 全绿（core 11 / lexer 10 / parser 19 / stmt 15 / decl 10）

## increment 6b-1（struct / interface / enum — ✅ 已完成）
- [x] ClassDecl 加 `Kind`（"class"/"struct"/"interface"，三者同构：`_parseClass` → `_parseTypeDecl(mods, kind)`，Dump head=Kind）
- [x] AST：EnumDecl + EnumMember（值仅捕获文本，自增/校验留后端）；Decl.z42
- [x] 递归下降：`_parseTypeDecl`（class/struct/interface 共用基类·接口列表 + 成员块）/ `_parseEnum`（底层类型 `: int` + 成员 `name [= [-]int]` + 尾随逗号）+ ParseCompilationUnit dispatch
- [x] 3 单测（struct[含 ctor 消歧] / interface[方法签名+基接口] / enum[裸成员 / 底层类型+显式值+尾逗号]）→ tests/decl/
- [x] 验证：`xtask test compiler-z42` → 5 units **68 cases** 全绿（core 11 / lexer 10 / parser 19 / stmt 15 / decl 13）

## increment 6b-2（后续 — record / delegate / 顶层 func）
- [ ] record（位置式 `record P(T a)` + 块式）/ delegate `delegate R Name(params);` / 顶层 func（class 外的自由函数）

## increment 6b-3（后续 — property）
- [ ] auto-property `T Name { get; set; }` + 访问器修饰；interface 内属性签名

## increment 6b-4（后续 — 泛型形参 + attribute）
- [ ] 类型声明泛型形参 `<T>` + `where T : ...` 约束 + attribute `[X]`（顶层 + 成员 + 参数）

## increment 6c（后续 — Lexer 补全）
- [ ] 插值串 / raw 串 / hex·bin·分隔符·后缀 / 转义解码

## increment 6d（后续 — Visitor + TypeExpr）
- [ ] 真实 Visitor 基类（替代 Dump 临时方案）；TypeExpr AST（替代类型文本字符串）

## increment 6e（后续 — byte-identical）
- [ ] token + AST JSON 与 C# `--dump-tokens` / `--dump-ast` 逐项对账

## 备注
- SyntaxSkeleton.z42 暂留（semantics/pipeline/driver 仍引用，各自移植时移除）。
- Token.z42 `using Z42.Core` 被报 unused（字段类型 Span 引用不计入 using 检测）——cosmetic。
