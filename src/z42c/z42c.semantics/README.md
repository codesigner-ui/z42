# z42c.semantics

## 职责
镜像 C# [z42.Semantics](../../compiler/z42.Semantics/README.md)：语义层（SymbolCollector / TypeCheck 含 Bound 节点 / Codegen）。首个硬子系统，dogfood 缺口高发段。**B0 骨架：占位类型 `SemanticsSkeleton`**（引用 core/syntax/ir 验证多依赖跨包编译）；真实实现待 0.3.5–0.3.6。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/SemanticsSkeleton.z42` | 占位（`namespace Z42.Semantics`，引用 core/syntax/ir）|

## 入口点
`Z42.Semantics`（命名空间）。

## 依赖关系
→ z42c.core, z42c.syntax, z42c.ir。stdlib 自动可用。
