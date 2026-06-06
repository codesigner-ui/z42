# Tasks: add-tests-bench-manifest-config

> 状态：🟡 进行中 | 创建：2026-06-06 | 类型：lang/compiler/build feat

## Phase 1 — Spec（本提交）

- [x] 1.1 proposal.md：设计 + 约定 + schema + 错误码 + 编译模型 + 迁移
- [x] 1.2 tasks.md：本文件
- [ ] 1.3 commit + push

## Phase 2 — C# 编译器 schema 扩展

- [ ] 2.1 [ManifestErrors.cs](../../../../src/compiler/z42.Project/ManifestErrors.cs)：添加 WS012 + WS040 + WS041 + WS042 + WS043 错误码常量 + factory 方法
- [ ] 2.2 [ProjectManifest.cs](../../../../src/compiler/z42.Project/ProjectManifest.cs)：
    - [ ] 2.2.1 新增 record：`TestsConfig` / `BenchConfig` / `TestEntry` / `BenchEntry`（含 `Name` / `Src` / `Sources` / `Dependencies`）
    - [ ] 2.2.2 `ProjectManifest` 字段加 `Tests`, `Bench`, `TestEntries`, `BenchEntries`
    - [ ] 2.2.3 `KnownTopLevelKeys` 加 `"tests"`, `"bench"`, `"test"`, `"benchmark"`
    - [ ] 2.2.4 `KnownTestsKeys` / `KnownBenchKeys`（include / exclude / dependencies）
    - [ ] 2.2.5 `KnownTestEntryKeys` / `KnownBenchEntryKeys`（name / src / sources / dependencies）
    - [ ] 2.2.6 `ParseTests` / `ParseBench` / `ParseTestEntries` / `ParseBenchEntries` helper（依现有 helper 模式）
    - [ ] 2.2.7 `LoadWithWarnings` 中扫描 `[dependencies]`：若包含已知 test-only deps（z42.test）→ emit WS012
    - [ ] 2.2.8 [[test]] / [[bench]] 缺 name / src → WS040 / WS041；重名 → WS042；src 不存在 → WS043
- [ ] 2.3 C# 单元测试（[z42.Tests](../../../../src/compiler/z42.Tests)）：
    - [ ] 2.3.1 ProjectManifestTests：parse [tests] / [bench] / [[test]] / [[bench]] 各组合
    - [ ] 2.3.2 ManifestErrorsTests：WS012 / WS040-WS043 round-trip
- [ ] 2.4 GREEN：`dotnet test src/compiler/z42.Tests/z42.Tests.csproj`
- [ ] 2.5 commit + push（compiler schema 单元）

## Phase 3 — xtask dir-mode 发现 + synthetic mini-manifest + 输出目录隔离

- [x] 3.1 [xtask_test.z42](../../../../scripts/xtask_test.z42)：
    - [x] 3.1.1 测试发现循环：识别 `tests/<name>/source.z42` dir-mode（`_discoverTestUnits`）
    - [x] 3.1.2 dir-mode 测试编译路径：合成 synthetic mini-manifest 至 cache，传 `z42c build <toml>`（`_compileTestUnit` + `_renderSyntheticManifest`）
    - [x] 3.1.3 [tests.dependencies] 双层 dep 合并（合入合成 manifest 的 [dependencies]；`_harvestParentDeps` + `_appendDeps`）— 三层（含 `[[test]].dependencies`）随 Phase 5 demo 引入实际用例时补
    - [x] 3.1.4 合成 manifest 强制 `[build].output_dir = <work>/<lib>/<unit>` —— xtask 自身写入，[[test]] 入口无法覆盖
    - [x] 3.1.5 zpkg 命名：单文件 `<lib>.test.<filename_no_ext>`；dir-mode `<lib>.test.<dirname>`
- [ ] 3.2 [xtask_bench.z42](../../../../scripts/xtask_bench.z42)：同 3.1（bench 路径独立 commit）
- [x] 3.3 sort 修复：`Directory.Enumerate` first-wins → `_sortedStrings` 显式 sort（common-pitfalls §1）
- [ ] 3.4 [xtask.z42](../../../../scripts/xtask.z42)：`clean tests` / `clean bench` 子命令
- [ ] 3.5 xtask 单元测试 / integration smoke
- [x] 3.6 commit + push（xtask dir-mode 单元）

## Phase 4 — 22 stdlib z42.toml 迁移

