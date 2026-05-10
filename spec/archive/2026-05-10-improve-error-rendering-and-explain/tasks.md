# Tasks: Source-Context Diagnostic Rendering + Extended Explain

> 状态：🟢 已完成 | 完成：2026-05-10 | 创建：2026-05-10
> 类型：feat + refactor（最小化模式）

**变更说明：** 把 z42c 的诊断输出从 MSBuild 风格升级为 rust/clang 风格 —— `error[E0xxx]: title` + `--> file:L:C` + 源码上下文行 + caret/underline + ANSI 颜色（TTY 检测）。同时把 `z42c explain` 扩到能解释 `WS###` workspace 错误码（复用 C# 基础设施）。

**Z#### VM 错误码 catalog + 跨语言注册表** 留给批 2，本批仅 C# 侧。

**原因：** L2 错误体系收尾。已有 42 个 E#### + DiagnosticCatalog 100% 文档化、explain 命令已存在；缺源码上下文渲染（用户感知最强）+ explain 跨码空间统一。

**文档影响：** [docs/dev.md](../../docs/dev.md) 加诊断输出格式说明（如有 dev 章节）；不需新 design doc。

## 阶段 1: Source-context renderer（核心）

- [x] 1.1 新建 [src/compiler/z42.Core/Diagnostics/DiagnosticRenderer.cs](../../src/compiler/z42.Core/Diagnostics/DiagnosticRenderer.cs) — 静态类，提供 `Render(Diagnostic, sourceText, useColor) -> string`
  - 头：`error[E0xxx]: <title>`（severity 取 catalog Title，不取 message — 避免重复）
  - 位置：`  --> <file>:<line>:<col>`
  - 源码上下文：显示出错行 ± 1 行（gutter 数字 + 竖线分隔）
  - Caret 行：`^^^` 标 span 字符位置
  - 消息：`  = note: <message>` （从 Diagnostic.Message 来）
  - useColor=true → ANSI 红/黄/蓝（severity）+ 灰（gutter）
- [x] 1.2 [DiagnosticBag.cs](../../src/compiler/z42.Core/Diagnostics/DiagnosticBag.cs) `PrintAll` 改用 Renderer，按 file 分组读源码一次
  - 加 `enum DiagnosticOutputFormat { Pretty, Plain }`，默认 Pretty
  - Plain 模式保 MSBuild 一行格式（IDE 集成兼容）
  - TTY 检测：`Console.IsErrorRedirected ? noColor : color`
- [x] 1.3 [Program.cs](../../src/compiler/z42.Driver/Program.cs) 加 `--diagnostic-format <pretty|plain>` 全局选项；或环境变量 `Z42_DIAG_FORMAT`

## 阶段 2: Workspace catalog + 扩展 explain

- [x] 2.1 [src/compiler/z42.Project/WorkspaceCatalog.cs](../../src/compiler/z42.Project/WorkspaceCatalog.cs) NEW — 镜像 DiagnosticCatalog 结构，把 [ManifestErrors.cs](../../src/compiler/z42.Project/ManifestErrors.cs) 已有的 factory message（即每个 WS## 的 `error[...]:` 文案）抽成 Title/Description/Example
- [x] 2.2 [DiagnosticCatalog.cs](../../src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs) 加 `TryGet(code)` 跨 catalog 查找路由：E → 自身；W → 转 WorkspaceCatalog（依赖反转 — 用接口 / DI）
  - 简单做：`DiagnosticCatalog.Explain` 内部按前缀 dispatch；新增 `IDiagnosticCatalog` 接口 + WorkspaceCatalog 实现 + 静态 `Catalogs` 注册表
- [x] 2.3 [Program.cs](../../src/compiler/z42.Driver/Program.cs) `explain` 命令支持任意前缀（E/W/Z），按前缀路由
  - Z#### 分支：返回"VM error, see runtime documentation"占位（批 2 接 Z catalog 后真返）
- [x] 2.4 `errors` 命令把 W#### 也列出来

## 阶段 3: 测试

- [x] 3.1 [src/compiler/z42.Tests/DiagnosticRendererTests.cs](../../src/compiler/z42.Tests/DiagnosticRendererTests.cs) NEW — 单元测试：
  - 单行错误带 caret 位置正确
  - 多行 span 不显示 caret 但有竖线 marker
  - Plain 模式输出兼容 MSBuild
  - useColor=false 不含 ANSI escape
- [x] 3.2 加 1-2 个 errors/ golden case 验证 Pretty 输出（snapshot test）—— 可选，避免脆弱
- [x] 3.3 explain 命令 e2e：调 `z42c explain WS003` 返回 WorkspaceCatalog Title/Description

## 阶段 4: 文档

- [x] 4.1 [docs/dev.md](../../docs/dev.md) 或 [src/compiler/z42.Core/Diagnostics/](../../src/compiler/z42.Core/Diagnostics/) README 段说明输出格式 + 选项

## 阶段 5: 验证

- [x] 5.1 dotnet build src/compiler/z42.slnx 全绿
- [x] 5.2 dotnet test src/compiler/z42.Tests/z42.Tests.csproj 全绿（含新测试 + 现有 errors/ golden）
- [x] 5.3 手动 smoke：跑 `z42c src/tests/errors/missing_brace.z42` 看 Pretty 输出

## Scope

| 文件 | 类型 | 说明 |
|---|---|---|
| `src/compiler/z42.Core/Diagnostics/DiagnosticRenderer.cs` | NEW | 渲染器 |
| `src/compiler/z42.Core/Diagnostics/DiagnosticBag.cs` | MODIFY | PrintAll 改用 Renderer |
| `src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs` | MODIFY | 加跨 catalog 路由 |
| `src/compiler/z42.Project/WorkspaceCatalog.cs` | NEW | WS catalog |
| `src/compiler/z42.Driver/Program.cs` | MODIFY | --diagnostic-format flag + explain 路由 |
| `src/compiler/z42.Tests/DiagnosticRendererTests.cs` | NEW | 单元测试 |
| `docs/dev.md` 或 README | MODIFY | 文档 |

**只读引用：**
- `src/compiler/z42.Project/ManifestErrors.cs` — 抽 factory 文案到 catalog（不改 throw 路径）
- `src/compiler/z42.Core/Text/Span.cs` — 用 Span.Start/End/Line/Column

## 备注

- errors/ golden 测试当前用 `expected_error.txt` 比对 `Diagnostic.ToString()`。Pretty 模式默认开后会改 errors/ golden 输出格式 → 测试要么改用 Plain 模式跑，要么 golden 文件全更新。**默认走 Plain，避免 golden 爆改**；Pretty 是用户终端默认（通过 TTY 检测）
- ANSI 颜色仅在 stderr 是 TTY 时启用；CI / IDE / pipe 下自动关
- Z#### 留批 2 —— 本批 explain Z 时返回简短提示
