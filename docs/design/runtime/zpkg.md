# zpkg — z42 包格式

> 单一来源：[`src/compiler/z42.Project/ZpkgWriter.cs`](../../../src/compiler/z42.Project/ZpkgWriter.cs) 是 wire format 的代码权威；本文档是与之同步的人类可读契约。任何不一致按 [freeze-zpkg-v0 spec](../../spec/archive/2026-05-14-freeze-zpkg-v0/) 流程更新。

## 设计目标

- **多模块封装**：一个 `.zpkg` 文件承载一个包内全部 `.z42` 编译产物（多个 zbc module），加上包级元数据（导出 / 依赖 / 命名空间）
- **跨包元数据自包含**：TSIG / IMPL section 携带跨 zpkg 必需的类型签名 + impl-trait-for 声明，加载时无需读其他 zpkg
- **多模式分发**：
  - `packed` 模式 — 一个文件包含所有模块字节（默认发行 / stdlib 用）
  - `indexed` 模式 — 一个索引文件 + 多个独立 `.zbc`（增量编译 cache 用）
  - `sym-only` sidecar（`.zsym`）— 拆分 debug-symbols 出去后的瘦身分发

## 核心设计决策

### 与 zbc 的关系

zpkg 是 zbc 的容器：packed-mode zpkg 把多个 zbc module 的 FUNC + TYPE + DBUG 字节直接嵌入 `MODS` section。zpkg outer wire format 与 zbc inner wire format **强耦合**：

