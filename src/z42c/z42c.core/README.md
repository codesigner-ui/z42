# z42c.core

## 职责
镜像 C# [z42.Core](../../compiler/z42.Core/README.md)：基础设施层（源码位置 Span / 诊断 Diagnostic·DiagnosticBag / 语言特性开关 LanguageFeatures）。无兄弟依赖，被所有子包引用。**B0 骨架：占位类型 `CoreSkeleton`**，真实实现待后续 spec。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/CoreSkeleton.z42` | 占位（`namespace Z42.Core`）；真实 Span/Diagnostic/Features 待 0.3.3+ |

## 入口点
`Z42.Core`（命名空间，镜像 C# 同名）。

## 依赖关系
无（叶子）。stdlib 自动可用。