> 状态：✅ 已落地 by [`6aac4c5d stdlib: migrate 21 packages to [tests.dependencies] = z42.test`](https://github.com/codesigner-ui/z42/commit/6aac4c5d) (2026-06-06)。21 个使用 z42.test 的 stdlib 包统一在一个 commit 里完成迁移（z42.test 自身不需迁移）；spec 原"逐包 commit"细分降级为单 commit 批量执行，因为 21 处都是同形 diff + 必须一起绿才算成功，分 commit 反而不可拆。

- [x] 4.1 一次性 sed/awk migration（commit 内不入仓库，commit 即落地）
- [x] 4.2 21 包合并到 commit `6aac4c5d`（z42.test 自身不迁）
- [x] 4.3 / 4.4 全量验证：commit 落地时 `xtask test stdlib` 全绿；本 spec Phase 3 落地后再次跑 `xtask test stdlib z42.core --no-build` 通过 6/6（验证新 xtask + 已迁移 toml 仍 GREEN）

## Phase 5 — 多文件测试 demo

> 状态：🔴 BLOCKED — xtask 端 dir-mode 基础设施已完整（Phase 3 + Phase 5 polish 落地：dir-mode 发现 / synthetic manifest 写入到源目录旁 / 删除清理 / `[profile.*].pack = true` 强制 packed / `IsSyntheticHarnessProject` 抑制 WS012）；端到端阻塞在 VM 侧 zpkg loader 的 TIDX 聚合，2026-06-06 试跑 secp256k1 demo 时确认。
>
> **触发**：synthetic 多文件项目编译产出 packed zpkg；z42-test-runner 调 `load_artifact(zpkg)` 时，[src/runtime/src/metadata/loader.rs:312-321](../../../../src/runtime/src/metadata/loader.rs#L312-L321) 显式写 `test_index: vec![]` 并附注释 _"R1: zpkg test metadata aggregation deferred. R3 runner reads individual .zbc files directly via load_artifact, where TIDX sections are populated. Setting empty here is correct for now."_ 测试发现因此为空，runner 退出 3 (no tests found)。
>
> **要做的事**：在 `load_zpkg_bytes` 里枚举每个内嵌模块的 TIDX，按 merge_modules 已有的函数/字符串偏移规则 remap `method_id` + `skip_reason_str_idx`，concat 后 vec 一并填入 `LoadedArtifact.test_index`。需要一并暴露 `read_zpkg_modules` 已有但没向外露的 TIDX 段。预计 1-2 天工作量，应作为独立 spec 推出（`vm` 类型，需 spec-first）。
>
> Phase 5 demo 文件本身（`tests/secp256k1/{source,vectors}.z42`）在本次试跑后已恢复回单文件 `tests/ecdsa_secp256k1_vectors.z42`；落地 TIDX 聚合后再 re-introduce。

- [ ] 5.0 [起 spec `aggregate-zpkg-tidx`]：vm-side 改动，在 zpkg 加载时聚合多 module TIDX 段
- [ ] 5.1 demo 重做：选一个 stdlib 包（推荐 z42.crypto）改造一个测试为 dir-mode
- [ ] 5.2 验证 dir-mode 路径全跑通（compile → load_artifact → test discovery → run）

## Phase 6 — CI 守门 + 文档同步

- [ ] 6.1 [.github/workflows/](../../../../.github/workflows/) 新增 release-guard step（或并入现有 build-and-test）：
    - [ ] 6.1.1 `find dist/ -name '*.test.*.zpkg' -o -name '*.bench.*.zpkg'` 零命中
    - [ ] 6.1.2 `find tests/dist/` 反向：不许出现无 `.test.` infix 的 zpkg
- [ ] 6.2 [docs/design/compiler/project.md](../../../design/compiler/project.md) 同步新 schema：
    - [ ] 6.2.1 `[tests]` / `[bench]` 段语义
    - [ ] 6.2.2 `[[test]]` / `[[bench]]` 数组
    - [ ] 6.2.3 三层 dep 合并模型
    - [ ] 6.2.4 输出目录布局（与 §restructure-build-output-dirs 的 output_dir/cache_dir/dist_dir 衔接）
- [ ] 6.3 [docs/design/runtime/zpkg.md](../../../design/runtime/zpkg.md)（若存在）：说明 release 产物剥离 [tests] / [bench] 字段
- [ ] 6.4 `.claude/rules/` 是否需补 "test-only deps 必须用 [tests.dependencies]" 规则？（待评估）
- [ ] 6.5 spec 归档：docs/spec/changes/ → docs/spec/archive/

## 验收（GREEN）

- [ ] dotnet test src/compiler/z42.Tests/z42.Tests.csproj 全绿
- [ ] xtask test stdlib 全绿（22 包）
- [ ] xtask bench stdlib 跑通（per-package micro-bench）
- [ ] 全 stdlib 扫描 WS012 零触发
- [ ] 一个 dir-mode 多文件测试 demo 跑通
- [ ] CI release-guard step 零命中
- [ ] `xtask clean tests` / `clean bench` 独立可清，不影响生产产物
- [ ] roadmap.md / 0.3.x A 主线相关行同步
