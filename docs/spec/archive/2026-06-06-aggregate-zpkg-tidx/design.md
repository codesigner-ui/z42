# Design: Aggregate per-module TIDX into zpkg

## Architecture

```
ZpkgWriter (C#)                       zpkg file on disk
─────────────                         ─────────────────
foreach module:                       MODS section
  ns / src / hash                       module[0]:
  func_count, first_sig                   ns_idx, src_idx, hash_idx
  FUNC bytes                              func_count, first_sig
  TYPE bytes                              FUNC … TYPE … DBUG … REGT …
  DBUG bytes                          ★   TIDX_LEN + TIDX_DATA  ← new
  REGT bytes                            module[1]:
★ TIDX bytes (ZbcWriter.BuildTidx)      …
                                            FUNC … TYPE … DBUG … REGT …
                                      ★     TIDX_LEN + TIDX_DATA  ← new
                                       ...

zbc_reader (Rust) read_mods_section            loader load_zpkg_bytes
─────────────────────────────────             ─────────────────────────
foreach module slot:                          (Modules, tidx_bytes_per_module) result
  read ns / src / hash …                      cumulative_func_offset = 0
  read FUNC … TYPE … DBUG … REGT …            cumulative_str_offset  = 0
★ read tidx_len + tidx_data                   for each (module, raw_tidx_bytes):
  push (Module, namespace, raw_tidx_bytes)      entries = read_test_index(raw_tidx_bytes)?
                                                for e in entries:
                                                  e.method_id += cumulative_func_offset
                                                  e.*_str_idx += cumulative_str_offset (only if != 0)
                                                  loaded.test_index.push(e)
                                                cumulative_func_offset += module.functions.len()
                                                cumulative_str_offset  += module.string_pool.len()
                                              loaded.test_index = all aggregated entries
```

`merge_modules` 跟 ConstStr / LoadFnCached 的 remap 完全同模式；TIDX 加进去是同种 cumulative offset 套路。

## Decisions

### Decision 1: 每 module TIDX vs 顶层聚合 TIDX

**问题**：TIDX 数据放哪？(A) 每 module MODS 段尾追加 vs (B) zpkg 顶层加新 TIDX 段。

**选项**：
- A — **每 module TIDX**（与 DBUG / REGT 同 pattern）；method_id 保留 module-local；reader 端按 cumulative function offset 聚合。
- B — 顶层 TIDX 段，writer 已经做了 method_id 全局 remap（与 SIGS 同 pattern）。

**决定**：**A**。
- writer 改动最小：直接 `ZbcWriter.BuildTidxSection(mod.TestIndex, pool)` 字节级复制，无 remap 逻辑。
- reader 端聚合逻辑与现有 `merge_modules` 的 string/function 偏移累积模式完全一致 —— 已有的代码可参照。
- 多文件测试场景的 module 数量很小（1-3 个 .z42 文件 → 1-3 module）；reader 端 O(modules × tests) 聚合 cost 可忽略。
- B 选项把 writer-side remap 抠出来要在 C# 侧维护一份 cumulative offset 计算（functions / strings），重复 reader-side 的逻辑；代码两份反而高 risk。

### Decision 2: tidx_len = 0 时不写 magic header

**问题**：模块无 [Test] 时，要不要写 4 字节 magic + 1 字节 version + 4 字节 entry_count = 9 字节的"空" TIDX？

**决定**：**tidx_len = 0 表示该 module 完全没有 TIDX 段**。reader 读到 `tidx_len == 0` 直接跳过，不调 `read_test_index`。理由：
- stdlib lib 99% module 无 [Test] —— 22 个 lib × 60-180 modules ≈ ~2000 module，每个省 9 byte ≈ 18KB 节省。微小但 free。
- DBUG / REGT 已经是这个 pattern（`if (data.Length > 0) w.Write(data)`），保持一致。

### Decision 3: 字符串索引 remap 直接用模块串池 offset

**问题**：`*_str_idx` 字段（skip_reason / skip_platform / skip_feature / expected_throw_type）在原 zbc 里指向**该 module 的 raw STRS pool**。聚合到 zpkg 时，所有 module 的 string_pool 已被 `merge_modules` 串行 concat 成一个大 pool；TIDX 里的索引要怎么对应？

**决定**：reader 端聚合时，对每 module 的 TIDX entries 加上**该 module 的 string_pool 起始 offset**（== merge 前各 module string_pool.len() 累加值）。`*_str_idx == 0` 表示 "无 string"，不偏移；非零的 1-based 索引整体 += offset。

