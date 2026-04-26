# Design: source_hash 增量编译

## Architecture

```
PackageCompiler.RunResolved / Run
  ├─ resolve sourceFiles + cacheDir + outDir + version + name
  ├─ ★ NEW: IncrementalBuild.Probe(sourceFiles, lastZpkgPath, cacheDir)
  │     - 加载上次 zpkg.Files[] (file → SourceHash 表) + ExportedModules
  │     - 对每个 sourceFile:
  │         · SHA-256(file content) 与表中 source_hash 比对
  │         · cache/<rel>.zbc 存在
  │         · ExportedModule (按 namespace) 存在
  │         · 三者皆满足 → cached
  │     - 输出 (cachedSet, freshSet, cachedExportsByNs, cachedZbcByFile)
  ├─ TryCompileSourceFiles(freshSet, depIndex, tsigCache, cachedExports?)
  │     ├─ Phase 1: parse fresh + sharedCollector.Collect
  │     │     - cachedExports 通过 externalImported-like 路径注入 sharedCollector
  │     ├─ Phase 2: typecheck + irgen for fresh
  │     └─ 输出 fresh CompiledUnits
  ├─ Reconstruct cached CompiledUnits
  │     - foreach (file ∈ cachedSet): zbc = ZbcReader.Read(cachedZbcByFile[file])
  │     - new CompiledUnit { Module = zbc, Namespace = ..., ExportedTypes = cachedExportsByNs[ns], ... }
  ├─ allUnits = cachedUnits ∪ freshUnits
  └─ BuildPacked / BuildIndexed (不变)
```

## Decisions

### D1: 增量粒度 = 文件级

每个 .z42 文件作为 cache 单元。理由：
- z42 文件级独立性强（一个文件一个 namespace 通常）
- 类级粒度复杂得多（追踪类内部依赖图）
- 文件级足够覆盖 stdlib 与典型用户工程

### D2: SHA-256 算法

- 与 ZbcFile.SourceHash 既有用法一致
- 已被 ZpkgWriter / ZpkgReader 序列化
- 无需引入新 hash 库

### D3: cache 失效条件（保守判断）

任一不满足即视为 fresh：

1. 上次 zpkg `<dist>/<name>.zpkg` 存在
2. 上次 zpkg.Files 中含该 sourceFile 的 SourceHash
3. SHA-256(当前文件内容) == 记录的 SourceHash
4. `<cache>/<rel>.zbc` 文件存在
5. zpkg.ExportedModules 中含该文件 namespace 对应的 ExportedModule

宁可 fresh 不可错误命中（错误命中会导致编译产物逻辑不一致）。

### D4: 默认开启 + `--no-incremental` 强制全量

- 默认开（用户不感知）
- 调试或 CI 重现时可禁用

### D5: cached CU 符号注入复用 externalImported 路径

`PackageCompiler` 已有从外部 zpkg 加载 ExportedModule 注入 sharedCollector 的路径（`LoadExternalImported`）。把"同包但 cached"的 ExportedModule 一起注入：

```csharp
var externalImported = LoadExternalImported(tsigCache);
foreach (var (ns, exported) in cachedExportsByNs)
    externalImported.AddOrMerge(ns, exported);
```

不引入新机制，最小破坏性。

### D6: 命中率展示

```
   Compiling z42.core v0.1.0 [release]
cached: 28/30 files
wrote → /path/to/dist/z42.core.zpkg
    Finished → /path/to/dist
```

`cached: 0/N` 表示首次或全量。`cached: N/N` 表示完全命中（理想状况下不打 zpkg？—— 仍打，因为 zpkg 是最终产物的物理 manifest，规则保持简单）。

## Implementation Notes

### IncrementalBuild.cs

```csharp
public sealed class IncrementalBuild
{
    public sealed record ProbeResult(
        IReadOnlyDictionary<string, byte[]> CachedZbcByFile,    // sourceFile → zbc bytes
        IReadOnlyDictionary<string, ExportedModule> CachedExportsByNs,
        IReadOnlyList<string> FreshFiles,
        int CachedCount, int TotalCount);

    public ProbeResult Probe(
        IReadOnlyList<string> sourceFiles,
        string projectDir,
        string cacheDir,
        string lastZpkgPath);
}
```

