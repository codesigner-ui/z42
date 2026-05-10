# Tasks: source_hash 增量编译

> 状态：🟢 已完成 | 创建：2026-04-26 | 归档：2026-04-27 | GREEN: dotnet 717/717 + cross-zpkg 1/1 (incremental) + VM 188/188

## 实施备注（与 spec 的差异 / 关键发现）

1. **cache zbc 用 fullMode 而非 stripped**：BuildPacked 写 cache zbc 用 ZbcFlags.None，让
   ZbcReader.Read 单独反序列化为完整 IrModule（含 fn.Name / TypeParams / LocalVarTable）。
   stripped zbc 缺 SIGS 全局表，函数名会变 `func#<idx>` 占位 → cross-zpkg VCall 失败。
   BuildIndexed 仍 stripped（zpkg.files[] 引用，VM 通过 zpkg 全局 SIGS 加载，不破坏既有兼容）。

2. **cached CU 的 UsedDepNamespaces 必须从上次 zpkg.Dependencies 恢复**：否则
   BuildDependencyMap 派生的 zpkg.Dependencies 为空 → VM 找不到外部 zpkg 中的函数。
   IncrementalBuild.ProbeResult 增加 LastZpkgDepNamespaces 字段。

3. **新增 ZpkgReader.ReadSourceHashes**：跨 indexed/packed 通用读取 (sourceFile, sourceHash,
   namespace)。indexed 读 FILE section（namespace 留空，由 ZbcReader.ReadNamespace 兜底），
   packed 读 MODS section header。

4. **CompilerUtils.Sha256Hex 复用**：保证 hash 格式与 ZpkgWriter 写入一致（`"sha256:<hex>"`）。
   早期自实现纯 hex 导致 100% miss；改 CompilerUtils.Sha256Hex 后命中。

5. **path 形态兼容**：zpkg 存的 sourceFile 可能绝对或相对；hashBySource 同时按 abs + rel 索引。

6. **ZbcWriter stripped 模式 LineTable.File intern bug 沿途修复**（371a3ce 已 commit；本 spec
   依赖该修复）：stripped 模式 InternPoolStrings 漏掉 fn.LineTable[].File，但 BuildFuncSection
   无条件 pool.Idx → KeyNotFoundException。已在 stripped 路径补 intern。

## 进度概览
- [ ] 阶段 1: IncrementalBuild 模块 + 单元测试
- [ ] 阶段 2: TryCompileSourceFiles 接入分流
- [ ] 阶段 3: BuildOptions / CLI 接入 --no-incremental
- [ ] 阶段 4: 集成测试 + 命中率输出
- [ ] 阶段 5: 文档同步

---

## 阶段 1: IncrementalBuild 模块

- [ ] 1.1 新增 `src/compiler/z42.Pipeline/IncrementalBuild.cs`：ProbeResult record + Probe(...) 方法
- [ ] 1.2 SHA-256 hash 计算（与 ZpkgWriter 既用算法一致；如已有 helper 直接复用）
- [ ] 1.3 失效条件实现：zpkg 不存在 / hash 不匹配 / zbc 缺失 / ExportedModule 缺失
- [ ] 1.4 全 fresh fallback：`ProbeResult.AllFresh(files)` 静态构造
- [ ] 1.5 单元测试 `src/compiler/z42.Tests/IncrementalBuildTests.cs`：6 个 Case 覆盖（见 design.md 测试表）

## 阶段 2: TryCompileSourceFiles 接入

- [ ] 2.1 修改 `TryCompileSourceFiles` 签名：增加 `IReadOnlyDictionary<string, ExportedModule>? cachedExports`
- [ ] 2.2 把 cachedExports 注入 sharedCollector 的 externalImported（与 LoadExternalImported 输出 merge）
- [ ] 2.3 fresh CU 走原 Phase1+2，输出 List<CompiledUnit>
- [ ] 2.4 修改 `BuildTarget`：调 IncrementalBuild.Probe → 分流 → 重建 cached CU → 合并 → BuildPacked / BuildIndexed
- [ ] 2.5 修改 `RunResolved` / `Run`：增加 useIncremental bool 参数（默认 true）
- [ ] 2.6 cached CU 重建：用 ZbcReader.Read 解析 zbc bytes，命名空间从 IrModule.Name 取

## 阶段 3: BuildOptions / CLI 接入

- [ ] 3.1 修改 `WorkspaceBuildOrchestrator.BuildOptions`：增加 `Incremental: bool = true`
- [ ] 3.2 修改 `BuildCommand.Create` / `CreateCheck`：新增 `--no-incremental` Option
- [ ] 3.3 把 BuildOptions.Incremental 传给 `RunResolved` / `Run`
- [ ] 3.4 单工程模式（无 workspace）也支持 --no-incremental

## 阶段 4: 集成测试 + 命中率输出

- [ ] 4.1 编译过程 stderr 输出 `cached: N/M files`
- [ ] 4.2 新增 `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs`：
  - 首次构建：cached 0/N
  - 立即重建：cached N/N
  - 修改一个文件：cached (N-1)/N
  - --no-incremental 强制 0/N
  - 单工程模式同样有效
- [ ] 4.3 验证 stdlib 实际增量：`./scripts/build-stdlib.sh` 第二次跑显著加速（仅打日志，无 assert）

## 阶段 5: 文档同步

- [ ] 5.1 修改 `docs/design/compiler-architecture.md`：ManifestLoader 模块表追加 IncrementalBuild；说明文件级粒度 + 失效条件
- [ ] 5.2 修改 `docs/design/project.md`：在 L6 末尾或独立章节说明增量构建机制 + `--no-incremental` flag
- [ ] 5.3 修改 `docs/dev.md`：示例命令补 `--no-incremental`
- [ ] 5.4 修改 `docs/roadmap.md`：标记增量编译落地

---

## 验证清单（GREEN）

- [ ] `dotnet build src/compiler/z42.slnx` 无错
- [ ] `cargo build --manifest-path src/runtime/Cargo.toml` 无错
- [ ] `dotnet test src/compiler/z42.Tests/` 全绿
- [ ] `./scripts/test-vm.sh` 全绿
- [ ] `./scripts/test-cross-zpkg.sh` 全绿
- [ ] `./scripts/build-stdlib.sh` 第二次跑命中 5/5（手动观察 cached 输出）

## 备注

- 本 spec 仅实施"读 cache 跳过 parse + typecheck + irgen"，是真正的增量第一步
- typecheck 的 SymbolCollector 在重建 cached CU 时**不参与**（cached 的符号通过 ExportedModule 注入 externalImported）
- 后续 manifest_hash / upstream_zpkg_hash 增量留独立 spec
