# Design: Test Metadata Section (Compile-Time Discovery)

## Architecture

```
.z42 源码                C# 编译器                            zbc 二进制
─────────                ─────────                            ───────────

[Test]                   Lex / Parse / TypeCheck             Function table:
fn test_x()      ──►     AttributeBinder                ──►   - test_x (id=42)
                         ↳ 识别 z42.test.* attr name           - bench_y (id=43)
[Benchmark]              ↳ 收集 TestEntry 列表              
fn bench_y()             ↳ 写到 IrModule.TestIndex            New: TestIndex section:
                                                              [
                         IR Codegen / Emitter                   {method_id: 42, kind: Test, ...},
                         ↳ Writer 把 TestIndex 序列化到          {method_id: 43, kind: Benchmark, ...}
                            zbc 新 section                     ]
                            
                         ───────────────────────────────────────

                         Rust runtime (本 spec 侧)
                         ─────────────────────────
                         loader.rs：load_artifact
                         ↳ ZbcReader 读 TestIndex section
                         ↳ 返回 LoadedArtifact { ..., test_index: Vec<TestEntry> }

                         R3 runner（不在本 spec）：
                         ↳ 直接用 test_index 调度执行
```

## Decisions

### Decision 1: TestIndex 二进制格式（锁定）

新 zbc section ID = `0x0A`（紧接当前最大 + 1，具体值实施时确认）。

Section payload 布局（小端，与 zbc 既有约定一致）：

```
TestIndex Section
─────────────────
u32       magic       = 0x54494458  // "TIDX" big-endian, but file is little-endian so on-disk: 58 44 49 54
u8        version     = 1            // bump on incompatible changes
LEB128    entry_count
TestEntry entries[entry_count]

TestEntry (variable size)
─────────────────────────
LEB128    method_id              // index into module.functions[]
u8        kind                   // TestEntryKind: 1=Test, 2=Benchmark, 3=Setup, 4=Teardown, 5=Doctest (reserved)
u16       flags                  // bitset (see below)
LEB128    skip_reason_str_idx    // 0 = none; otherwise 1-based index into module.string_pool[]
LEB128    expected_throw_type_idx // 0 = none; otherwise 1-based index into module.type_pool[]
LEB128    test_case_count        // 0 if not parameterized
TestCase  test_cases[test_case_count]

TestCase (parameterized [TestCase] arg list, variable size)
─────────────────────────────────────────────────
LEB128    arg_repr_str_idx       // 1-based; string repr of args (parsed at runtime by R3 runner)
                                 // R4 will replace with typed encoding

TestFlags (u16 bitset)
─────────────────────
bit 0: skipped       (has [Skip])
bit 1: ignored       (has [Ignore])
bit 2: should_throw  (has [ShouldThrow<E>])
bit 3: doctest       (reserved for v0.2)
bit 4-15: reserved (must be 0)
```

**字符串池复用**：所有字符串字段都进现有 `module.string_pool`，不复制。

**method_id 引用**：直接是函数表索引；reader 用 `module.functions[method_id]` 拿元数据。

### Decision 2: C# 端类型映射（锁定）

[src/compiler/z42.IR/Metadata/TestEntry.cs](src/compiler/z42.IR/Metadata/TestEntry.cs)：

```csharp
namespace Z42.IR.Metadata;

public enum TestEntryKind : byte
{
    Test      = 1,
    Benchmark = 2,
    Setup     = 3,
    Teardown  = 4,
    Doctest   = 5,  // reserved for v0.2
}

[Flags]
public enum TestFlags : ushort
{
    None        = 0,
    Skipped     = 1 << 0,
    Ignored     = 1 << 1,
    ShouldThrow = 1 << 2,
    Doctest     = 1 << 3,
    // bits 4-15 reserved
}

public readonly record struct TestEntry(
    int                            MethodId,
    TestEntryKind                  Kind,
    TestFlags                      Flags,
    int                            SkipReasonStrIdx,        // 0 = none
    int                            ExpectedThrowTypeIdx,    // 0 = none
    IReadOnlyList<TestCase>        TestCases                // empty for non-parameterized
);

public readonly record struct TestCase(
    int ArgReprStrIdx                                      // 1-based string pool index
);
```

[src/compiler/z42.IR/IrModule.cs] 加 `IReadOnlyList<TestEntry> TestIndex { get; init; } = Array.Empty<TestEntry>();`。

### Decision 3: Rust 端类型镜像（锁定）

[src/runtime/src/metadata/test_index.rs](src/runtime/src/metadata/test_index.rs)：