实现要点：
- 上次 zpkg 不存在 / 解析失败 → 全部 fresh
- ExportedModules 按 IrModule.Name（即 namespace）索引
- `<cache>/<rel>.zbc` 路径通过 `Path.GetRelativePath(projectDir, sourceFile)` + `.zbc` 派生（与 ZpkgBuilder 写入时同 convention）

### TryCompileSourceFiles 改造

```csharp
static List<CompiledUnit>? TryCompileSourceFiles(
    IReadOnlyList<string> sourceFiles,
    DependencyIndex depIndex,
    TsigCache? tsigCache = null,
    IReadOnlyDictionary<string, ExportedModule>? cachedExports = null)   // ★ 新参数
{
    var externalImported = LoadExternalImported(tsigCache);
    if (cachedExports is not null)
    {
        foreach (var (ns, exp) in cachedExports)
            // 注入到 externalImported（与 LoadExternalImported 输出形态一致）
            ...
    }
    // ... 原 Phase 1+2 流程，sourceFiles 仅含 fresh
}
```

### PackageCompiler.BuildTarget 改造

```csharp
static int BuildTarget(...)
{
    string lastZpkgPath = Path.Combine(outDir, $"{name}.zpkg");
    var probe = useIncremental
        ? new IncrementalBuild().Probe(sourceFiles, projectDir, cacheDir, lastZpkgPath)
        : IncrementalBuild.ProbeResult.AllFresh(sourceFiles);

    Console.Error.WriteLine($"cached: {probe.CachedCount}/{probe.TotalCount} files");

    var freshUnits = TryCompileSourceFiles(probe.FreshFiles, depIndex, tsigCache, probe.CachedExportsByNs);
    if (freshUnits is null) return 1;

    var cachedUnits = probe.CachedZbcByFile.Select(kv =>
        new CompiledUnit(
            File: kv.Key,
            Module: ZbcReader.Read(kv.Value),
            Namespace: ...,
            ExportedTypes: probe.CachedExportsByNs[ns],
            ...)).ToList();

    var allUnits = freshUnits.Concat(cachedUnits).ToList();
    // 后续：构建 zpkg 等不变
}
```

### BuildOptions / CLI 参数

```csharp
// WorkspaceBuildOrchestrator.BuildOptions：新增 bool Incremental = true
// PackageCompiler.RunResolved / Run / BuildTarget：增加 useIncremental 参数
// BuildCommand.Create / CreateCheck：增加 --no-incremental flag
```

## Testing Strategy

### 单元测试（IncrementalBuildTests）

| Case | 验证 |
|---|---|
| 上次 zpkg 不存在 | 全 fresh |
| zpkg 存在但 hash 不匹配 | 该文件 fresh |
| zbc 缺失 | 该文件 fresh |
| ExportedModule 缺失 | 该文件 fresh |
| 完整命中 | cached |
| 部分命中 | mixed cached + fresh |

### 集成测试（IncrementalBuildIntegrationTests）

| Case | 验证 |
|---|---|
| 首次构建 | `cached: 0/N`，所有文件 fresh |
| 立即重建 | `cached: N/N`，跳过 typecheck（用 mock 注入计数） |
| 修改一个文件 | `cached: (N-1)/N` |
| `--no-incremental` | `cached: 0/N` 即使 cache 存在 |
| stdlib 端到端 | rm -rf dist + 第一次 build vs 第二次 build 时间对比（仅打日志，不严格 assert） |

## Open Risks

| 风险 | 缓解 |
|---|---|
| ZbcReader.Read 恢复的 IrModule 是否完整（含 line table / classes / functions） | 现有 ZpkgReader.ReadModules 已用 ZbcReader.Read，证明能恢复运行所需信息；spec 中明确 cached CU **不再过 typecheck**，仅作为 IR 提供方 |
| ExportedModule 在 zpkg 中按 namespace 索引，但同 namespace 跨多个文件时如何区分？ | 当前设计：cached_exports 是 namespace-level（与 ExportedModule 粒度一致），多文件同 namespace 视为整体；如其中一个文件 fresh，则该 namespace 视为整体重新生成 ExportedModule（即整 namespace 的所有文件都跑 typecheck） |
| sharedCollector 的 inheritance 在 cached / fresh 混合时是否一致 | 注入 cached 的 ExportedModule 走 externalImported（既有路径），与跨包导入逻辑一致 |
| cache 路径解析与 ZpkgBuilder 写入路径需精确对应 | 都用 `Path.GetRelativePath(projectDir, sourceFile)` + `.zbc`；新增单元测试覆盖 |

