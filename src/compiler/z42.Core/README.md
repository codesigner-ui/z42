# z42.Core

## 职责
基础设施层：源码位置、诊断收集、语言特性开关。不依赖任何其他 z42 模块；被所有模块引用。

## 核心文件
| 文件 | 职责 |
|------|------|
| `Text/Span.cs` | 源码位置范围 `[Start, End)` + 行列号 |
| `Diagnostics/Diagnostic.cs` | 单条诊断记录 + `DiagnosticCodes` 错误码常量 |
| `Diagnostics/DiagnosticBag.cs` | 诊断收集器，允许多错误累积后再报告 |
| `Diagnostics/DiagnosticCatalog.cs` | 每个错误码的完整说明（`z42c explain`）|
| `Features/LanguageFeatures.cs` | 语言特性开关（Minimal / Phase1 profiles）|

## 入口点
- `Z42.Core.Text.Span` — 所有 AST 节点和诊断的位置类型
- `Z42.Core.Diagnostics.DiagnosticBag` — 编译器错误收集
- `Z42.Core.Features.LanguageFeatures` — 特性 gate 查询

## 依赖关系
无（不依赖任何 z42 模块）
