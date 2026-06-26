# z42c.pipeline

## 职责
镜像 C# [z42.Pipeline](../../compiler/z42.Pipeline/README.md)：编译管线编排（单文件 + 包级 Lexer→Parser→Sem→IR→Emit）。**B0 骨架：占位类型 `PipelineSkeleton`**（引用全部 5 个直接依赖，验证最深多依赖节点跨包编译）；真实编排 → 端到端 build 待 0.3.9。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/PipelineSkeleton.z42` | 占位（`namespace Z42.Pipeline`，引用 core/syntax/semantics/ir/project）|

## 入口点
`Z42.Pipeline`（命名空间）。

## 依赖关系
→ z42c.core, z42c.syntax, z42c.semantics, z42c.ir, z42c.project。stdlib 自动可用。
