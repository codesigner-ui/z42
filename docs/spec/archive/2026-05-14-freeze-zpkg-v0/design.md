# Design: Freeze `.zpkg` v0 wire format

## Architecture

```
冻结对象（pre-1.0 持续 strict-pin，与 zbc 完全同模式）：

  ┌─────────────────────────────────────────────────────────────────────┐
  │  v0 STRUCTURAL FRAMING        ← 本 spec 锁定                         │
  │                                                                      │
  │   Magic "ZPK\0" (4B)                                                 │
  │   16-byte header layout                                              │
  │     ├─ major:    u16   = 0                                           │
  │     ├─ minor:    u16   (current 6 after catch-up bump)               │
  │     ├─ flags:    u16   (FlagPacked / FlagExe / FlagSymOnly)          │
  │     ├─ sec_cnt:  u16                                                 │
  │     └─ reserved: 4B                                                  │
  │                                                                      │
  │   Section directory entry (12B / 项)                                 │
  │     ├─ tag:      [4B; ASCII]                                         │
  │     ├─ offset:   u32                                                 │
  │     └─ size:     u32                                                 │
  │                                                                      │
  │   Section tag 集合：                                                  │
  │     META   — name/version/entry inline UTF-8                         │
  │     STRS   — unified string heap                                     │
  │     NSPC   — namespace list                                          │
  │     EXPT   — export table                                            │
  │     DEPS   — dependency list                                         │
  │     SIGS   — function signatures (packed mode only)                  │
  │     MODS   — per-module FUNC+TYPE bodies (packed mode)               │
  │     FILE   — per-file path entries (indexed mode)                    │
  │     TSIG   — type signatures (cross-package generic / interface)     │
  │     IMPL   — `impl Trait for Type` declarations (L3-Impl2)           │
  │     MDBG   — per-module debug bodies (sym-only sidecar)              │
  │     BLID   — 16B BLAKE3-128 build_id (sidecar pairing)               │
  │                                                                      │
  │   Reader fetches sections by tag from dict (unknown tags → skip)     │
  └─────────────────────────────────────────────────────────────────────┘

  ┌─────────────────────────────────────────────────────────────────────┐
  │  v0 minor 可变（持续 iteration，每次 bump = 全 regen）               │
  │                                                                      │
  │   - 新增 section tag（reader 自动 skip）                              │
  │   - 已定义 section 内部字段语义变化                                  │
  │   - **耦合**：zbc minor 任意 bump → zpkg minor 必须同步 +1（见 Decision 2）│
  └─────────────────────────────────────────────────────────────────────┘

  ┌─────────────────────────────────────────────────────────────────────┐
  │  触发 major bump (zpkg v1)                                           │
  │                                                                      │
  │   - 改 magic                                                         │
  │   - 改 16B header layout                                             │
  │   - 改 section directory 12B 条目格式                                │
  │   - 弃用 packed / indexed 模式之一                                   │
  └─────────────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: 锁定 strict-pin（同 zbc）

**问题**：旧 zpkg artifacts 怎么处理？

**决定**：与 zbc 完全同模式 — reader 精确匹配 writer 的 major + minor，旧 zpkg 必须 regen（`./scripts/build-stdlib.sh + scripts/regen-golden-tests.sh`）。

**理由**：与 `.claude/rules/workflow.md` "不为旧版本提供兼容" + freeze-zbc-v1 决策 1 强对齐；不在已有规则上再开例外。

**实现**：`ZpkgReader` 改 `if (major != ZpkgWriter.VersionMajor || minor != ZpkgWriter.VersionMinor) → bail`。

### Decision 2: zbc inner minor 与 zpkg outer minor 强耦合

**问题**：packed-mode zpkg 内嵌 zbc 1.X 模块；zbc minor bump 时是否要 zpkg minor 一起 bump？

**选项**：

- A — 耦合：任意 zbc minor bump → zpkg minor +1。简单的 SoT，artifact 一致性显式
- B — 解耦：zpkg outer 独立维护 minor；reader 在解 MODS 时检测内嵌 zbc minor 是否兼容
- C — 内嵌 zbc minor 写入 zpkg METADATA：zpkg 不变，加 metadata 暴露内嵌 zbc version

**决定**：A。

**理由**：

- 历史上每次 zbc bump 都顺手 bump 了 zpkg（1.0→0.2, 1.2→0.3, 1.3→0.4, 1.4→0.5），唯一例外是 zbc 1.5（漏 bump 才触发本 spec 的 catch-up）
- A 与现有惯例一致；B / C 需要新解码路径，违反 "pre-1.0 不投入兼容" 政策
- 通过 workflow.md 明文规则强制让 "漏 bump" 不再发生

**实施**：

- 本 spec 把 zpkg 0.5 → 0.6（catch up zbc 1.5）
- workflow.md `.zbc` bump 流程子节扩展，把 zpkg 同步列为第 5 项

### Decision 3: 未识别 section 静默跳过（同 zbc）

**问题**：zpkg reader 遇到未知 section tag 怎么办？

**决定**：静默跳过。

**理由**：

- C# `ZpkgReader` + Rust 端均通过 dict-lookup 按 tag 取，未注册的 tag 自然 fall-through —— 已是默认行为
- 测试固化此契约：构造含 `XXXX` section 的 zpkg，验证 reader 正常加载

### Decision 4: 字节级 golden fixture 集合

**问题**：fixture 怎么覆盖 zpkg 的 mode 多样性？

**决定**：5 个 fixture，覆盖 packed / indexed / sym-only 三种 mode + 内容多样性：

| Fixture | mode | 覆盖 |
|---------|------|------|
| `packed-minimal/`        | packed   | 单 lib 单模块；META + STRS + NSPC + EXPT + SIGS + MODS + DEPS（最小 packed 形态）|
| `packed-multi-module/`   | packed   | 多模块（2-3 个 .z42 文件 → 同一 zpkg）；MODS 多条目 + 共享 STRS pool |
| `packed-with-tsig/`      | packed   | 含 cross-package 引用（含 TSIG section）|
| `indexed-minimal/`       | indexed  | 增量编译 cache form；FILE section 替代 MODS |
| `sym-only-sidecar/`      | sidecar  | `FlagSymOnly` set；只含 MDBG + BLID（zpkg 端 sym-only sidecar 形态）|

> 不覆盖 IMPL section 的独立 fixture：IMPL 出现频率与 TSIG 强相关，packed-with-tsig 已能间接触发。如未来 IMPL 单独漂移可加 `packed-with-impl/`。

### Decision 5: Golden 比对双轨（同 zbc）

字节级 `source.zpkg` 对账 + 归一化 `expected.json` 对账，两个 CI 测试 lane。

### Decision 6: ZpkgGoldenJsonFormatter 字段集（类比 ZbcGoldenJsonFormatter）

输出字段（避免不稳定字段）：

```jsonc
{
  "header": { "major": 0, "minor": 6, "flags": ["Packed"], "section_count": 7 },
  "sections": ["META", "STRS", "NSPC", "EXPT", "DEPS", "SIGS", "MODS"],
  "package": { "name": "demo", "version": "1.0", "entry": "demo.main" },
  "namespaces": ["Demo", "Demo.Util"],
  "exports":    [ "Demo.Foo", "Demo.Bar" ],
  "deps":       [ { "name": "z42.core", "version": "0.1.0" } ],
  "module_count": 2,
  "tsig_entries": 0,
  "impl_entries": 0,
  "is_sym_only": false,
  "has_build_id": false
}
```

排除字段：绝对 offset / section size（依赖编码紧凑度）/ 单模块 opcode dump（属 zbc 层 invariant）/ build_id（content-hash 但增加输出复杂度）。

### Decision 7: `z42c golden-json` 子命令扩展为支持 zpkg

**问题**：golden-json 子命令目前只读 `.zbc`；要不要分两个子命令？

**决定**：单子命令按扩展名分发。`.zbc` → `ZbcGoldenJsonFormatter`，`.zpkg` → `ZpkgGoldenJsonFormatter`。与 `disasm` 子命令的双扩展派发模式一致。

### Decision 8: workflow.md zpkg 联动规则

`.claude/rules/workflow.md` "Bumping `.zbc` minor version" 子节加 step 5（同一 commit 必须）：

```
5. `src/compiler/z42.Project/ZpkgWriter.cs` — `VersionMinor++` 且注释更新内嵌 zbc 版本；
   `src/runtime/src/metadata/zbc_reader.rs` — `ZPKG_VERSION_MINOR` 同步；
   `docs/design/runtime/zpkg.md` — Minor changelog 加一行；
   `src/tests/zpkg-format/generate-fixtures.sh` 跑一遍 regen 5 个 fixture
