# Proposal: Aggregate per-module TIDX into zpkg

## Why

`add-tests-bench-manifest-config` Phase 5（多文件 dir-mode test demo）的端到端阻塞点：xtask 已能产出 synthetic multi-file lib zpkg，但 `z42-test-runner` 加载该 zpkg 时发现 **0 tests** 并退出 3。

根因（2026-06-06 调查确认）：

1. **ZpkgWriter 根本不写 TIDX**。当前 [`ZpkgWriter.Sections.cs:BuildModsSection`](../../../src/compiler/z42.Project/ZpkgWriter.Sections.cs#L231) 在 MODS 段中按 module 顺序写 `ns_idx / src_idx / hash_idx / func_count / first_sig / FUNC / TYPE / DBUG / REGT` —— 没有 TIDX。
2. **zbc_reader 不读，loader 显式写空**。[`src/runtime/src/metadata/loader.rs:312-321`](../../../src/runtime/src/metadata/loader.rs#L312-L321) 的 `load_zpkg_bytes` 显式 `test_index: vec![]`，注释 _"R1: zpkg test metadata aggregation deferred"_。
3. 结果：单文件 emit-zbc 测试模式仍 work（.zbc 直接含 TIDX，loader 走 `load_zbc_bytes` 路径读到）；多文件 zpkg 测试模式全瞎。

## What Changes

1. **zpkg 0.10 → 0.11 minor bump**（strict-pin policy；旧 zpkg 不可读，需 regen 全 stdlib + 4 fixture）。
2. **每 module MODS 段尾追加 `tidx_len: u32` + `tidx_data: [u8; tidx_len]`**，平行 DBUG / REGT 已有的 len-prefixed pattern。`tidx_data` 直接复用 `ZbcWriter.BuildTidxSection` 的 byte 输出（method_id 仍 module-local —— reader 端按 cumulative function offset 聚合）。
3. **ZbcWriter / zbc minor 不动** —— TIDX wire format 本身不变（仍 v=3）。只是 TIDX bytes 现在也写到 zpkg 里。
4. **`read_mods_section` 读 tidx_len + tidx_data**，把每 module 的 TestEntry vec 累积到 `LoadedArtifact.test_index`；reader 端按 `function_offset = sum(prev modules' func_count)` 重映射 `method_id`（与 SIGS first_sig 同模式）；`*_str_idx` 字段按 `string_offset = sum(prev modules' string_pool.len())` 重映射。
5. **Phase 5 demo 重做**：`tests/secp256k1/{source.z42, vectors.z42}` 多文件测试落地。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | `VersionMinor` 10 → 11，常量旁注释本次 bump 触发 spec |
| `src/compiler/z42.Project/ZpkgWriter.Sections.cs` | MODIFY | `BuildModsSection` 在 REGT 后追加 `tidx_len` + `tidx_data`；用 `ZbcWriter.BuildTidxSection(mod.TestIndex, pool)` 序列化（0 长度 = 无 TIDX） |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | `ZPKG_VERSION_MINOR` 10 → 11；`read_mods_section` 读 tidx_len + tidx_data 后返回 `Vec<(Module, namespace, Vec<TestEntry>)>` 三元组（接口改） |
| `src/runtime/src/metadata/loader.rs` | MODIFY | `load_zpkg_bytes` 收到三元组后，按 cumulative function offset + cumulative string pool offset 聚合 TestEntry vec 到 `LoadedArtifact.test_index`；移除 `vec![]` 占位 |
| `src/runtime/src/metadata/loader_tests.rs`（或新文件） | NEW/MODIFY | 单元测试覆盖：空 TIDX / 单 module / 多 module method_id remap / 多 module str_idx remap |
| `docs/design/runtime/zpkg.md` | MODIFY | MODS 段 layout 表加 TIDX 列；Minor changelog 加 0.11 行（日期 / 触发 spec / 引入内容） |
| `src/tests/zpkg-format/generate-fixtures.sh` | RUN（不入仓） | 4 个 fixture 的 source.zpkg + expected.json regen |
| `src/tests/zpkg-format/fixtures/*/source.zpkg` | MODIFY | regen 输出（4 个 fixture，包括 expected.json） |
| `src/tests/zpkg-format/fixtures/*/expected.json` | MODIFY | regen 输出 |
| `src/libraries/z42.crypto/tests/secp256k1/source.z42` | NEW | Phase 5 demo 多文件测试入口 |
| `src/libraries/z42.crypto/tests/secp256k1/vectors.z42` | NEW | Phase 5 demo SecpVectors 静态类 |
| `src/libraries/z42.crypto/tests/ecdsa_secp256k1_vectors.z42` | DELETE | 被 dir-mode 替代 |
| `docs/spec/changes/add-tests-bench-manifest-config/tasks.md` | MODIFY | Phase 5 标 ✅ done（aggregate-zpkg-tidx 是 unblocker，本 spec 落地后顺手标） |
| `docs/spec/archive/2026-06-XX-aggregate-zpkg-tidx/` | NEW | 归档目录 |

**只读引用**：
- `src/runtime/src/metadata/test_index.rs` — 理解 TIDX wire format
- `src/runtime/src/metadata/merge.rs` — 理解现有 function/string offset 累积模式
- `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` `BuildTidxSection` — 复用的 writer
- `.claude/rules/version-bumping.md` — zpkg minor bump checklist
- `src/toolchain/test-runner/src/main.rs` / `bootstrap.rs` — 验证 test discovery 读 `LoadedArtifact.test_index`

## Out of Scope

- TIDX wire format 本身改动（仍 v=3 = `add-test-timeout-attribute` 落地后的形式）。
- 跨语言 contract test — 已有的 `ReadWriteRoundTrip` 框架够用；如果不通则改 spec 或代码。
- `[[test]].dependencies` 三层合并（仍待 Phase 5 demo 出现使用案例时再做；与本 spec 解耦）。
- bench dir-mode（Phase 3.2 独立 spec，与本 spec 无格式耦合）。
- 老 zpkg 0.10 兼容路径 — pre-1.0 strict-pin policy，与 [philosophy.md "不为旧版本提供兼容"](../../../.claude/rules/philosophy.md#不为旧版本提供兼容2026-04-26-强化) 一致。

## Open Questions

- [ ] zpkg meta `[meta].version` 字段写 0.11 后，sidecar (.zsym) zpkg 是否也走同 minor？(推荐：是 —— sidecar 走同 ZpkgWriter，自然 0.11)
- [ ] 跨 module 同名函数 — `merge_modules` 现做 first-wins dedup；TIDX method_id 指 module-local 函数，但聚合后的 functions 数组里同名函数被 dedup 掉，怎么处理 TIDX entry？(推荐：在 reader 聚合时丢弃 method_id 已被 dedup 掉的 entry，并 warn；测试场景里 stdlib 各 module 函数名不冲突，dedup 极少触发)
