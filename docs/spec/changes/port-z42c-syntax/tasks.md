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

## increment 3（后续）
- [ ] Parser 补全：postfix（`.` 成员 / `()` 调用 / `[]` 索引）/ 赋值 / 三目 / `??` / 位运算 / `is`·`as`
- [ ] 语句（block/if/while/for/return/...）+ 声明（class/func/field/...）递归下降 + 抽象 Visitor
- [ ] Lexer 补全：插值串 `$"..."` / raw 串 `"""..."""` / hex·bin·分隔符·后缀 / 转义解码
- [ ] byte-identical：token 流 + AST JSON 与 C# `--dump-tokens` / `--dump-ast` 逐项对账

## 备注
- SyntaxSkeleton.z42 暂留（semantics/pipeline/driver 仍引用，各自移植时移除）。
- Token.z42 `using Z42.Core` 被报 unused（字段类型 Span 引用不计入 using 检测）——cosmetic。
