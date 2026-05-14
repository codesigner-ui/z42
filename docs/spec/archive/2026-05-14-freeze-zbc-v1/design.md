# Design: Freeze `.zbc` v1 wire format

## Architecture

```
冻结对象（pre-1.0 持续 strict-pin，不留兼容）：

  ┌─────────────────────────────────────────────────────────────────────┐
  │  v1 STRUCTURAL FRAMING        ← 本 spec 锁定                         │
  │                                                                      │
  │   Magic "ZBC\0" (4B)                                                 │
  │   16-byte header layout                                              │
  │     ├─ major:    u16   = 1                                           │
  │     ├─ minor:    u16   (current 5)                                   │
  │     ├─ flags:    u16   (ZbcFlags bitset)                             │
  │     ├─ sec_cnt:  u16                                                 │
  │     └─ payload offset (implicit = 16 + 12*sec_cnt)                   │
  │                                                                      │
  │   Section directory entry (12B / 项)                                 │
  │     ├─ tag:      [4B; ASCII]                                         │
  │     ├─ offset:   u32                                                 │
  │     └─ size:     u32                                                 │
  │                                                                      │
  │   Reader fetches sections by tag from dict (unknown tags → skip)     │
  └─────────────────────────────────────────────────────────────────────┘

  ┌─────────────────────────────────────────────────────────────────────┐
  │  v1 minor 可变（持续 iteration，每次 bump = 全部 artifacts regen）   │
  │                                                                      │
  │   - 新增 opcode（已用 0x00–0xB1，预留 0xB2–0xFF）                    │
  │   - 已定义 section 的内部字段语义                                    │
  │   - 新增 section id（reader 自动 skip 旧 zbc 无此 section）          │
  │   - 已定义 section 的内部 sub-version（TIDX / DBUG / FRCS）          │
  └─────────────────────────────────────────────────────────────────────┘

  ┌─────────────────────────────────────────────────────────────────────┐
  │  触发 major bump (v2)                                                │
  │                                                                      │
  │   - 改 magic                                                         │
  │   - 改 header 字段宽度 / 排列                                        │
  │   - 改 section directory 条目格式                                    │
  │   - Token 编码空间重划（IMPORT_BASE / UNRESOLVED 等）                │
  └─────────────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: 锁定 strict-pin 语义，不实施前向 / 后向兼容

**问题**：minor bump 之后旧 zbc artifacts 怎么办？

**选项**：

- A — strict-pin：reader 拒收任何 minor != current_supported_min；旧 artifacts 必须 regen
- B — forward-compat：reader 接受 minor < current；遇未识别 opcode → `UnsupportedOpcode`

**决定**：A。

**理由**：

- 与 `.claude/rules/workflow.md` "不为旧版本提供兼容" 强对齐
- 当前代码 `ZbcReader.cs:35-37` 已经是 A（`minor < 5 → bail`）；文档（zbc.md L428-429）错误描述为 B → **doc-code 漂移**，本 spec 校正为 A
- 语言 pre-1.0 持续迭代，B 的实施成本（保留多版本 opcode 表 / 跨版本 reader fuzz）显著大于 regen 旧 artifacts 的成本（一行 `./scripts/regen-golden-tests.sh`）

### Decision 2: 当前 minor 由 writer 单点声明，reader 强制 `==`

**问题**：是 `minor >= MIN` 接受窗还是 `minor == CURRENT` 精确匹配？

**决定**：精确匹配。`ZbcReader` 拒收 `minor != ZbcWriter.VersionMinor`。

**理由**：

- 配合 Decision 1 的 strict-pin，精确匹配是最简单实现 —— minor 不存在 "window"，每次 bump = 全 break
- 现状：`minor < 5 → bail` + writer 写 5 + reader 不检查 upper bound = 实际上是 "≥ 5 接受"（forward-leaky）
- 改成 `minor != WRITER_MINOR → bail`（一行修改），关闭 leak
- 单点声明：`ZbcWriter.VersionMinor` const 是唯一 truth，reader 引用同 const

**实施细节**：

```csharp
// ZbcWriter.cs（已存在）
public const ushort VersionMajor = 1;
public const ushort VersionMinor = 5;

// ZbcReader.cs（改）
if (major != ZbcWriter.VersionMajor)
    throw new InvalidDataException($"zbc major {major} not supported (expected {ZbcWriter.VersionMajor})");
if (minor != ZbcWriter.VersionMinor)
    throw new InvalidDataException(
        $"zbc minor {minor} not supported (writer is at {ZbcWriter.VersionMinor}); " +
        $"regen via ./scripts/regen-golden-tests.sh");
