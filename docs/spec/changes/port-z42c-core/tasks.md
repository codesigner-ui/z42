# Tasks: port-z42c-core — z42c.core 真实移植（increment 1：诊断地基）

> 状态：🟡 进行中 | 创建：2026-06-07 | 子系统锁：z42c + toolchain（见 ACTIVE.md）
> **变更说明：** 把 C# `z42.Core` 的诊断地基用 z42 重写，替换 B0 的 `CoreSkeleton` 占位。
> **原因：** B 主线自举逐子系统实做的第一步；Lexer（下一子系统）依赖 Span/DiagnosticBag/DiagnosticCodes。
> **类型：** port（实现既有行为，受限写法；非新语言特性）。架构见 [self-hosting.md](../../../design/compiler/self-hosting.md)。
> **文档影响：** src/z42c/z42c.core/README.md（核心文件表）；self-hosting.md 进度（归档时）。

## 受限写法映射（C# z42.Core → z42）
- `readonly record struct Span` → `class Span`（public 字段 + 构造器）
- `enum DiagnosticSeverity` → `static class` + int 常量（z42 暂无 enum，沿用 SplitOptions/SeekOrigin 模式）
- `sealed record Diagnostic` → `class Diagnostic`
- `class DiagnosticBag` → `class`（内部 `List<Diagnostic>`）
- `static class DiagnosticCodes`（const string）→ `static class` + `static string` 字段

## increment 1a 范围（诊断地基 — 类型，✅ 已提交）
- [x] 1.1 `Span.z42`（Start/End/Line/Column/File）
- [x] 1.2 `DiagnosticSeverity.z42`（Error/Warning/Info = 0/1/2，int 常量）
- [x] 1.3 `Diagnostic.z42`（Severity/Code/Message/Span + IsError/IsWarning/Format + 工厂）
- [x] 1.4 `DiagnosticBag.z42`（typed array + count；Add/Error/Warning/Info/Count/Get/ErrorCount/HasErrors/HasWarnings/Grow）
- [x] 1.5 `DiagnosticCodes.z42`（E01xx–E10xx 错误码常量，镜像 C#）
- [x] 1.6 保留 `CoreSkeleton.z42`（过渡：syntax/semantics/pipeline/driver 仍引用，各自移植时移除——非本 increment）
- [x] 1.7 README 核心文件表更新；self-hosting.md 受限写法补充（3 条 dogfood 发现）
- [x] 1.8 验证：z42c.core 编译通过（`build -p z42c.core`）

## increment 1b 范围（测试 + 工具链 — 下一步）
- [ ] 2.1 z42c [Test] 单测（Span / Diagnostic / DiagnosticBag / DiagnosticCodes：正常 + 边界）
- [ ] 2.2 `scripts/xtask_compiler_z42.z42` 接入 z42-test-runner 跑 z42c [Test]（建 z42c 测试构建+运行通道；mirror stdlib _testLib 的 compile-unit + runner）
- [ ] 2.3 验证：build compiler-z42 7 zpkg ✅ + z42c [Test] 全绿

## 验证 + 归档
- [ ] 3.1 dotnet 不涉及；z42c workspace 构建无错
- [ ] 3.2 z42c.core [Test] 全绿（z42-test-runner）
- [ ] 3.3 commit + push（逐文件 stage）；ACTIVE.md 阶段 9 释放 z42c/toolchain

## 备注（后续 increment）
- LanguageFeatures（Dict<string,bool> + Minimal/Phase1 profile）→ increment 2
- DiagnosticRenderer / DiagnosticCatalog / DiagnosticCategory（CLI 渲染 + explain）→ increment 3（driver 需要时）
- PreludePackages → 随 project/pipeline 子系统
