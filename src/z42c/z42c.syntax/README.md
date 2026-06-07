# z42c.syntax

## 职责
镜像 C# [z42.Syntax](../../compiler/z42.Syntax/README.md)：语法层（Lexer 词法 + Parser 语法 → AST）。**B0 骨架：占位类型 `SyntaxSkeleton`**（引用 z42c.core 验证跨包编译）；真实 Lexer/Parser/AST（class 继承 + 抽象 Visitor，受限写法）待 0.3.3。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/SyntaxSkeleton.z42` | 占位（`namespace Z42.Syntax`，引用 `Z42.Core`）|

## 入口点
`Z42.Syntax`（命名空间）。

## 依赖关系
→ z42c.core。stdlib 自动可用。
