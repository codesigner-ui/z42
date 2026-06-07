# z42c.syntax

## 职责
镜像 C# [z42.Syntax](../../compiler/z42.Syntax/README.md)：语法层（Lexer 词法 + Parser 语法 → AST）。**B0 骨架：占位类型 `SyntaxSkeleton`**（引用 z42c.core 验证跨包编译）；真实 Lexer/Parser/AST（class 继承 + 抽象 Visitor，受限写法）待 0.3.3。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/TokenKind.z42` | 147 个 token 类型常量（int；镜像 C# enum TokenKind）|
| `src/Token.z42` | 词法 token（Kind / Text / Span）|
| `src/Lexer.z42` | 手写词法器：trivia 跳过 + 标识符/关键字 + 数字 + 字符串/字符 + 全符号最长匹配 + EOF |
| `src/SyntaxSkeleton.z42` | **过渡占位**：semantics/pipeline/driver 仍引用；各自移植时移除 |

## 入口点
`Z42.Syntax.Lexer`：`var lx = new Lexer(src, file); lx.Tokenize(); lx.TokenCount(); lx.TokenAt(i)`。
测试：`tests/lexer/`（10 例），经 `xtask test compiler-z42`。
待移植：Parser（Pratt + 递归下降）+ AST；Lexer 补全（插值/raw 串、hex/bin、转义解码）。

## 依赖关系
→ z42c.core。stdlib 自动可用。