```rust
use anyhow::{bail, Context, Result};

pub const TEST_INDEX_MAGIC: u32 = 0x58_44_49_54;  // "XDIT" reading le; on-disk "TIDX" be
pub const TEST_INDEX_VERSION: u8 = 1;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum TestEntryKind {
    Test      = 1,
    Benchmark = 2,
    Setup     = 3,
    Teardown  = 4,
    Doctest   = 5,
}

bitflags::bitflags! {
    #[derive(Debug, Clone, Copy, PartialEq, Eq)]
    pub struct TestFlags: u16 {
        const SKIPPED      = 1 << 0;
        const IGNORED      = 1 << 1;
        const SHOULD_THROW = 1 << 2;
        const DOCTEST      = 1 << 3;
    }
}

#[derive(Debug, Clone)]
pub struct TestEntry {
    pub method_id:               u32,
    pub kind:                    TestEntryKind,
    pub flags:                   TestFlags,
    pub skip_reason_str_idx:     u32,  // 0 = none
    pub expected_throw_type_idx: u32,  // 0 = none
    pub test_cases:              Vec<TestCase>,
}

#[derive(Debug, Clone)]
pub struct TestCase {
    pub arg_repr_str_idx: u32,
}

/// Decode a TestIndex section payload (without the section ID/length wrapper).
pub fn read_test_index(payload: &[u8]) -> Result<Vec<TestEntry>> {
    // ... LEB128 + magic + version validation
}
```

`bitflags` crate 已在 Cargo.toml 中（如果没有，本 spec 加）。否则用裸 `u16` + 常量。

### Decision 4: AttributeBinder 集成

需要先调研当前 attribute binding 的位置。预期在 `Z42.Semantics` 命名空间。集成点：

```csharp
// src/compiler/z42.Semantics/TestAttributeNames.cs (NEW)
namespace Z42.Semantics;

public static class TestAttributeNames
{
    public const string Namespace = "z42.test";

    public const string Test       = "z42.test.Test";
    public const string Benchmark  = "z42.test.Benchmark";
    public const string Skip       = "z42.test.Skip";
    public const string Ignore     = "z42.test.Ignore";
    public const string ShouldThrow= "z42.test.ShouldThrow";
    public const string Setup      = "z42.test.Setup";
    public const string Teardown   = "z42.test.Teardown";
    public const string TestCase   = "z42.test.TestCase";

    public static bool IsTestAttr(string fullName) => fullName.StartsWith("z42.test.");
}
```

AttributeBinder 在收集函数 attributes 后，遍历 attributes 表：

```csharp
// pseudocode
foreach (var fn in module.Functions)
{
    var entry = new TestEntryBuilder { MethodId = fn.Id };
    foreach (var attr in fn.Attributes)
    {
        if (!TestAttributeNames.IsTestAttr(attr.FullName)) continue;
        switch (attr.FullName)
        {
            case TestAttributeNames.Test:        entry.Kind = TestEntryKind.Test; break;
            case TestAttributeNames.Benchmark:   entry.Kind = TestEntryKind.Benchmark; break;
            case TestAttributeNames.Setup:       entry.Kind = TestEntryKind.Setup; break;
            case TestAttributeNames.Teardown:    entry.Kind = TestEntryKind.Teardown; break;
            case TestAttributeNames.Skip:        entry.Flags |= TestFlags.Skipped; entry.SkipReasonStrIdx = AddToPool(attr.Args[0]); break;
            case TestAttributeNames.Ignore:      entry.Flags |= TestFlags.Ignored; break;
            case TestAttributeNames.ShouldThrow: entry.Flags |= TestFlags.ShouldThrow; entry.ExpectedThrowTypeIdx = LookupTypeIdx(attr.TypeArgs[0]); break;
            case TestAttributeNames.TestCase:    entry.TestCases.Add(new TestCase(...)); break;
        }
    }
    if (entry.Kind != null) module.TestIndex.Add(entry.Build());
}
```

**本 spec 不做语义校验**（如 `[Setup]` 不能与 `[Test]` 共存）；R4 加。

### Decision 5: zbc Writer / Reader 集成

`ZbcWriter`：
- 现有 sections 写完后，**仅当** `module.TestIndex.Count > 0` 才追加 TestIndex section
- Section header：`[u8 section_id=0x0A] [LEB128 payload_length] [payload...]`

`ZbcReader`：
- 主循环识别 section_id；遇到 0x0A 时调用新 `ReadTestIndex` 方法
- 兼容：旧 zbc 无 TestIndex section → reader 读 EOF，TestIndex 为空（向前兼容）

**Reader 容错**：`magic` 不对 → bail；`version` 不为 1 → bail（pre-1.0 不留兼容）

### Decision 6: 错误码占位

[docs/design/error-codes.md](docs/design/error-codes.md) 注册（语义留 R4 填）：