```

加交叉引用 "Bumping `.zpkg` minor version"（独立场景：只改 zpkg outer 不动 zbc 内）。

## Implementation Notes

### zpkg.md 结构（新文件，约 200-250 行）

骨架按 zbc.md：

```
# zpkg — z42 包格式

## 设计目标
## 核心设计决策
## 文件格式
   ### 文件头（16 字节）
   ### Section 目录（12 字节 / 项）
   ### Sections 详解
       META / STRS / NSPC / EXPT / DEPS / SIGS / MODS / FILE / TSIG / IMPL / MDBG / BLID
## Packed vs Indexed mode
## Sym-only sidecar (.zsym)
## 版本兼容性
   - strict-pin 政策（同 zbc）
   - 当前版本：major=0, minor=6
   - 触发 minor / major bump 的事项
   - 与 zbc 内嵌 minor 的耦合规则
## Minor changelog（0.1 → 0.6 全表，每行含日期 + 触发 spec）
```

### Minor changelog 完整内容（按 git log 还原）

| minor | 日期 | 触发 spec | 引入内容 |
|:-----:|------|----------|---------|
| 0.1 | 2026-04-22 之前 | (initial) | zpkg 初始结构（packed / indexed mode 雏形）|
| 0.2 | 2026-05-09 | [tokenize-ir-and-zbc-bump](../../spec/archive/2026-05-09-tokenize-ir-and-zbc-bump/) | inner zbc 1.0；reader v1.0 IdMap decode |
| 0.3 | 2026-05-10 | [split-debug-symbols](../../spec/archive/2026-05-11-split-debug-symbols/) | inner zbc 1.2；per-member DBUG body；FlagSymOnly + MDBG + BLID for sidecar |
| 0.4 | 2026-05-10 | split-debug-symbols Phase 4 | inner zbc 1.3（SIGS 加 per-param type names）|
| 0.5 | 2026-05-11 | [add-generic-func-constraint](../../spec/archive/2026-05-11-add-generic-func-constraint/) | inner zbc 1.4（func constraint bundle）|
| 0.6 | 2026-05-14 | [freeze-zpkg-v0](../../spec/changes/freeze-zpkg-v0/)（本 spec catch-up） | inner zbc 1.5（Convert opcode；zbc 1.5 在 2026-05-13 fix-numeric-cast-lowering 落地，zpkg 当时漏 bump）|

### ZpkgReader 改动（C#）

```csharp
// before（line 279）：
if (major == 0 && minor < 5)
    throw new InvalidDataException("zpkg ...");

