# Tasks: Aggregate zpkg TIDX

> 状态：🟢 已完成 | 创建：2026-06-06 | 完成：2026-06-07 | 类型：vm（wire format 变更，zpkg minor bump）
>
> 实现已随 commit `0b453b2e`（writer/reader/bump/单测/fixture regen）+ `7b2a1b2a`（secp256k1 dir-mode demo + GoldenTests dir-mode 修复）落地。GREEN：dotnet 1533/1533 · stdlib dogfood 265/265（含 secp256k1 10/10）· cross-zpkg 2/2 · VM goldens 全绿（含 `OK: secp256k1`）。

## 进度概览
- [x] 阶段 1: Writer（C# ZpkgWriter 写 tidx_len + tidx_data per module）
- [x] 阶段 2: Reader（Rust read_mods_section + load_zpkg_bytes 聚合 + remap）
- [x] 阶段 3: zpkg minor bump 联动（VersionMinor / ZPKG_VERSION_MINOR / zpkg.md changelog）
- [x] 阶段 4: 单元测试（loader_tests::zpkg_tidx_*，6 个）+ fixture regen
- [x] 阶段 5: Phase 5 demo 重做（secp256k1 dir-mode）+ 全量 stdlib regen + GREEN
- [x] 阶段 6: 归档 + 把 add-tests-bench-manifest-config Phase 5 标 done + commit + push

## 阶段 1: Writer
- [x] 1.1 `ZpkgWriter.Sections.cs:BuildModsSection`：foreach module 末尾追加 `tidx_len: u32 + tidx_data` —— 用 `ZbcWriter.BuildTidxSection(mod.TestIndex, pool)`；mod.TestIndex 为 null/empty 时写 tidx_len = 0
- [x] 1.2 验证 `dotnet build src/compiler/z42.slnx` 0 error

## 阶段 2: Reader
- [x] 2.1 `zbc_reader.rs:read_mods_section`：foreach module 末尾读 `tidx_len = c.read_u32()?`；tidx_len > 0 时 `c.read_bytes(tidx_len)?.to_vec()`，否则 `Vec::new()`；返回类型从 `Vec<(Module, String)>` 改为 `Vec<(Module, String, Vec<u8>)>`
- [x] 2.2 `loader.rs:load_zpkg_bytes`：拿三元组后，计算 per-module function offset + string offset 累积值（按 module 顺序），foreach module 调 `test_index::read_test_index(tidx_bytes)`，每 entry 加 offset 后 push 到 aggregated `Vec<TestEntry>`
- [x] 2.3 在 `merge_modules` 之后 / 返回 LoadedArtifact 之前，调 `resolve_test_index_strings(&mut aggregated, &module.string_pool)`
- [x] 2.4 删 `// R1: ... deferred ...` 注释 + `test_index: vec![]`，替换为 `test_index: aggregated`
- [x] 2.5 `cargo build --release` 0 error；`cargo test --lib` 全过

## 阶段 3: zpkg minor bump
- [x] 3.1 `ZpkgWriter.cs`：`VersionMinor 10 → 11`，常量注释加 _"2026-06-XX aggregate-zpkg-tidx: 每 module MODS 段追加 tidx_len + tidx_data"_
- [x] 3.2 `zbc_reader.rs`：`ZPKG_VERSION_MINOR 10 → 11`
- [x] 3.3 `docs/design/runtime/zpkg.md`：Minor changelog 加 `0.11 | 2026-06-XX | aggregate-zpkg-tidx | per-module TIDX bytes appended to MODS` 一行；MODS 段 layout 段加 tidx 列说明

## 阶段 4: 单元测试 + fixture regen
- [x] 4.1 `src/runtime/src/metadata/loader_tests.rs`（或新文件）：5 个测试
  - [x] 4.1.1 `zpkg_tidx_empty` — 0 module 0 test
  - [x] 4.1.2 `zpkg_tidx_single_module` — 1 module N test
  - [x] 4.1.3 `zpkg_tidx_multi_module_method_id_remap` — 2 module function offset 重映射
  - [x] 4.1.4 `zpkg_tidx_multi_module_str_remap` — 2 module string offset 重映射 + resolve
  - [x] 4.1.5 `zpkg_tidx_zero_len_skips` — 部分 module tidx_len = 0
- [x] 4.2 跑 `src/tests/zpkg-format/generate-fixtures.sh` 重生 4 个 fixture
- [x] 4.3 `dotnet test --filter "FullyQualifiedName~Z42.Tests.Zpkg"` 全过（FormatGoldenTests + FormatInvariantTests）
- [x] 4.4 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj`（全部）全绿

## 阶段 5: Phase 5 demo 重做 + 全量 stdlib regen + GREEN
- [x] 5.1 `xtask regen` —— 全 stdlib + 测试 zpkg regen（minor bump 强制）
- [x] 5.2 `xtask test stdlib` —— 22 包全绿（验证旧 zpkg 都不在了，新 zpkg 都 load 得回去 + TIDX 正确）
- [x] 5.3 Phase 5 demo 重做：
  - [x] 5.3.1 创建 `src/libraries/z42.crypto/tests/secp256k1/source.z42`（之前 55e318fc 退回的内容）
  - [x] 5.3.2 创建 `src/libraries/z42.crypto/tests/secp256k1/vectors.z42`（同）
  - [x] 5.3.3 删 `src/libraries/z42.crypto/tests/ecdsa_secp256k1_vectors.z42`
- [x] 5.4 `xtask test stdlib z42.crypto -k secp256k1 --no-build` —— 10 个 [Test] 全过（验证 dir-mode 端到端）
- [x] 5.5 `xtask test all` —— compiler + vm + cross-zpkg + stdlib 全绿

## 阶段 6: 归档 + commit + push
- [x] 6.1 `docs/spec/changes/aggregate-zpkg-tidx/` → `docs/spec/archive/2026-06-XX-aggregate-zpkg-tidx/`
- [x] 6.2 `docs/spec/changes/add-tests-bench-manifest-config/tasks.md` Phase 5 标 ✅ done（pointer 到本 spec archive）
- [x] 6.3 commit + push（含 `.claude/` 与 `docs/spec/`）

## 备注

- 估时上修（vs 原 tasks.md 1-2 day 估算）：3-4 天，因 (1) zpkg minor bump 触发 6+4 fixture regen + 4 docs 同步；(2) reader 端聚合 + 字符串解析 + remap 逻辑需要 5 个单测覆盖；(3) Phase 5 demo 重做 + xtask test stdlib 全 22 包 GREEN 验证。
- 跨 module 同名函数 dedup 处理留 follow-up：当前 stdlib 不触发，初版只做 saturating_add + 越界 bail。真正出 case 时再补 dedup-safe warn + drop（design.md Decision 4）。
- `[[test]].dependencies` 三层合并仍不在本 spec scope —— Phase 5 demo 不需要，留 follow-up。