## 后续工作（不在本 spec）

- **跳过 typecheck**：当前 fresh CU 仍跑完整 typecheck；要彻底跳过需 SymbolCollector 重构（让符号表能从 ExportedModule 直接构建而不需要 AST）
- **manifest_hash 增量**：member 的 manifest 或 include 链改动 → 全量重编（C4a tasks.md 中提及）
- **upstream_zpkg_hash 增量**：依赖的 member 改了 → 当前 member 至少要重链
- **partial recompile**（类级粒度） → 远期

## partial 语义兼容性（2026-04-26 用户澄清）

z42 后续将引入 `partial class`（C# 风格，编译期糖；运行时无 partial 概念）。
当前 spec 的设计需明确 cache 与 dist 两种 zbc 语义的区分，以兼容未来：

### zbc 语义分层

| 路径 | 内容 | 增量复用 |
|---|---|---|
| `<member>/cache/<rel>.zbc`（**fragment zbc**） | partial 来时：仅含本文件**贡献片段**的 IrModule（IrClassDesc 不完整） | ✅ source_hash 命中 → 跳过 parse + typecheck + irgen |
| `<member>/dist/<lib>.zpkg` 内的 `IrModule`（**merged**） | 所有 partial 片段合并后的**完整类型定义** | ❌ 不直接复用；总是从 fragment 合并 |
| indexed 模式下 `<member>/dist/<lib>/<canonical>.zbc`（**merged dist zbc**） | 同上（partial 来时与 cache zbc 不 1:1 对应；多个 fragment 合并到一个 canonical） | ❌ |

**当前（无 partial）特殊情形**：fragment ≡ merged（一文件一类、无合并需求）。所以本 spec 实施的"cache zbc 直接复用为 zpkg.modules[]"完全等价，且不影响后续 partial 引入。

### 后续 partial 实施需要的扩展点

| 扩展 | 说明 |
|---|---|
| `ZbcFlags.IsFragment`（新增标志位） | 标记 cache zbc 是 partial 片段；merged 后写出的 dist zbc 不带此标志 |
| `PartialMerger` 模块（新增） | 接收一组同类型的 fragment IrClassDesc，合并 fields / methods / interfaces；位置：`z42.Pipeline/PartialMerger.cs` |
| `BuildPacked` / `BuildIndexed` 在写 zpkg 前调 PartialMerger | 当前 spec 的接口已是 `IReadOnlyList<ZbcFile> zbcFiles`；partial 来时在此处插入合并阶段，不破坏现有接口 |
| `IncrementalBuild` 的 sibling 联动失效 | partial 类的兄弟文件 hash 变化 → 整组重编；通过类→文件多对多反向索引判断 |
| `ExportedModule` 已是 namespace 级（按 namespace 索引） | **天然支持** partial 跨文件合并视图，无需结构变更 |

### 关键不变量

- **运行时无 partial**：lazy_loader 永远看到 merged IrClassDesc；从 dist zpkg 读出的 IrModule 已是合并视图
- **cache 层 per-file**：增量编译粒度保持文件级；partial 仅影响"同类兄弟联动"判断
- **zpkg 是合并锚点**：partial 合并的最终物化在 zpkg 写出阶段，不在 cache 层；即使所有 partial 文件都命中 cache，写新 zpkg 时仍要把 fragment IrModule 重新走一遍 merger（merger 是纯函数，不耗时）

### 当前 spec 不实施

partial 语法本身（parser / typechecker / 关键字识别）+ PartialMerger 模块 + sibling 联动失效——均为独立 spec。本 spec 的增量 cache 实现严格保持"cache zbc 直接 inline 到 zpkg"的当前流程；partial 来时只需在 BuildPacked / BuildIndexed 前**插入** PartialMerger，不改本 spec 已实现的代码路径。