// after（精确匹配 + ref writer const）：
if (major != ZpkgWriter.VersionMajor)
    throw new InvalidDataException(
        $"zpkg major {major} not supported (writer is at {ZpkgWriter.VersionMajor})");
if (minor != ZpkgWriter.VersionMinor)
    throw new InvalidDataException(
        $"zpkg minor {minor} not supported (writer is at {ZpkgWriter.VersionMinor}); " +
        $"regen via ./scripts/build-stdlib.sh");
```

### Rust 端

```rust
// 新增常量（zbc_reader.rs 顶部，紧跟 ZBC_VERSION_*）：
pub const ZPKG_VERSION_MAJOR: u16 = 0;
pub const ZPKG_VERSION_MINOR: u16 = 6;

// read_zpkg（line ~1188）+ parse_zpkg_sidecar（line ~647）：
if major != ZPKG_VERSION_MAJOR || minor != ZPKG_VERSION_MINOR {
    bail!("zpkg {major}.{minor} not supported (writer is at \
           {ZPKG_VERSION_MAJOR}.{ZPKG_VERSION_MINOR}); \
           regen via ./scripts/build-stdlib.sh");
}
```

### Fixture 生成路径

zpkg 不是 z42 source 直接 emit 出来的，要走 `z42c build` workspace 模式。fixture 源是 mini workspace：

```
src/tests/zpkg-format/packed-minimal/
├── source.z42.toml          ← workspace.toml + lib manifest
├── src/lib.z42              ← 单 .z42 source
├── source.zpkg              ← check-in 字节
└── expected.json            ← check-in 归一化 JSON
```

`generate-fixtures.sh` 跑：

```bash
for case_dir in */; do
    (cd "$case_dir" && dotnet "$DRIVER_DLL" build --release)
    cp "$case_dir/artifacts/<lib-name>.zpkg" "$case_dir/source.zpkg"
    dotnet "$DRIVER_DLL" golden-json "$case_dir/source.zpkg" -o "$case_dir/expected.json"
