# Tasks: fix-package-compiler-cross-file

> 状态：🟢 已完成 | 创建：2026-04-26 | 完成：2026-04-26 | 类型：fix (PackageCompiler 架构)

## 进度概览
- [x] 阶段 1: ImportedSymbolLoader.Combine + Empty helpers
- [x] 阶段 2: SymbolCollector / SymbolTable 跟踪 imported interface / func / enum names
- [x] 阶段 3: PackageCompiler 两阶段编译（Phase 1 collect intraSymbols, Phase 2 full compile）
- [x] 阶段 3.5: 修复跨 CU 多级继承合并 — Phase 1 改用 shared collector + global FinalizeInheritance
- [x] 阶段 4: 端到端验证：清空 zpkg cache → build-stdlib.sh 干净启动 5/5
- [x] 阶段 5: 全量回归（dotnet test / test-vm.sh / cargo test / test-dist 全绿）
- [x] 阶段 6: 文档同步 + 归档

---

## 阶段 1: ImportedSymbolLoader Combine + Empty helpers

- [x] 1.1 新增 `public static ImportedSymbols Empty()` — 全空 dict 构造
- [x] 1.2 新增 `public static ImportedSymbols Combine(ImportedSymbols low, ImportedSymbols high)`
  — high 优先；按 Decision 5 实现

## 阶段 2: SymbolTable 跟踪 imported interface / func names

- [x] 2.1 SymbolCollector 新增 `_importedInterfaceNames` + `_importedFuncNames` +
  `_importedEnumNames`，MergeImported 时填入
- [x] 2.2 SymbolTable 暴露 ImportedInterfaceNames / ImportedFuncNames /
  ImportedEnumNames（与已有 ImportedClassNames 对称）
- [x] 2.3 新增 `SymbolTable.ExtractIntraSymbols(string namespace)` 方法 →
  返回 ImportedSymbols 仅含本包 declarations（用 ImportedXxxNames 过滤）

## 阶段 3: PackageCompiler 两阶段编译

- [x] 3.1 抽取 `LoadExternalImported(tsigCache)` helper
- [x] 3.2 `TryCompileSourceFiles` 改两阶段：parse all + Phase 1 collect intraSymbols / Phase 2 完整编译
- [x] 3.3 `CompileFile` 签名增 `string source` + `ImportedSymbols? imported`，
  避免 Phase 2 重复读盘

## 阶段 3.5: 跨 CU 多级继承合并（实施中发现）

发现：Phase 1 per-CU 独立 SymbolCollector 时，CU 处理顺序与继承顺序不一致
导致 `SymbolCollector` 第二阶段 `if (!_classes.TryGetValue(base, out _)) continue;`
静默跳过合并。多级继承（ArgumentNullException → ArgumentException → Exception）
任意一级断链就丢失继承字段，`this.Message` 报 E0402。

修复：

- [x] 3.5.1 `SymbolCollector.Classes.cs` 新增 public `FinalizeInheritance()` —
  全局拓扑合并 `_classes` 内所有 class 的继承字段/方法，幂等
- [x] 3.5.2 `PackageCompiler` Phase 1 改用 **共享** `SymbolCollector` 实例，
  所有 CU 累积进同一 `_classes`；Phase 1 末尾调 `FinalizeInheritance()`
- [x] 3.5.3 `ExtractIntraSymbols` 从最新 `SymbolTable` 一次性抽取（base
  chain 已展开完整）

## 阶段 4: 端到端验证：清空 cache 自启动

- [x] 4.1 `rm -rf artifacts/z42/libs/*.zpkg src/libraries/*/dist`
- [x] 4.2 `./scripts/build-stdlib.sh` 一次成功 5/5（z42.core / z42.io / z42.math /
  z42.text / z42.collections）
- [x] 4.3 `./scripts/build-stdlib.sh --use-dist` 同样 5/5

## 阶段 5: 全量回归

- [x] 5.1 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` —— 585/585 通过
- [x] 5.2 `./scripts/test-vm.sh` —— 186/186 通过（93 interp + 93 jit）
- [x] 5.3 `cargo test --manifest-path src/runtime/Cargo.toml` —— 61/61 通过
- [x] 5.4 `./scripts/test-dist.sh` —— 186/186 通过（dist 端到端）

## 阶段 6: 文档同步 + 归档

- [x] 6.1 `docs/design/compiler-architecture.md` 新增"多 CU 包内 symbol 共享" 小节
  （两阶段编译 + FinalizeInheritance + ExtractIntraSymbols + 兼容性）
- [x] 6.2 tasks.md 状态 → `🟢 已完成`
- [x] 6.3 归档 + commit + push（scope `fix(compiler)`）

## 备注

- **关键发现（3.5）**：per-CU SymbolCollector 在多级继承场景下不足以保证字段展开。
  根因是 SymbolCollector 第二阶段只看当前 CU 的 cu.Classes 且依赖 _classes
  已含 base —— CU 顺序敏感。修复用 shared collector + global topological
  merge，与 C# / Java 编译器"先建骨架再填字段"的模式一致。
- 本变更解锁 `fix-generic-interface-dispatch`（已 stash 保留）可恢复推进。
