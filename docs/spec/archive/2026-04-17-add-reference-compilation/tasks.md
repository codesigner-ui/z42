# Tasks: 引用编译（Reference Compilation）

> 状态：🟢 已完成 | 创建：2026-04-17

## 进度概览
- [x] I1: TSIG 写入
- [x] I2: TSIG 读取 + TypeChecker 注入
- [x] I3: 清理 — Stdlib → Dep 通用化重命名

## I1: TSIG 写入
- [x] 1.1 新增 `z42.IR/ExportedTypes.cs` — ExportedModule 等 record 定义
- [x] 1.2 `ZpkgTags` 新增 TSIG tag
- [x] 1.3 `ZpkgWriter` 新增 `BuildTsigSection()` — 从 SemanticModel 提取类型签名序列化
- [x] 1.4 `PackageCompiler` 传递 SemanticModel 到 zpkg 写入流程
- [x] 1.5 重编译 stdlib，验证 zpkg 包含 TSIG section
- [x] 1.6 dotnet build && dotnet test 全绿

## I2: TSIG 读取 + TypeChecker 注入
- [x] 2.1 `ZpkgReader` 新增 `ReadTsigSection()` — 反序列化 ExportedModule
- [x] 2.2 新增 `ImportedSymbolLoader` — ExportedModule → Z42ClassType/Z42FuncType 重建
- [x] 2.3 `SymbolCollector.Collect()` 扩展 — 接受 imported symbols
- [x] 2.4 `TypeEnv` 移除 `BuiltinClasses` 硬编码
- [x] 2.5 `PipelineCore` 扩展 — Compile/CheckAndGenerate 接受 imported symbols
- [x] 2.6 `PackageCompiler` 扩展 — 加载 TSIG → 构建 imported SymbolTable → 传入编译流程
- [x] 2.7 dotnet build && dotnet test && ./scripts/test-vm.sh 全绿

## I3: 清理 — Stdlib → Dep 通用化
- [x] 3.1 `StdlibCallIndex` → `DependencyIndex`，`StdlibCallEntry` → `DepCallEntry`
- [x] 3.2 `UsedStdlibNamespaces` → `UsedDepNamespaces`（IrGen/PipelineCore/PackageCompiler）
- [x] 3.3 `BuildStdlibIndex` → `BuildDepIndex`，所有 Stdlib 局部变量/参数/方法名
- [x] 3.4 `StdlibIndex` → `DepIndex`，`TrackStdlibNamespace` → `TrackDepNamespace`（IEmitterContext）
- [x] 3.5 文件重命名 `StdlibCallIndex.cs` → `DependencyIndex.cs`
- [x] 3.6 测试方法/变量重命名（IrGenTests/GoldenTests/ZbcRoundTripTests）
- [x] 3.7 dotnet build && cargo build && dotnet test && ./scripts/test-vm.sh 全绿
