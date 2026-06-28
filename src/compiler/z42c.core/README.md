# z42c.core

## 职责
镜像 C# [z42.Core](../../compiler/z42.Core/README.md)：基础设施层（源码位置 Span / 诊断 Diagnostic·DiagnosticBag / 语言特性开关 LanguageFeatures）。无兄弟依赖，被所有子包引用。**B0 骨架：占位类型 `CoreSkeleton`**，真实实现待后续 spec。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/Span.z42` | 源码位置范围 `[Start,End)` + 行列 + File |
| `src/DiagnosticSeverity.z42` | Error/Warning/Info（int 常量；z42 暂无 enum）|
| `src/Diagnostic.z42` | 单条诊断（Severity/Code/Message/Span + IsError + Format + 工厂）|
| `src/DiagnosticBag.z42` | 诊断收集器（typed array + count；Add/Error/Count/Get/ErrorCount/HasErrors）|
| `src/DiagnosticCodes.z42` | E01xx–E10xx 错误码常量（镜像 C# `DiagnosticCodes`）|
| `src/LanguageFeatures.z42` | 特性开关（snake_case 名 + 并行数组；IsEnabled / Phase1Profile / MinimalProfile）|
| `src/CoreSkeleton.z42` | **过渡占位**：尚未移植的 syntax/semantics/pipeline/driver 仍引用它；各自移植到真实 core 时移除 |

> 受限写法（无 enum / 类字段无泛型 / List 约束 → typed array）见 [self-hosting.md](../../../docs/design/compiler/self-hosting.md)。
> 测试：`tests/diag/`（11 例：诊断 7 + 特性 4），经 `xtask test compiler`。
> 待移植：DiagnosticRenderer·Catalog·Category（CLI 渲染，driver 需要时）/ PreludePackages。

## 入口点
`Z42.Core`（命名空间，镜像 C# 同名）。

## 依赖关系
无（叶子）。stdlib 自动可用。
