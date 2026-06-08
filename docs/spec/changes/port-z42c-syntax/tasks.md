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

## increment 6b-2（record / delegate / 顶层 func — ✅ 已完成）
- [x] 抽出 `_parseParamList` → `ParamList` holder（method/ctor/func/delegate/record 共用）；MethodDecl 加 `IsFree`（Dump head func/method/ctor）
- [x] AST：DelegateDecl / RecordDecl（位置式 PosParams + 块体成员）；Decl.z42
- [x] 递归下降：`_parseDelegate` / `_parseTopLevelFunc`（命名空间级自由函数）/ `_parseRecord`（位置式 `;` 或块体 `{}`，可带基类）+ ParseCompilationUnit dispatch（含 type-keyword/identifier 守卫）
- [x] 3 单测（顶层 func[head=func] / delegate[含 public] / record[位置式;/位置+基类+块体/纯块式]）→ tests/decl/
- [x] 验证：`xtask test compiler-z42` → 5 units **71 cases** 全绿（core 11 / lexer 10 / parser 19 / stmt 15 / decl 16）

## increment 6b-3（property — ✅ 已完成）
- [x] AST：PropertyDecl（HasGet/GetMods/HasSet/SetMods + 可选 init）；Decl.z42
- [x] 递归下降：`_parseProperty`（auto `{ [vis]? get; [vis]? set; }` + `= init;`；get/set 为上下文标识符；自定义 body/表达式体/init-only 延后）hook 进 `_parseMember`（type+name 后遇 `{`）+ `_isVisibilityModifier` 辅助
- [x] 2 单测（class[get/set·get-only·init·访问器修饰] / interface 内属性签名）→ tests/decl/
- [x] 验证：`xtask test compiler-z42` → 5 units **73 cases** 全绿（core 11 / lexer 10 / parser 19 / stmt 15 / decl 18）

## increment 6b-4（泛型形参 `<T>` + where — ✅ 已完成）
- [x] ClassDecl/RecordDecl/DelegateDecl/MethodDecl 加 `TypeParams`（文本，含尖括号）+ `WhereText`
- [x] 解析：`_parseTypeParams`（复用 `_consumeAngles`）/ `_parseWhereClause`（多段 `where T : C1, C2`）/ `_parseConstraint`（new()/class/struct/类型）；wire 进类型声明·record·delegate·方法（成员名后 `<` = 泛型方法）·顶层 func
- [x] 3 单测（泛型类[+基接口类型实参+where]·泛型 interface / 泛型方法[+where new()] / 泛型 delegate·record[+where class]）→ tests/decl/
- [x] 验证：`xtask test compiler-z42` → 5 units **76 cases** 全绿（core 11 / lexer 10 / parser 19 / stmt 15 / decl 21）

## increment 6b-5（attribute — ✅ 已完成）
- [x] AST：Attr（Name + Expr 参数）+ AttrList holder + AttributedDecl 包裹（不污染各 decl 构造器）；Decl.z42
- [x] 解析：`_parseAttributes`（连续 `[A][B]` 组 + 组内逗号 `[A, B]` + `(args)` 走 `_parseExpr`）hook 进 ParseCompilationUnit + `_parseMember`（拆 `_parseMemberBody`）
- [x] 3 单测（成员 attribute[Test/位置参数] / 类型 attribute[单/多组/组内逗号] / 命名参数 `k=v`→AssignExpr）→ tests/decl/
- [x] 验证：`xtask test compiler-z42` → 5 units **79 cases** 全绿（core 11 / lexer 10 / parser 19 / stmt 15 / decl 24）

> **increment 6b 全部完成**（顶层声明补全：struct/interface/enum/record/delegate/顶层 func/property/泛型形参+where/attribute）。下一步 6c。