```

Rust 端同：`zbc_reader.rs` 加 `const ZBC_VERSION_MAJOR: u16 = 1; const ZBC_VERSION_MINOR: u16 = 5;` 并精确匹配。

### Decision 3: 未识别 section 由 reader 静默跳过（已默认行为）

**问题**：reader 遇到 dict 中有但代码不识别的 section tag 怎么办？

**决定**：静默跳过（dict-lookup 实现已默认；本 spec 加 test 固化）。

**理由**：

- 当前 `ZbcReader.ReadSection<T>` 通过 `dir.TryGetValue(tag, ...)` 取，未注册的 tag 自然 fall-through 到 `defaultValue`
- 这是 v1 内"加 section 不破坏旧 reader"的唯一兼容点（**例外** Decision 1）—— 因为新增 section 不会破坏旧 reader 解 STRP/FUNC 等核心 section
- 但前提是新 section 不携带必须信息：如果新 section 是 "metadata 必须读否则跑不起来"，那 minor bump 本身就 break（Decision 1）

**测试固化**：写一个 zbc 含 `ZZZZ` 段，验证 reader 解出正常 module 且不抛错。

### Decision 4: 字节级 golden fixture 来源

**问题**：golden zbc bytes 怎么生成 + 维护？

**选项**：

- A — 手工 hex 编辑 .zbc 文件
- B — 用 ZbcWriter 跑 z42 源码生成 → check in 字节
- C — 用 ZbcWriter API 直接构造（不走 z42 → IR 路径，最小化 fixture）

**决定**：B。fixture 目录形式：

```
src/tests/zbc-format/
├── README.md                    ← 目录职责 + 维护流程
├── generate-fixtures.sh         ← 一键 regen：跑 .z42 → ZbcWriter → cp 到 fixture
├── empty/
│   ├── source.z42               ← `module Empty { }`
│   ├── source.zbc               ← 字节（check in）
│   └── expected.json            ← 归一化 parse 结果
├── strp-func-minimal/
├── with-dbug-blid/
├── with-tidx/
├── with-frcs/
└── cross-import-token/
```

**理由**：

- B 跑通整个 pipeline，golden = 实际产物 → 任何 writer 改动会立即看到 fixture diff
- A 维护代价高（每次 bump 要手算 offset）；C 绕过 IR pipeline，覆盖不到真实 codegen 路径
- 5-7 个 fixture 覆盖关键 layout（empty / 基础 / 含 debug / 含 test index / 含 cross-import / 含 frame info）

### Decision 5: Golden 比对策略 —— 字节级 + 归一化 JSON 双轨

**问题**：fixture diff 该看字节还是看 parse 后的语义？

**决定**：两个都做。

- **字节级**：`source.zbc` 文件 check in，CI 把 `generate-fixtures.sh` 跑一遍后 `diff` 当前生成 vs check-in
  - 价值：catch 任何 layout 偷偷变
  - 失败信息：`expected XX bytes, got YY` —— 不直观但确定
- **归一化 JSON**：每个 fixture 同目录 `expected.json` 是 reader 解出来 + 标准化后的 module representation
  - 价值：reader 解析路径不偷偷变；失败信息直接显示"哪个字段语义变了"
  - 归一化包含：strs / func bodies / class descs / opcode mnemonics / line tables / build_id（如有）

CI 同时跑两个轨：字节失败 = layout 漂；JSON 失败 = 语义漂。

### Decision 6: minor bump 程序定义（写入 workflow.md）

**问题**：未来谁 bump minor 时遵循什么流程？

**决定**：写入 `.claude/rules/workflow.md` 新子节 "Bumping `.zbc` minor"：

```markdown
### Bumping `.zbc` minor version

凡修改 wire format（新增 opcode / 改 section 字段语义 / 新增 section），必须：

1. `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` —— `VersionMinor` ++ 并在常量旁注释本次 bump 内容
2. `src/runtime/src/metadata/zbc_reader.rs` —— 同步 `ZBC_VERSION_MINOR` 常量
3. `docs/design/runtime/zbc.md` —— changelog 表加一行（minor、日期、spec ID、修改摘要）
4. `src/tests/zbc-format/generate-fixtures.sh` 跑一遍 regen 所有 fixture
5. 提交需在同一 commit 内包含 1+2+3+4，CI invariant test 验证 writer.VersionMinor == reader.ZBC_VERSION_MINOR
```

### Decision 7: Reader 拒收 message 提示 regen 路径

**问题**：旧 zbc 加载失败时给用户什么提示？

**决定**：错误 message 显式提示 `./scripts/regen-golden-tests.sh`，避免用户卡住。

```
zbc 1.4 not supported; writer is at 1.5.
regen via ./scripts/regen-golden-tests.sh  (pre-1.0 zbc artifacts are not preserved across minor bumps).
```

## Implementation Notes

### zbc.md changelog 表（要落地的版本）

| minor | 日期 | 引入内容 | 触发 spec |
|:-----:|------|---------|----------|
| 1.0 | 2026-05-09 | 重设结构骨架（dropping pre-1.0 sequential format）；Tokenizable IR + 0x8000_0000 IMPORT_BASE | tokenize-ir-and-zbc-bump |
| 1.1 | TODO 查 archive | ?  |  |
| 1.2 | 2026-05-10 | DBUG section 统一 + BLID section（split-debug-symbols Phase 1-3）| split-debug-symbols |
| 1.3 | 2026-05-10 | SIGS 携带 per-param 类型名（split-debug-symbols Phase 4）| split-debug-symbols |
| 1.4 | TODO 查 archive | ?  |  |
| 1.5 | 2026-05-13 | `Convert` opcode (0xB1) 显式数值类型转换 | fix-numeric-cast-lowering |

> 1.1 / 1.4 内容实施时 grep archive 补；找不到对应 spec 时标 "Not committed via spec; reconstructed from git log"。

### 归一化 JSON 字段集（避免不稳定字段）

```jsonc
{
  "header": { "major": 1, "minor": 5, "flags": ["HasDebug"], "section_count": 6 },
  "sections": ["NSPC", "STRS", "TYPE", "FUNC", "DBUG", "BLID"],
  "module": "test.empty",
  "classes": [
    { "name": "Foo", "fields": [...], "methods": [...] }
  ],
  "functions": [
    {
      "name": "main",
      "params": [{ "name": "argv", "type": "Array<String>" }],
      "ret_type": "i32",
      "opcodes": ["LoadConst i32 0", "Ret"]
    }
  ],
  "build_id": "0123456789abcdef0123456789abcdef"   // 16 bytes hex; only if BLID present
}
```

**排除字段**（避免 spurious diff）：

- 绝对 offset（依赖 layout）
- 字节 size（依赖编码紧凑度）
- 时间戳（不存在但确认）

### `src/tests/zbc-format/generate-fixtures.sh` 行为

```bash
#!/usr/bin/env bash
# Regenerate all zbc-format golden fixtures from sources.
# Run after legitimate format changes (writer bump). CI does NOT call this;
# it diffs check-in bytes against fresh build.
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
DRIVER_DLL="artifacts/build/compiler/z42.Driver/bin/z42c.dll"