- zbc minor bump → zpkg minor 必须 +1（[`version-bumping.md` 强制规则](../../../.claude/rules/version-bumping.md#zpkg-联动规则freeze-zpkg-v0-2026-05-14)）
- zpkg outer 独立变化（如新增 section）也走 zpkg minor bump，但不需 zbc 同步

历史一致性：zpkg 0.1 → 0.5 每次 minor 都对应一次 zbc 内嵌版本提升，唯一漏 bump 是 zbc 1.4 → 1.5（fix-numeric-cast-lowering）；本文档定型时通过 freeze-zpkg-v0 catch-up 把 zpkg 升到 0.6 对齐 zbc 1.5。

### Strict-pin 政策

与 [`zbc.md` "版本兼容性"](zbc.md#版本兼容性) 完全同模式：

- Reader 仅接受 `major == ZpkgWriter.VersionMajor && minor == ZpkgWriter.VersionMinor`
- 每次 minor bump = 所有现存 `.zpkg` artifacts 必须 regen（`z42 xtask.zpkg build stdlib`）
- pre-1.0 z42 阶段不为旧 zpkg minor 提供向前 / 向后兼容

## 文件格式

### 文件头（16 字节）

```
[4]  magic:         0x5A 0x50 0x4B 0x00   ("ZPK\0")
[2]  version_major  当前 0
[2]  version_minor  当前 6 (详见 minor changelog)
[2]  flags          bit0=Packed, bit1=Exe, bit2=SymOnly
[2]  section_count
[4]  reserved
```

**Flags 语义**：

- `Packed (0x01)` — 模块字节直接嵌入 MODS section（默认分发形态）
- `Exe (0x02)` — 标记为可执行入口包（main entry 来自 META.entry）
- `SymOnly (0x04)` — `.zsym` sidecar 形态（仅 MDBG + BLID），不可作为项目包加载

### Section 目录（每项 12 字节）

```
[4]  tag            META / STRS / NSPC / EXPT / DEPS / SIGS / MODS
                  / FILE / TSIG / IMPL / MDBG / BLID
[4]  offset         从文件头起的字节偏移
[4]  size           section 字节长度
```

Reader 通过 dict-lookup `tag → (offset, size)` 取 section；**未识别 tag 静默跳过**（v0 内"加 section 不破坏旧 reader"的唯一兼容点，与 zbc 同模式）。

### Sections 详解

#### META — 包元数据

无 STRS 依赖（自给自足 UTF-8 字符串），保证 reader 不需先读 STRS 也能拿到 package name / version：

```
[2 + N]  name      u16 length + UTF-8 bytes
[2 + N]  version   u16 length + UTF-8 bytes
[2 + N]  entry     u16 length + UTF-8 bytes (Packed+Exe 时为 main 函数限定名；其他为空)
```

#### STRS — 统一字符串堆

Packed mode 的核心优化：所有模块共享同一 STRS pool，跨模块重复字符串只存一份。格式与 zbc STRS 同（u32 count + (offset, length) 表 + UTF-8 字节段）。

#### NSPC — 命名空间列表

包内导出的所有命名空间（指向 STRS 索引）；提供给 lazy zpkg 加载器做"namespace → zpkg" 反查。

```
[4]   count
[4*N] str_idx[]    每条目对应 STRS 池中一项
```

#### EXPT — 导出表

包对外提供的 public 符号（类型 / 函数），指向 STRS。

```
[4]   count
[4*N] str_idx[]    符号全名
```

#### DEPS — 依赖列表

```
[4]   count
( N × [4]: name_str_idx + [4]: version_str_idx )
```

**Release 边界**：`[tests.dependencies]` / `[bench.dependencies]` / `[[test]].dependencies` 字段（add-tests-bench-manifest-config, 2026-06-06）**不**进入 DEPS 段 —— 它们是编译期 test/bench harness 的 dev-dep，不属于 lib 的运行时依赖契约。`ZpkgWriter` 在产 release zpkg 时只写顶层 `[dependencies]`；测试 / bench harness 由 xtask 通过 synthetic mini-manifest 单独编 `.test.` / `.bench.` zpkg，不污染 lib 的 DEPS。CI release-guard step（[add-tests-bench-manifest-config](../../spec/changes/add-tests-bench-manifest-config/) Phase 6.1）扫 `dist/` 禁止 `.test.` / `.bench.` infix 出现作为最后防线。

#### SIGS — 函数签名（仅 packed mode）

跨模块的所有函数签名（用于跨包调用解析）。格式与 zbc SIGS 同（含 1.3 引入的 per-param type names）。

#### MODS — 模块体（仅 packed mode）

每个嵌入模块的 FUNC + TYPE + DBUG 字节段，按 zbc 内嵌格式编码（每个模块 strRemap 引用全局 STRS）。

```
[4]   module_count
( N × per-module record:
    [4] namespace_str_idx
    [4] source_file_str_idx
    [4] source_hash_str_idx
    [4] type_len + TYPE bytes
    [4] sigs_len + SIGS bytes
    [4] func_len + FUNC bytes
    [4] dbug_len + DBUG bytes    ; 0 in strip mode
)
```

#### FILE — 文件路径（仅 indexed mode）

替代 MODS：每条目指向磁盘上的 `.zbc` 文件路径。

```
[4]   count
( N × [4]: path_str_idx + [4]: namespace_str_idx )
```

#### TSIG — 跨包类型签名

携带跨 zpkg 引用的 `interface` / `class` / generic 类型签名，避免加载时跨包查询。

```
[4]   exported_module_count
( N × per-module record:
    [4] qualified_name_str_idx
    [4] sig_kind                    ; 0=Class, 1=Interface, 2=Generic
    [4] field_count
    ... ; 详见 ZpkgReader.Tsig.cs
)
```

#### IMPL — `impl Trait for Type` 声明（L3-Impl2）

跨 zpkg 的 impl 声明（让 dispatch 器在加载时拿到对应表）。

#### MDBG — 模块 debug 体（仅 sym-only sidecar）

替代 packed mode 的内嵌 DBUG：sidecar zpkg 独立携带每个模块的 line table + 局部变量名，供主 zpkg 通过 build_id 配对加载。

#### BLID — Build ID（仅 sym-only sidecar）

16 字节 BLAKE3-128 hash of main zpkg bytes（BLID 写入时该字段先归零参与哈希，回填）。主 zpkg + sidecar 通过 BLID 配对。

## Packed vs Indexed mode

| 维度 | Packed | Indexed |
|------|--------|---------|
| 用途 | 默认分发 / stdlib / runtime 加载 | 增量编译 cache（`.cache/`）|
| MODS section | 含；模块字节嵌入 | 缺 |
| FILE section | 缺 | 含；指向独立 .zbc |
| 模块加载 | 直接解 MODS bytes | 按 FILE 索引读 .zbc |
| 跨模块 STRS pool | 是 | 否（每个 .zbc 独立 STRS）|

切换：`ZpkgFile.Mode = ZpkgMode.Packed | ZpkgMode.Indexed`；`ZpkgWriter.Write` 内部按 mode 走 `WritePacked` / `WriteIndexed`。

## Sym-only sidecar（`.zsym`）

split-debug-symbols Phase 1（zpkg 0.3）引入，与 zbc 同名机制：

- 主 zpkg strip 后省 DBUG bytes，追加 BLID section（最后）
- 配套生成 `.zsym` sidecar zpkg，含 META + STRS + MDBG + BLID（标 `FlagSymOnly`）
- 主 zpkg + sidecar 通过 BLID 配对；runtime 加载主 zpkg 后探测同目录 `<name>.zsym`，build_id 匹配则把 MDBG 合入

Sidecar 不可作为项目包加载（reader 见 `FlagSymOnly` 即 bail）。

## 版本兼容性

**Strict-pin 政策**：reader 仅接受 `major == ZpkgWriter.VersionMajor && minor == ZpkgWriter.VersionMinor`。pre-1.0 z42 阶段不为旧 zpkg minor 提供兼容；每次 minor bump = 所有现存 zpkg artifacts 必须 regen（`z42 xtask.zpkg build stdlib`）。

- **当前版本**：`major=0, minor=10`（详见下方 Minor changelog）
- **触发 minor bump** 的事项：新增 section id / 已定义 section 字段语义变化 / **任意 zbc minor bump（强耦合）**
- **触发 major bump** 的事项（迄今未发生）：改 magic / 改 16B header layout / 改 section directory 12B 条目格式 / 弃用 packed 或 indexed 模式之一
- **zbc inner 与 zpkg outer minor 强耦合**：zbc minor 任意 bump → zpkg minor 必须同步 +1。历史唯一例外是 zbc 1.4 → 1.5（漏 bump），freeze-zpkg-v0 通过 0.5 → 0.6 catch-up 修正。

### Minor changelog

| minor | 日期 | 触发 spec | 引入内容 |
|:-----:|------|----------|---------|
| 0.1 | 2026-04-22 之前 | (initial) | zpkg 初始结构（packed / indexed mode 雏形）|
| 0.2 | 2026-05-09 | [tokenize-ir-and-zbc-bump](../../spec/archive/2026-05-09-tokenize-ir-and-zbc-bump/) | inner zbc 1.0；reader v1.0 IdMap decode |
| 0.3 | 2026-05-10 | [split-debug-symbols](../../spec/archive/2026-05-11-split-debug-symbols/) | inner zbc 1.2（LineTable 移入 DBUG）；per-member DBUG body；`FlagSymOnly` + MDBG + BLID（sidecar）|
| 0.4 | 2026-05-10 | split-debug-symbols Phase 4 | inner zbc 1.3（SIGS 加 per-param type names）|
| 0.5 | 2026-05-11 | [add-generic-func-constraint](../../spec/archive/2026-05-11-add-generic-func-constraint/) | inner zbc 1.4（func constraint bundle flag 0x40 + signature）|
| 0.6 | 2026-05-14 | [freeze-zpkg-v0](../../spec/archive/2026-05-14-freeze-zpkg-v0/) catch-up | inner zbc 1.5（Convert opcode；zbc 1.5 在 2026-05-13 fix-numeric-cast-lowering 落地，zpkg 当时漏 bump）|
| 0.7 | 2026-05-19 | [fix-array-default-init](../../spec/archive/2026-05-19-fix-array-default-init/) | inner zbc 1.6（`ArrayNew` opcode 追加 element type tag byte，驱动 per-type 默认值）|
| 0.8 | 2026-05-27 | [align-zbc-reader-writer-asymmetry](../../spec/archive/2026-05-27-align-zbc-reader-writer-asymmetry/) | inner zbc 1.7（SIGS / TYPE 在 u8 TypeTag 之后追加 u32 type_str_idx）+ zpkg outer SIGS 同步加 ret_type str_idx。修 Read→Write byte parity |
| 0.9 | 2026-05-27 | [jit-type-specialization](../../spec/changes/jit-type-specialization/) P0 step 0.3/0.4 | inner zbc 1.8（新 REGT section 承载 per-register `IrType`）+ zpkg packed module 在 DbugData 之后追加 `u32 RegtLen + bytes RegtData`，承载该 module 的 REGT 字节流。 |
| 0.10 | 2026-05-30 | [add-test-timeout-attribute](../../spec/changes/add-test-timeout-attribute/) | inner zbc 1.9（TIDX v=3 每条 TestEntry 追加 `timeout_ms: i32` 承载 `[Timeout(milliseconds: N)]`）。zpkg outer 无新字段，纯 minor bump 跟随 zbc 强耦合规则 |

> **如何 bump minor**：见 [`version-bumping.md` §"Bumping `.zbc` minor version"](../../../.claude/rules/version-bumping.md#bumping-zbc-minor-versionfreeze-zbc-v1-2026-05-14)（zbc bump 流程含 zpkg 同步条款）+ [§"Bumping `.zpkg` minor version (independent)"](../../../.claude/rules/version-bumping.md#bumping-zpkg-minor-version-independent)（仅 zpkg outer 变化场景）。

---

## Deferred / Future Work

> **reader-writer asymmetry**（2026-05-14 调查 / 2026-05-27 修复落地）：原 inner zbc SIGS / TYPE TypeTag 1 byte 编码 lossy（`"int"` → `I32` → canonical `"i32"`），导致 Read→Write 字节不对账。**已通过 [align-zbc-reader-writer-asymmetry](../../spec/archive/2026-05-27-align-zbc-reader-writer-asymmetry/) (zpkg 0.8 / zbc 1.7) 落地 Option A**：SIGS / TYPE 在 u8 TypeTag 之后追加 u32 type_str_idx；zpkg outer SIGS 同步加。ReadWriteRoundTrip CI 防线启用。

