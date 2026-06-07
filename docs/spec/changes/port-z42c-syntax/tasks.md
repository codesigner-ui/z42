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

## increment 2（后续）
- [ ] Lexer 补全：插值串 `$"..."` / raw 串 `"""..."""` / hex·bin·数字分隔符·后缀 / 转义解码 / Newline 处理对账
- [ ] Parser（Pratt 表达式 + 递归下降 声明/语句）+ AST（class 继承 + 抽象 Visitor，受限写法）
- [ ] byte-identical：token 流 JSON 与 C# `--dump-tokens` 逐项对账

## 备注
- SyntaxSkeleton.z42 暂留（semantics/pipeline/driver 仍引用，各自移植时移除）。
- Token.z42 `using Z42.Core` 被报 unused（字段类型 Span 引用不计入 using 检测）——cosmetic。
