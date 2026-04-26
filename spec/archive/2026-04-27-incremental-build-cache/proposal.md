# Proposal: source_hash 增量编译（cache 真正起到加速作用）

## Why

371a3ce 让 cache 跨 packed/indexed 一致存在，但 `TryCompileSourceFiles` 仍**完全不查 cache**，每次构建全量 parse + typecheck + irgen。当前 source_hash 字段只是被记录，没人读。

stdlib 5 个包每次构建约 5-8 秒，多数文件未变动。开启增量后可降到 < 1 秒（仅扫 source_hash 比对）。

## What Changes

| 变更 | 说明 |
|---|---|
| **`IncrementalBuild` 模块**（新增） | 给定 sourceFile + 上次 zpkg 路径，计算 SHA-256 hash 并比对上次产物中的 source_hash；命中则返回 cached IrModule + ExportedModule |
| **`TryCompileSourceFiles` 增量分流** | 编译前按 source_hash 把 sourceFiles 分两路：cachedFiles（命中）+ freshFiles（未命中或首次） |
| **cached CU 重建** | 从 `cache/<rel>.zbc` 读 IrModule（ZbcReader.Read 已支持）+ 从上次 `dist/<name>.zpkg` 读对应 ExportedModule（ZpkgReader.ReadTsig 已支持） |
| **Symbol 注入** | cached CU 的 ExportedModule 通过 `externalImported`（同 LoadExternalImported 路径）注入 sharedCollector，让 fresh CU 能引用 cached CU 的类型 |
| **`--no-incremental` flag** | 强制全量重编（已有 BuildOptions 字段，C4a 占位；本变更激活实现） |
| **诊断输出** | 编译日志含 `cached: <count>/<total> files` 让开发者看到增量命中率 |

## Scope（允许改动的文件）

### 新增（NEW）

| 文件路径 | 说明 |
|---------|------|
| `src/compiler/z42.Pipeline/IncrementalBuild.cs` | source_hash 比对 + zbc 反序列化 + ExportedModule 加载 |
| `src/compiler/z42.Tests/IncrementalBuildTests.cs` | hash 比对 / cache miss / 命中后跳过 / `--no-incremental` |
| `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs` | 端到端：第一次全量 + 第二次未变 → 全部命中 + 修改一个文件 → 仅该文件重编 |

### 修改（MODIFY）

| 文件路径 | 说明 |
|---------|------|
| `src/compiler/z42.Pipeline/PackageCompiler.cs` | `TryCompileSourceFiles` 调用 IncrementalBuild 分流；cached CU 注入 externalImported；fresh CU 走原 Phase1+2 流程 |
| `src/compiler/z42.Pipeline/WorkspaceBuildOrchestrator.cs` | 当前 `BuildOptions` 已有 Release/CheckOnly；新增 `Incremental: bool = true`，传到 PackageCompiler.RunResolved |
| `src/compiler/z42.Driver/BuildCommand.cs` | `--no-incremental` flag 解析并传入 BuildOptions |

### 只读引用

| 文件路径 | 用途 |
|---------|------|
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.cs` | Read(byte[]) 恢复 IrModule |
| `src/compiler/z42.Project/ZpkgReader.cs` | ReadMeta + ReadTsig 拿 source_hash 表 + ExportedModules |

## Out of Scope

- **跳过 typecheck 阶段**（更激进的增量）—— 需要 SymbolCollector 重构，留 future
- **依赖图增量**：一个 lib 改了 → 依赖它的 lib 也要重编 —— 当前已由 manifest_hash / upstream_zpkg_hash 自然保证（C4a 设计 D4a.3 三层判定的概念，本 spec 只实现第一层 source_hash）
- **partial recompile**：部分类/方法改 → 仅该类重编 —— 文件级粒度足够，类级太复杂
- **cross-process locking**：并行 z42c 同时跑同一 workspace 时的 cache 写竞争 —— 后续视需求加

## Open Questions

无。

## 决策记录

| # | 决策 | 选择 |
|---|---|---|
| D1 | 增量粒度 | 文件级（`.z42` 一个文件一个 cache 单元） |
| D2 | source_hash 算法 | SHA-256（ZbcFile.SourceHash 已用） |
| D3 | cache 失效时机 | source_hash 不匹配 / cache zbc 不存在 / 上次 zpkg 不存在 / 上次 zpkg 中无对应 ExportedModule |
| D4 | 默认开关 | 默认开启增量；`--no-incremental` 强制全量 |
| D5 | cached CU 的符号注入路径 | 复用 externalImported 机制（同 ExportedModule 形式），不引入新机制 |
| D6 | 命中率展示 | stderr 输出 `cached: N/M files`；不输出每文件名（避免噪声） |
