# z42c.ir

## 职责
镜像 C# [z42.IR](../../compiler/z42.IR/README.md)：共享契约（IR 模型 + zbc 二进制格式 + 项目类型）。与 C# 一致为无依赖叶子。**B0 骨架：占位类型 `IrSkeleton`**，真实 IR 模型 + lowering + ZbcWriter（寄存器 SSA）待 0.3.7/0.3.8。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/IrSkeleton.z42` | 占位（`namespace Z42.IR`）；真实 IR/zbc 待 0.3.7+ |

## 入口点
`Z42.IR`（命名空间）。

## 依赖关系
无（叶子）。stdlib 自动可用。