done
```

> **实施期决策点**：fixture workspace 太重 → 用 `z42c build` 全套；轻一点的做法是直接 C# call `ZpkgWriter.Write(...)` 构造测试 zpkg。先 spike fixture 生成流程，复杂度超出则改用 C# direct construction（fixture harness 内嵌 builder helper）。

## Testing Strategy

| 层次 | 验证方式 |
|------|---------|
| **C# byte golden** | `FormatGoldenTests.ByteEqual` × 5 fixture |
| **C# json golden** | `FormatGoldenTests.JsonEqual` × 5 fixture |
| **C# writer determinism** | `FormatGoldenTests.WriterDeterministic` × 5 fixture |
| **C# invariant** | `FormatInvariantTests`: WriterVersionConstantsExposed / MajorMismatchRejected / MinorBelowWriterRejected / MinorAboveWriterRejected / UnknownSectionSkipped |
| **Rust invariant** | 延后到独立 spec `align-zpkg-reader-rust-tests`（依赖 add-std-process / fix-cross-pkg-subclass-fields 归档；test crate 当前编不通）|

GREEN 入口 `./scripts/test-all.sh`（含新增 fixture 现场重生比对）。

## Deferred / Future Work

### freeze-zpkg-A1: Rust 端 invariant + round-trip 测试

- **来源**：本 spec design Testing Strategy
- **触发原因**：当前 Rust test crate 编不通（另一窗口 WIP TypeDesc 字段未在其他 test 文件同步）
- **触发条件**：上述 add-std-process / fix-cross-pkg-subclass-fields 归档之后

### freeze-zpkg-A2: Read-Write 字节对账（同 freeze-zbc-v1 留作 backlog）

- **来源**：design.md decision pattern 对齐
- **触发原因**：zbc 端发现 reader 信息丢失，zpkg 端大概率有同问题；不阻塞 freeze
- **触发条件**：独立 spec `align-zpkg-reader-writer-asymmetry`

### freeze-zpkg-A3: `z42c disasm` zpkg 完整化（0.2.1 退出标准的另一半）

- **来源**：roadmap.md 0.2.1
- **触发原因**：当前 disasm 对 zpkg 仅显示模块名；缺 metadata / TSIG / IMPL section 可读输出
- **触发条件**：本 spec Phase 4 实施完后视复杂度决定本 spec 顺手做或独立 spec
