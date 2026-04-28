# Tasks: strict-using-resolution

> 状态：🟢 已完成 | 类型：lang (using/import 语义) | 创建：2026-04-28 | 完成：2026-04-28

## 阶段 1：基础

- [x] 1.1 `src/compiler/z42.Core/PreludePackages.cs` (NEW) — 硬编码 `{ "z42.core" }`
- [x] 1.2 `src/compiler/z42.Core/Diagnostics/Diagnostic.cs` + `DiagnosticCatalog.cs` 加 E0601 / E0602 / W0603

## 阶段 2：核心

- [x] 2.1 ImportedSymbols 加 `ClassPackages` + `Collisions` 字段（向后兼容默认值）
- [x] 2.2 TsigCache 暴露 `_pathToPkg` + `LoadForPackages(activatedPackages)` + `PackagesProvidingNamespace(ns)` + `AllPackages()` API
- [x] 2.3 ImportedSymbolLoader.Load 双签名：旧 (modules, usings) 兼容包装；新 (modules, packageOf, activated, prelude) 主 API；冲突写入 Collisions
- [x] 2.4 PackageCompiler.LoadExternalImported 改用 cu.Usings + prelude（重构 TryCompileSourceFiles 为 parse-all → 收集 usings → 加载）
- [x] 2.5 SingleFileCompiler.LocateImportedSymbols 改签名加 `userUsings` 参数
- [x] 2.6 TypeChecker.EmitImportDiagnostics 报 E0601 / E0602
- [x] 2.7 PackageCompiler.BuildTarget + SingleFileCompiler 在 zpkg 注册时检测 reserved-prefix → W0603

## 阶段 3：测试与迁移

- [x] 3.1 ImportedSymbolLoaderTests 加 prelude / 冲突 / unresolved / PackageOf 5 个用例
- [x] 3.2 UsingResolutionTests (NEW) 6 个端到端用例
- [x] 3.3 `scripts/audit-missing-usings.sh` 扫描 source.z42 推断需要 using
- [x] 3.4 golden test 批量补 using（patched 89 个文件）
- [x] 3.5 GoldenTests / IrGenTests / ZbcRoundTripTests 改用新 API
- [x] 3.6 删除 source.z42 中残留的 `using System;`（C# 习惯）
- [x] 3.7 4 个 IR snapshot 因 line shift + Console 类型解析 regen expected.zasm

## 阶段 4：验证 + 文档 + 归档

- [x] 4.1 `dotnet build src/compiler/z42.slnx` 无错
- [x] 4.2 `dotnet test` 734/734 全绿
- [x] 4.3 `bash scripts/test-vm.sh` interp 100/100 + jit 100/100 全绿
- [x] 4.4 `docs/design/namespace-using.md` 重写多文件章节为生效语义 + 加 strict-using-resolution 节
- [x] 4.5 `docs/design/stdlib.md` Module Auto-load Policy 表格说明 prelude 白名单
- [x] 4.6 `docs/design/error-codes.md` 加 E06xx 章节
- [x] 4.7 commit + push + 移动到 spec/archive/2026-04-28-strict-using-resolution/

## 备注

- 决策已确认（2026-04-28）：z42.core 唯一 prelude / W0603 软警告 / golden 一次性补 / List/Dictionary 不动
- intraSymbols（同包跨 CU）路径不走 using 过滤，直接全可见（同包内类型默认互见，与 namespace 无关）
- 删除 fix-using-prelude-include 的 hardcoded `allowedNs.Add("Std")`，由新机制等价覆盖（compat wrapper 走兼容路径，新主 API 走 prelude 包激活）
- z42.io / z42.collections / z42.text / z42.math / z42.test 全部以 `z42.` 开头 →
  `IsStdlibPackage` 检测放行，可安全声明 `Std.*` namespace
- 第三方包仅在不以 `z42.` 开头 + 声明 `Std.*` 时触发 W0603
- 兼容性入口 `ImportedSymbolLoader.Load(modules, usings)` 仍保留（GoldenTests 早期路径），按 namespace 形成虚拟 package 过滤
