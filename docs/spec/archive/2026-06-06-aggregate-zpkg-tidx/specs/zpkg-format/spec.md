# Spec: zpkg MODS section — per-module TIDX aggregation

## MODIFIED Requirements

### Requirement: zpkg MODS section carries per-module TIDX bytes

**Before:**
zpkg 0.10 MODS section 每 module 写 5 字段 + 4 内嵌段：
```
ns_idx u32, src_idx u32, hash_idx u32, func_count u16, first_sig u32,
func_len u32 + func_data, type_len u32 + type_data,
dbug_len u32 + dbug_data, regt_len u32 + regt_data
```

**After:**
zpkg 0.11 MODS section 每 module 增加 1 段（在 regt 后）：
```
... 同上 5 字段 + FUNC + TYPE + DBUG + REGT 段 ...
tidx_len u32 + tidx_data        ← NEW
```

`tidx_data` 字节内容 = `ZbcWriter.BuildTidxSection(mod.TestIndex, pool)` 输出（与 standalone .zbc 的 TIDX 段格式完全相同，TIDX v=3）。`tidx_len = 0` 表示该 module 无 [Test] / [Benchmark] 等标注，reader 跳过 read_test_index。

#### Scenario: 全空 module（无 [Test]）

- **WHEN** 编译一个所有 module 都没 [Test] / [Benchmark] 的 lib（典型 stdlib lib，如 z42.io）→ ZpkgWriter 输出 zpkg
- **THEN** 每 module 的 MODS 末尾写 `tidx_len = 0`，不写 tidx_data
- **AND** loader 读取该 zpkg，`LoadedArtifact.test_index` = 空 Vec
- **AND** test-runner 加载 → "no tests found" 退出 3

#### Scenario: 单 module 含 N 个 [Test]

- **WHEN** 单 module 含 N 个 [Test] 方法 → 编译产 zpkg
- **THEN** 该 module 的 MODS 末尾 `tidx_len > 0`，`tidx_data` 是 ZbcWriter.BuildTidxSection 输出（N 个 TestEntry + magic + version + count header）
- **AND** loader `test_index.len() == N`，每个 TestEntry 的 `method_id` 与该 module local function index 完全对应（offset = 0）
- **AND** test-runner 发现 N 个 test 并逐个跑

#### Scenario: 多 module 含 [Test]，method_id remap 正确

- **WHEN** zpkg 含 2 module：M0 有 3 函数 + 1 个 [Test] (method_id=2)；M1 有 5 函数 + 2 个 [Test] (method_id=0, method_id=4)
- **THEN** loader 聚合后 `test_index` 三条：
  - entry 0: method_id = 2  (M0 局部，offset=0)
  - entry 1: method_id = 3  (M1 局部 0 + offset 3)
  - entry 2: method_id = 7  (M1 局部 4 + offset 3)
- **AND** merged `module.functions[method_id]` 取出来与编译时 [Test] 标注的函数 1:1 对应

#### Scenario: 多 module skip_reason / skip_platform / arg_repr 字符串 remap 正确

- **WHEN** M0 string_pool 含 10 entries (idx 1..=10)；M1 string_pool 含 5 entries (M0 局部 1..=5)；M1 的 TIDX entry 有 `skip_reason_str_idx = 3`
- **THEN** loader 聚合后该 entry 的 `skip_reason_str_idx = 3 + 10 = 13`
- **AND** `resolve_test_index_strings(...)` 调后 `skip_reason = Some(merged_string_pool[12])` （13 - 1）= M1 局部第 3 个字符串
- **AND** `*_str_idx = 0` 的字段保持 0（"无 string" 不偏移）

#### Scenario: 跨 module 函数 name dedup → entry 丢弃 + warn