## increment 6c-1（Lexer 数字格式 — ✅ 已完成）
- [x] `_lexNumber` 重写：hex `0x`/`0X` + bin `0b`/`0B`（含 `_` 分隔）→ IntLiteral；十进制 + `_` 分隔 + 小数 + 指数 `[eE][+-]?digits`（指数即浮点）；后缀 L/l/u/U/f/F/d/D/m/M（f·d·m → 浮点）
- [x] 辅助 `_consumeDigits` / `_isHexDigit` / `_consumeNumSuffix` / `_isNumSuffix` / `_emitNum`
- [x] 4 单测（hex·bin / `_` 分隔 / 后缀 / 指数）→ tests/lexer/
- [x] 验证：`xtask test compiler-z42` → 5 units **83 cases** 全绿（core 11 / lexer 14 / parser 19 / stmt 15 / decl 24）

## increment 6c-2（Lexer 字符串补全 — ✅ 已完成）
- [x] raw 串 `"""..."""`（`_lexRawString`，不处理转义，扫描到下一 `"""`）+ `_lexOne` dispatch（`$"`/`"""` 先于 `"`）
- [x] 插值串 `$"...{e}..."`（`_lexInterpolated`，整体单 token；文本段转义跳过 + `{{`/`}}` 字面 + `{...}` brace 深度跟踪 + 洞内嵌套串 `_skipNestedString`）
- [x] `DecodeString` 静态助手（剥引号 + 还原 `\n \t \r \\ \" \'`；`\0`/`\uXXXX`/`\xXX` 延后）
- [x] 3 单测（raw 串 / 插值串[含洞内嵌套串] / 转义解码）→ tests/lexer/
- [x] 验证：`xtask test compiler-z42` → 5 units **86 cases** 全绿（core 11 / lexer 17 / parser 19 / stmt 15 / decl 24）

> **increment 6c 全部完成**（Lexer 补全：数字格式 + raw/插值串 + 转义解码）。

## increment 6d（TypeExpr AST，架构性 — 设计见 design-6d-typeexpr-visitor.md）
> User 裁决（2026-06-08）：D1=连 where 约束一并结构化；D2=Visitor 本增量不做（保留 Dump，并入后端 semantics）。

### 6d-1（TypeExpr 节点 + is/as/new/var-decl/foreach — ✅ 已完成）
- [x] 新文件 `TypeExpr.z42`：NamedType / ArrayType / NullableType（Dump 复刻规范 type-text，无空格泛型实参 → 现有断言全绿）
- [x] `_parseType()`（递归泛型实参 + `_tryCloseTypeArgs`/`_pendingGt` 处理 `>>`/`>>>` 嵌套泛型 token 拆分）+ 数组 `[]` + nullable `?`
- [x] 切换 Ast（IsExpr/AsExpr/ObjNewExpr）+ Stmt（VarDeclStmt/ForeachStmt）的 string TypeText → TypeExpr
- [x] 1 新单测 test_type_expr_forms（空格归一/嵌套 `>>`/三层 `>>>`/限定名/数组/nullable/泛型+数组）+ 现有 is/as/new/foreach 断言全绿
- [x] 验证：`xtask test compiler-z42` → 5 units **87 cases** 全绿（core 11 / lexer 17 / parser 20 / stmt 15 / decl 24）

### 6d-2（后续 — Decl 类型字段 + 形参结构化）
- [ ] Param/Field/Property/Method.RetType/Delegate.RetType/Class·Enum·Record.Bases → TypeExpr；TypeParams → string[] Names

### 6d-3（后续 — where 约束结构化）
- [ ] WhereClause/WhereConstraint（TypeExpr | new()/class/struct）于 Class/Delegate/Record/Method；移除残留 `_parseTypeText`/WhereText string

## increment 6d（后续 — Visitor + TypeExpr）
- [ ] 真实 Visitor 基类（替代 Dump 临时方案）；TypeExpr AST（替代类型文本字符串）

## increment 6e（后续 — byte-identical）
- [ ] token + AST JSON 与 C# `--dump-tokens` / `--dump-ast` 逐项对账

## 备注
- SyntaxSkeleton.z42 暂留（semantics/pipeline/driver 仍引用，各自移植时移除）。
- Token.z42 `using Z42.Core` 被报 unused（字段类型 Span 引用不计入 using 检测）——cosmetic。