| 码 | 含义（R4 时定义） |
|----|----|
| Z0911 | `[Test]` 函数签名错误（必须 fn() -> void） |
| Z0912 | `[Benchmark]` 函数签名错误 |
| Z0913 | `[ShouldThrow<E>]` 中 E 不是 Exception 子类型 |
| Z0914 | `[Skip(reason)]` 缺失 reason 参数 |
| Z0915 | `[Setup]` / `[Teardown]` 函数签名错误 |

本 spec 不实现这些诊断；只在文档预留。

### Decision 7: 测试 attribute 不进入 ABI 暴露表

某些 z42.IR section 是 ABI 一部分（exported types / public methods）。`TestIndex` **不是** ABI —— 它是工具消费的元数据。Writer 不把测试函数从 export 表中移除（开发者自己控制 public/private），但 TestIndex 本身不参与 ABI 兼容性。

### Decision 8: 跨语言契约测试

[src/runtime/tests/zbc_compat.rs](src/runtime/tests/zbc_compat.rs) 加跨语言契约测试：

1. 手写 .z42 含 8 个 attribute（`examples/test_demo.z42`）
2. 用编译器编译为 .zbc
3. Rust 端 `read_test_index` 解析，比对：
   - entry 数量 == 期望
   - 每个 entry 的 method_id / kind / flags / 字符串值与 .z42 源码一致
4. 重写测试也走这个：用 fixture .zbc 检验 reader 边界（empty section、缺 magic、错 version）

## Implementation Notes

### bitflags crate 接入

如 [src/runtime/Cargo.toml](src/runtime/Cargo.toml) 没有 `bitflags`，本 spec 加 `bitflags = "2"` 到 dependencies。

### LEB128 编解码

zbc 现有 sections 应已有 LEB128 helpers；reader/writer 复用。如无，本 spec 不引入新的（用 `bincode` 现有 varint 编码）。

### IrModule 序列化路径

Module 已 `[Serializable]` —— TestIndex 字段直接进 bincode 流。

### 测试 attribute 在 TypeChecker 阶段才能拿到完整 method_id

method_id 在 IR codegen 阶段才确定（function 索引）。`AttributeBinder` 跑在 IR 阶段（不是 TypeCheck）—— 这意味着所有 attribute 信息已在 AST，但 method_id 要从 emitter 拿。

需要 emitter 在 emit 完函数后回调 AttributeBinder 注册 (method_id, attrs)。或者 AttributeBinder 提供 `BindMethodId(astNode, methodId)` API，emitter 在 emit 时调用。

**精确集成点 由实施时调研编译器现状决定**；本 spec 只规定"必须最终能拿到 method_id 写入 TestEntry"。

### 零运行时性能成本

TestIndex section 仅在 R3 runner 启动时读；普通 z42vm 跑应用不读 TestIndex。所以对运行时无任何性能影响。

## Testing Strategy

### C# 单元测试

- `AttributeBinderTests.RecognizesAllEightTestAttributes`：每个 attribute name 收集成对应 TestEntry
- `AttributeBinderTests.MultipleTestCaseAttributesProduceMultipleTestEntries`：参数化测试展开
- `TestIndexRoundTripTests.SerializeDeserialize`：构造 IrModule.TestIndex → Write → Read → 比对
- `TestIndexRoundTripTests.EmptyTestIndexNotEmittedToZbc`：无 [Test] 时不写 section
- `TestIndexRoundTripTests.OldZbcWithoutTestIndexReadsAsEmpty`：构造无 section 的 zbc，Reader 不报错，TestIndex.Count == 0

### Rust 单元测试

- `read_test_index_smoke`：手工构造合法 payload bytes → decode → 比对
- `read_test_index_rejects_wrong_magic`
- `read_test_index_rejects_wrong_version`
- `read_test_index_handles_empty`
- `read_test_index_decodes_all_kinds`：5 种 kind variant 都解码出正确 enum

### 跨语言契约测试

- `examples/test_demo.z42` 含 8 个 attribute 的方法
- C# `dotnet test` 编译它生成 `test_demo.zbc`
- Rust `cargo test` 加载 .zbc → 解 TestIndex → 比对预期 8 个 entry 的 kind/flags/字符串

### 验证矩阵

| Scenario | 测试 |
|----------|------|
| 编译器识别 `[Test]` | C# AttributeBinderTests |
| 编译器识别 `[Benchmark]` | 同上 |
| 编译器识别 `[Skip(reason)]` 写 reason | 同上 |
| 编译器识别 `[ShouldThrow<E>]` 写 type_idx | 同上 |
| 编译器识别 `[TestCase(args)]` 多实例 | 同上 |
| zbc 写入 TestIndex section | TestIndexRoundTripTests |
| zbc 读取 TestIndex section | 同上 |
| 旧 zbc 兼容（无 section） | 同上 |
| Rust 解码 LEB128 + magic + version | Rust 单测 |
| 跨语言契约 | examples/test_demo.z42 + zbc_compat.rs |