for case_dir in "$SCRIPT_DIR"/*/; do
    [ -f "$case_dir/source.z42" ] || continue
    name=$(basename "$case_dir")
    dotnet "$DRIVER_DLL" "$case_dir/source.z42" --emit zbc -o "$case_dir/source.zbc"
    # expected.json regenerated by C# test helper invoked in --regen mode
    dotnet "$DRIVER_DLL" "$case_dir/source.zbc" --emit golden-json -o "$case_dir/expected.json"
    echo "✓ $name"
done
```

需要 `z42c --emit golden-json` 这个新 emit mode（产归一化 JSON）。如果觉得侵入 driver 太重，fallback：写独立 `dotnet ZbcGoldenDump.dll` 工具，放 `src/tools/zbc-golden-dump/`。

> **实施期决策点**：preferred = 新 emit mode（一个开关 + 一段格式化器）；fallback = 独立工具。先尝试 emit mode，复杂度超出预期再 fallback。

## Testing Strategy

| 层次 | 验证方式 | 失败信息 |
|------|---------|---------|
| **C# unit** | `FormatInvariantTests.WriterReaderRoundTrip` | "writer at minor X, reader rejects minor X" |
| **C# unit** | `FormatInvariantTests.UnknownSectionSkipped` | "reader threw on unknown section ZZZZ" |
| **C# unit** | `FormatInvariantTests.MajorMismatchRejected` | "reader accepted major 2; expected throw" |
| **C# unit** | `FormatInvariantTests.MinorMismatchRejected` | "reader accepted minor 4; expected throw" |
| **C# unit** | `FormatInvariantTests.WriterDeterministic` | "same IR module → different bytes (Δ=NN)" |
| **C# golden** | `FormatGoldenTests` × 6 fixture | "fixture X: byte diff at offset N" / "fixture X: JSON field Y mismatch" |
| **C# round-trip** | `FormatInvariantTests.DisasmReassembleByteEqual` × 3 fixture | "fixture X: disasm → assemble → bytes differ at offset N" |
| **Rust unit** | `zbc_format_invariant_tests` | 对应 4 个 C# 测试的 Rust 版本（minor/major reject + unknown skip + writer-reader round-trip via metadata API）|

GREEN 入口 `./scripts/test-all.sh`（不变；新增测试纳入既有 `dotnet test` + `cargo test --lib` stages）。

## Deferred / Future Work

### freeze-A1: TIDX / DBUG / FRCS section 内部 sub-version 也接入 invariant test

- **来源**：本 spec 决定 Decision 3 + Phase 3
- **触发原因**：v0 invariant test 只校 zbc 整体 magic/major/minor，不深入到 section 内部 sub-version
- **触发条件**：任何 sub-version 出现不一致问题（如 TIDX writer 2 / reader 1 漂移）

### freeze-A2: v2 wire format 设计（如发生重大架构调整）

- **来源**：本 spec 决定 Decision 1
- **触发原因**：v1 框架沿用至自举；v2 需要时再独立设计
- **触发条件**：明确需要改 magic / 改 directory 条目宽度 / 重划 Token 空间

### freeze-A3: 字节 round-trip CI invariant 接入打包 release pipeline

- **来源**：implementation note
- **触发原因**：`.github/workflows/release.yml` 不跑 golden（只跑 build + package）
- **触发条件**：发现 release artifacts 里的 stdlib zpkg 与本地构建字节漂移