- **WHEN** M0 和 M1 都含同名函数 `foo`（merge_modules first-wins 保留 M0 的 foo，丢 M1 的 foo）；M1 的 TIDX 含 `[Test]` 指向 M1 的 foo (method_id 局部)
- **THEN** loader 聚合时检测到 method_id 重映射后指向不属于当前 module 的函数，发出 `tracing::warn!` 并丢弃该 entry
- **AND** 不导致 `merge_modules` 或 test-runner panic
- **AND** stdlib 实际不触发（namespace 隔离 + 函数名前缀）

### Requirement: zpkg minor version bump 0.10 → 0.11

#### Scenario: ZpkgWriter 写 0.11 minor

- **WHEN** ZpkgWriter 产出新 zpkg
- **THEN** header 字节序列：`Z P K G` (magic) + 0 (major LE u16) + 11 (minor LE u16) + ...
- **AND** zpkg.md "Minor changelog" 表含一行 `0.11 | 2026-06-XX | aggregate-zpkg-tidx | 每 module MODS 段追加 tidx_len + tidx_data`

#### Scenario: 0.10 zpkg 不可读

- **WHEN** 旧 0.10 zpkg 被新 zbc_reader 加载
- **THEN** bail 错误信息含 _"zpkg minor 10 not supported (writer is at 0.11); regen via ..."_ —— strict-pin policy
- **AND** 用户需要按 xtask regen 流程重生 stdlib + 用户项目

### Requirement: read_mods_section 返回类型扩展

#### Scenario: 反序列化保留 raw TIDX bytes

- **WHEN** `read_mods_section(sec, &pool, &sigs)` 调用
- **THEN** 返回 `Vec<(Module, String, Vec<u8>)>` — 每元组第三项是该 module 原始 TIDX 字节
- **AND** 上层 `load_zpkg_bytes` 调 `read_test_index(raw_tidx)` 解析

#### Scenario: tidx_len = 0 不调 read_test_index

- **WHEN** 某 module 的 tidx_len = 0
- **THEN** read_mods_section 推 (Module, ns, Vec::new())
- **AND** load_zpkg_bytes 看到 empty Vec，skip read_test_index call

## ADDED Requirements

### Requirement: LoadedArtifact.test_index 在 zpkg 加载路径正确填充

#### Scenario: load_zpkg_bytes 不再 hard-code vec![]

- **WHEN** load_zpkg_bytes 加载任何 0.11 zpkg
- **THEN** 返回的 LoadedArtifact 的 `test_index` 字段 = 所有 module TIDX 聚合 + remap 结果（按上 4 个 scenarios 的语义）
- **AND** test-runner bootstrap 拿到 user_artifact.test_index 后 discover.rs 不再返回空

#### Scenario: TIDX 字符串通过 resolve_test_index_strings 解析

- **WHEN** LoadedArtifact 构造完，调 `resolve_test_index_strings(&mut artifact.test_index, &artifact.module.string_pool)`
- **THEN** 每 entry 的 `skip_reason` / `skip_platform` / `skip_feature` / `expected_throw_type` 字段被填充为对应的 String（如 `*_str_idx != 0`）
- **AND** 后续 `rebuild_string_pool` 不影响这些已 resolve 的 Option<String> 字段（即使原始 raw_pool 被压缩）

## Pipeline Steps

变更影响的 pipeline 阶段：
- [ ] Lexer：无影响
- [ ] Parser / AST：无影响
- [ ] TypeChecker：无影响
- [ ] IR Codegen：无影响
- [x] **ZpkgWriter (Sections.cs)**：写 tidx_len + tidx_data per module（核心 writer 变更）
- [x] **zbc_reader.rs read_mods_section**：读 tidx_len + tidx_data；返回类型扩展
- [x] **loader.rs load_zpkg_bytes**：聚合 + remap method_id / str_idx；resolve strings
- [ ] **interp / JIT**：无影响（TIDX 仅 test-runner 消费）
- [x] **z42-test-runner**：现有 discovery 自动 work（已经读 LoadedArtifact.test_index）
