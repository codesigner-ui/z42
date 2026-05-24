# Tasks: fix MemoryStream override resolution (cross-CU + cross-package)

> 状态：🟢 已完成 | 创建：2026-05-24 | 完成：2026-05-24 | 类型：fix
> Spec 类型：minimal mode
>
> **Scope 扩大记录**：原以为只是 visibility（`override` 漏 `public`）— 加上 `public` 后症状仍在。
> 深查发现三个独立的 TypeChecker bug 叠加在一起：
>
> 1. **per-CU 处理顺序依赖**：`SymbolCollector.CollectClasses` 第二 pass 内的 override 检查
>    需要 `_virtualMethods[baseClassName]` 已 populated，但 alphabetical CU 顺序
>    （`MemoryStream.z42` 排在 `Stream.z42` 前）会让 MemoryStream 的检查先跑 → false negative
> 2. **MergeImported 不 populate `_virtualMethods`**：跨包 override（如 z42.compression 的
>    `CompressionEncoderStream : Stream` 其中 Stream 来自 z42.io）所以即使 TSIG 携带 virtual
>    flag，per-CU 检查时也找不到
> 3. **ExportedTypeExtractor.FuncToMethod 硬编码 IsVirtual=false**：所有 class methods
>    导出到 TSIG 时丢失 virtual 修饰符。pre-existing bug，因为之前没人跨包 override 没人发现。

**问题表象**：[`src/libraries/z42.io/src/MemoryStream.z42`](../../../../src/libraries/z42.io/src/MemoryStream.z42) 的 10 个 `public override` 方法报 E0411，导致 z42.io / z42.compression 等多个 stdlib 包编译失败。

**Fix（三层）**：
1. SymbolCollector：把 override-validation 移出 per-CU 第二 pass，新建 `FinalizeOverrideChecks()`，由 PackageCompiler 在 `FinalizeInheritance()` 后调用一次
2. MergeImported：populate `_virtualMethods` from imported class methods（基于 `ms.Modifiers.HasFlag(Virtual|Abstract)`）
3. ExportedTypeExtractor.FuncToMethod：接受 `FunctionModifiers` 参数并保留 IsVirtual / IsAbstract 到 ExportedMethodDef
4. TypeChecker.Check：single-CU 路径也调 `FinalizeInheritance()` + `FinalizeOverrideChecks()`（之前只 PackageCompiler 调）
5. MemoryStream.z42：10 个 `override` 加 `public` 前缀（visibility 修正，与 fix #1-#3 配合）

**Scope**（允许改动的文件）：

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/libraries/z42.io/src/MemoryStream.z42` | MODIFY | 10 个 `override` 方法加 `public` 前缀 |
| `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.cs` | MODIFY | MergeImported populate `_virtualMethods`；新增 `_pendingOverrideChecks` 字段 |
| `src/compiler/z42.Semantics/TypeCheck/SymbolCollector.Classes.cs` | MODIFY | 移 override-validation block 到新 `FinalizeOverrideChecks()` 方法 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.cs` | MODIFY | single-CU 路径调 `FinalizeInheritance()` + `FinalizeOverrideChecks()` |
| `src/compiler/z42.Semantics/TypeCheck/ExportedTypeExtractor.cs` | MODIFY | `FuncToMethod` 接受 modifiers 参数并保留 IsVirtual / IsAbstract |
| `src/compiler/z42.Pipeline/PackageCompiler.BuildTarget.cs` | MODIFY | `FinalizeInheritance()` 后调 `FinalizeOverrideChecks()` |

**Out of Scope**：
- 不改 Stream.z42 / SeekOrigin.z42（已 OK）
- 不改 stream_*.z42 测试

**文档影响**：无（纯实现修正 + visibility，不改对外 API）

## Tasks

- [x] 1.1 MemoryStream.z42 — 10 个 override 方法加 `public` 前缀
- [x] 1.2 SymbolCollector.Classes.cs — 移 override-validation block 出 per-CU；提供 `FinalizeOverrideChecks()` 公开方法
- [x] 1.3 SymbolCollector.cs — `_pendingOverrideChecks` 字段；MergeImported populate `_virtualMethods` 从 imported class methods
- [x] 1.4 ExportedTypeExtractor.cs — `FuncToMethod` 接受 `FunctionModifiers`，保留 IsVirtual / IsAbstract 到 TSIG
- [x] 1.5 PackageCompiler.BuildTarget.cs — 在 `FinalizeInheritance()` 后调 `FinalizeOverrideChecks()`
- [x] 1.6 TypeChecker.cs — single-CU 路径调 `FinalizeInheritance()` + `FinalizeOverrideChecks()`
- [x] 1.7 `dotnet build src/compiler/z42.slnx` 全绿
- [x] 1.8 `./scripts/build-stdlib.sh` 20 member 全成功（含 z42.io / z42.diagnostics / z42.test / z42.compression / z42.io.binary）
- [x] 1.9 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj --no-build` 1291/1291 全绿
- [x] 1.10 commit + push（单 commit；含本 spec）
- [x] 1.11 mv → `docs/spec/archive/2026-05-24-fix-memorystream-override-visibility/`

## 备注

实施期发现：
- 原以为是单层 visibility 修正，结果挖出三个 TypeChecker bug 叠加。前两者由 fix-package-compiler-cross-file (2026-04-26) 的 FinalizeInheritance pattern 启发解决；第三个是 pre-existing bug（IsVirtual 硬编码 false）— MemoryStream 是 stdlib 中首个跨文件 / 跨包 override 用例，所以一直没人发现。
- 单 commit 一起发布所有 5 处修正（不可拆，因为相互依赖）。
