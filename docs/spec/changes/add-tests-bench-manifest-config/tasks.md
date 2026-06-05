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

- [ ] 3.1 [xtask_test.z42](../../../../scripts/xtask_test.z42)：
    - [ ] 3.1.1 测试发现循环：识别 `tests/<name>/source.z42` dir-mode
    - [ ] 3.1.2 dir-mode 测试编译路径：合成 synthetic mini-manifest 至 cache，传 `--manifest` 给 z42c
    - [ ] 3.1.3 [tests.dependencies] + [[test]].dependencies 三层 dep 合并（合入合成 manifest 的 [dependencies]）
    - [ ] 3.1.4 合成 manifest 强制 `[build].output_dir = <parent.output_dir>/tests` — 不可被 [[test]] 覆盖
    - [ ] 3.1.5 zpkg 命名：单文件测试 `<lib>.test.<filename_no_ext>`；dir-mode `<lib>.test.<dirname>`
- [ ] 3.2 [xtask_bench.z42](../../../../scripts/xtask_bench.z42)：同 3.1，识别 `bench/*.z42` + `bench/<name>/source.z42`；合 [bench.dependencies]；output_dir 走 `<parent>/bench`
- [ ] 3.3 sort 修复：现有 `Directory.Enumerate` 接 first-wins → 加显式 sort（common-pitfalls §1）
- [ ] 3.4 [xtask.z42](../../../../scripts/xtask.z42)：`clean tests` / `clean bench` 子命令
- [ ] 3.5 xtask 单元测试 / integration smoke
- [ ] 3.6 commit + push（xtask dir-mode 单元）

## Phase 4 — 22 stdlib z42.toml 迁移

- [ ] 4.1 编写迁移脚本（一次性 helper，**不**入仓库）：自动把 z42.test 从 `[dependencies]` 挪到 `[tests.dependencies]`
- [ ] 4.2 逐包 commit（22 包）：
    - [ ] z42.cli
    - [ ] z42.collections
    - [ ] z42.compression
    - [ ] z42.core
    - [ ] z42.crypto
    - [ ] z42.diagnostics
    - [ ] z42.encoding
    - [ ] z42.io
    - [ ] z42.io.binary
    - [ ] z42.json
    - [ ] z42.math
    - [ ] z42.net
    - [ ] z42.numerics
    - [ ] z42.random
    - [ ] z42.regex
    - [ ] z42.test（自身不需迁移；其他包依赖它）
    - [ ] z42.text
    - [ ] z42.threading
    - [ ] z42.time
    - [ ] z42.toml
    - [ ] z42.uri
    - [ ] z42.yaml
- [ ] 4.3 每包迁移后跑 `xtask test stdlib <name>` 单包验证
- [ ] 4.4 全量验证：`xtask test stdlib`（22 包）零 WS012 触发 + 全绿

## Phase 5 — 多文件测试 demo

- [ ] 5.1 选一个有意义的 stdlib 包（推荐 z42.crypto 或 z42.compression）改造一个测试为 dir-mode
- [ ] 5.2 验证 dir-mode 路径全跑通

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
