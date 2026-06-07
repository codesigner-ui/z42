# z42c.project

## 职责
镜像 C# [z42.Project](../../compiler/z42.Project/README.md)：项目清单（`.z42.toml` 解析 / 源文件发现 / zpkg builder）。**B0 骨架：占位类型 `ProjectSkeleton`**（引用 z42c.ir 验证跨包编译）；真实 manifest reader 待 0.3.4。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/ProjectSkeleton.z42` | 占位（`namespace Z42.Project`，引用 `Z42.IR`）|

## 入口点
`Z42.Project`（命名空间）。

## 依赖关系
→ z42c.ir。stdlib 自动可用。