**关键不变式**：`merge_modules` 拼接 string_pool 时 module N 的 strings 落在 `[sum(prev modules' len), sum(prev modules' len) + module N len)` 区间 —— TIDX 索引正好用同 offset 偏移即可。

### Decision 4: dedup 掉的 function 对应的 TestEntry 直接丢弃

**问题**：`merge_modules` 对函数按 name first-wins dedup（line 78 `seen_functions.insert`）。若两 module 有同名函数（极少见，stdlib 实际为零），TIDX 里 module N 的 method_id 指向被 dedup 掉的函数，聚合后这个 entry 指向错的函数（或越界）。

**决定**：reader 聚合时，记录被 dedup 掉的 (module_idx, local_function_idx) set；对应 TIDX entry 直接丢弃 + WARN 一行（runtime tracing::warn）。**stdlib 不会触发**（每包 namespace 隔离 + 函数名内含 namespace 前缀）；如果用户工程出现同名函数 dedup，行为是"丢测试，留 warn"，保留 first-wins lib 语义不变。

### Decision 5: zpkg minor 0.10 → 0.11，zbc 不动

**问题**：bump zpkg 是否同步 bump zbc?

**决定**：**仅 zpkg bump**。TIDX 在 zbc 侧的写法没变（仍是 TIDX v=3）。只是从 "ZbcWriter emit 到 zbc 文件" 多了一条 "ZpkgWriter emit 到 zpkg 的 MODS 段"。每条 TIDX bytes 字段格式 1:1 复用。

**按 version-bumping.md "Bumping .zpkg minor version (independent)" 路径走**：
1. ZpkgWriter.VersionMinor++（10 → 11）+ 注释
2. zbc_reader.rs ZPKG_VERSION_MINOR 同步 → 11
3. docs/design/runtime/zpkg.md Minor changelog 加 0.11 行
4. zpkg fixture 4 个 regen

zbc fixture 不动；zbc reader 不动；ZbcWriter 不动。

## Implementation Notes

### Writer 改动（C#）

`BuildModsSection` 末尾按 module 序列加：

```csharp
// add-aggregate-zpkg-tidx (2026-06-XX): TIDX bytes per module.
// 0 长度 = 该 module 无 [Test] / [Benchmark] 等 annotation；
// reader 端按 tidx_len == 0 跳过。
byte[] tidxData = mod.TestIndex is { Count: > 0 } testIndex
    ? ZbcWriter.BuildTidxSection(testIndex, pool)
    : Array.Empty<byte>();
w.Write((uint)tidxData.Length);
if (tidxData.Length > 0) w.Write(tidxData);
```

`ZbcWriter.BuildTidxSection` 已存在（[ZbcWriter.cs:602+](../../../src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs#L602)）；直接复用不重写。

### Reader 改动（Rust）

`read_mods_section` 返回类型扩展：

```rust
// 旧：Vec<(Module, String)>
// 新：Vec<(Module, String, Vec<u8>)>   // 第三元为 raw TIDX bytes
fn read_mods_section(...) -> Result<Vec<(Module, String, Vec<u8>)>> {
    ...
    for _ in 0..mod_count {
        // ... existing reads (ns_idx, src_idx, hash_idx, func_count, ...)
        let tidx_len = c.read_u32()? as usize;
        let tidx_data = if tidx_len > 0 {
            c.read_bytes(tidx_len)?.to_vec()
        } else {
            Vec::new()
        };
        result.push((module, namespace, tidx_data));
    }
    Ok(result)
}
```

`load_zpkg_bytes` 聚合：

```rust
let module_pairs = read_zpkg_modules(raw).context("cannot load modules from zpkg")?;

let mut module_func_offsets = Vec::with_capacity(module_pairs.len());
let mut module_str_offsets  = Vec::with_capacity(module_pairs.len());
let mut cum_func = 0u32;
let mut cum_str  = 0u32;
for (m, _, _) in &module_pairs {
    module_func_offsets.push(cum_func);
    module_str_offsets.push(cum_str);
    cum_func += m.functions.len() as u32;
    cum_str  += m.string_pool.len() as u32;
}

let mut aggregated: Vec<TestEntry> = Vec::new();
for (idx, (_, _, tidx_bytes)) in module_pairs.iter().enumerate() {
    if tidx_bytes.is_empty() { continue; }
    let mut entries = crate::metadata::test_index::read_test_index(tidx_bytes)
        .context("decode per-module TIDX in zpkg")?;
    let func_off = module_func_offsets[idx];
    let str_off  = module_str_offsets[idx];
    for e in entries.iter_mut() {
        e.method_id = e.method_id.saturating_add(func_off);
        // *_str_idx 用 1-based，0 = 无 string；不偏移
        if e.skip_reason_str_idx != 0 { e.skip_reason_str_idx += str_off; }
        if e.skip_platform_str_idx != 0 { e.skip_platform_str_idx += str_off; }
        if e.skip_feature_str_idx != 0 { e.skip_feature_str_idx += str_off; }
        if e.expected_throw_type_idx != 0 { e.expected_throw_type_idx += str_off; }
        for tc in e.test_cases.iter_mut() {
            if tc.arg_repr_str_idx != 0 { tc.arg_repr_str_idx += str_off; }
        }
    }
    aggregated.extend(entries);
}

let modules: Vec<Module> = module_pairs.into_iter().map(|(m, _, _)| m).collect();
let mut module = merge_modules(modules).context("merging zpkg modules")?;
// ...existing post-merge processing...

let mut loaded_artifact = LoadedArtifact {
    module,
    entry_hint: meta.entry,
    dependencies: meta.dependencies,
    import_namespaces: vec![],
    test_index: aggregated,
};
// resolve strings against merged pool
crate::metadata::test_index::resolve_test_index_strings(
    &mut loaded_artifact.test_index,
    &loaded_artifact.module.string_pool,
);
```

### Dedup-safe variant

Decision 4 的 dedup 检测：

```rust
// 在 merge_modules 之前记录每 module 的函数 name vec
// 在 merge_modules 之后看 final module.functions 的 name -> index 映射
// 对每 aggregated TestEntry，验证 e.method_id 仍指向同 name；不一致则 warn + drop
```

实现细节留实施期；stdlib 无碰撞，初版可只做 saturating_add 加 method_id 越界 bail，dedup-safe 等真出 case 再补。

### 测试场景

- `loader_tests::zpkg_tidx_empty` — 0 module 0 test，TIDX 全空。
- `loader_tests::zpkg_tidx_single_module` — 单 module 含 2 test，test_index 取出来名字 / method_id 正确。
- `loader_tests::zpkg_tidx_multi_module_method_id_remap` — 2 module，module 1 有 3 函数 + 1 test；module 2 有 5 函数 + 2 test，验证 module 2 的 method_id 加了 3 偏移。
- `loader_tests::zpkg_tidx_multi_module_str_remap` — 跨 module 的 skip_reason 字符串正确解析（先 offset 后 resolve）。
- `loader_tests::zpkg_tidx_zero_len_skips` — 没 [Test] 的 module tidx_len = 0，loader 不 panic。

### Fixture regen

按 version-bumping.md zpkg 路径：

```bash
./src/tests/zpkg-format/generate-fixtures.sh
dotnet test --filter "FullyQualifiedName~Z42.Tests.Zpkg"
```

4 个 fixture 字节会变（多出 tidx_len 字段，即使为 0 也写 4 字节）。expected.json 也跟着变。git diff 反映 minor delta。

### Phase 5 demo 重做

aggregate-zpkg-tidx 落地后，把 `tests/secp256k1/{source.z42, vectors.z42}` 重新建（之前 2026-06-06 commit `55e318fc` 时退回了），删 `tests/ecdsa_secp256k1_vectors.z42`，跑 `xtask test stdlib z42.crypto -k secp256k1 --no-build` 验证 10 个 test 全过。

## Testing Strategy

- **单元测试**：见上 `loader_tests::zpkg_tidx_*` 5 项。
- **格式 fixture**：`src/tests/zpkg-format/fixtures/*` 4 个 fixture regen 后 `ReadWriteRoundTrip` 测试自动覆盖。
- **集成测试**：Phase 5 demo (`xtask test stdlib z42.crypto -k secp256k1`) 即端到端 smoke。
- **跨 module dedup**：stdlib 不会触发，留 follow-up 当真出现时再加专项测试。
- **GREEN 命令**（按 .claude/rules/workflow.md 阶段 8）：
  - `dotnet build src/compiler/z42.slnx`
  - `cargo build --manifest-path src/runtime/Cargo.toml --release`
  - `dotnet test src/compiler/z42.Tests/z42.Tests.csproj`
  - `xtask test vm + cross-zpkg + stdlib` —— stdlib zpkg 全部 regen 后跑通
  - `./src/tests/zpkg-format/generate-fixtures.sh` —— 4 fixture 重生 + git diff 体现 wire delta
