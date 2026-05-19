# Proposal: fix `new T[N]` default initialization

## Why

`new byte[N]` / `new int[N]` / `new bool[N]` 等数组分配在 z42 VM 当前实现里
**永远把元素填 `Value::Null`**，无视 T 类型。这违反了 `Instruction::ArrayNew`
opcode 注释 "Allocate a zero-initialised array" 的契约，也违反了所有主流语言
（C# / Java / Rust / Go）对数组默认值的合理预期。

### 触发症状

写任何"先分配再填"模式的纯脚本 stdlib 代码都会踩这个坑：

```z42
byte[] arr = new byte[64];
int v = (int)arr[0];   // VM error: cast Null to int 直接 crash
```

实施 `z42.crypto` SHA-256 时就被这个 bug 卡住：每次 `_pad()` 返回 `new byte[totalLen]`，下游 `_processBlock` 读 `(int)block[o] & 0xFF` 立刻 crash。
绕过方法是手写 zero-init 循环：

```z42
static byte[] _zeroBytes(int n) {
    byte[] arr = new byte[n];
    int i = 0;
    while (i < n) { arr[i] = (byte)0; i = i + 1; }
    return arr;
}
```

z42.core 的 `Dictionary` 也踩过同样的坑，注释 `// VM 默认初始化 bool[] 为 Null；显式置 false` + 显式 `while (i < 8) { this.occupied[i] = false; i = i + 1; }`
循环到现在还在。

### 不修会怎样

- 每个写 stdlib 的人都要重新踩一遍坑，且 bare "VM error" 无诊断信息很难定位
- 现有 workaround 是 O(N) 显式 loop init，掩盖了运行时本应一次 memset 的高效路径
- 与 C# / Java / Rust 对数组语义的认知预期不一致；新用户写 `byte[]` 必定 crash

## What Changes

### 数据流：opcode 携带 element type tag

`Instruction::ArrayNew` 增加 **element type tag**（zbc 1.x → 1.(x+1) minor bump）。
编译器从 `BoundArrayCreate.ElemType` 取 tag 写入，运行时读出后调
`metadata::default_value_for(...)` 替代当前硬编码的 `Value::Null`。

具体 wire 编码（u8 byte vs string pool 索引）在 [design.md](design.md) 决策。

`default_value_for` 现成（[runtime/src/metadata/types.rs:28](src/runtime/src/metadata/types.rs#L28)），
已正确覆盖：
- `int / long / short / byte / sbyte / ushort / uint / ulong / i8..i64 / u8..u64` → `Value::I64(0)`
- `double / float / f32 / f64` → `Value::F64(0.0)`
- `bool` → `Value::Bool(false)`
- `char` → `Value::Char('\0')`
- 引用类型 / 未知 → `Value::Null`（保留现状）

### 配套清理

修完 bug 后顺手清掉 workaround：

- `z42.core/Collections/Dictionary.z42` — 删 `bool[] occupied` 的 init 循环
- `z42.crypto/Sha256.z42` — 删 `_zeroBytes` helper（其他 stdlib 如果也有类似 workaround 一并清）

## Scope（允许改动的文件）

| 文件路径 | 变更 | 说明 |
|---|---|---|
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | `VersionMinor++` + ArrayNew 写 elem type tag |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs` | MODIFY | ArrayNew opcode emission |
| `src/compiler/z42.IR/Instructions/ArrayNewInstr.cs` | MODIFY | 加 `ElemType` 字段（如果是单独文件；否则在 Instruction 定义处） |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` | MODIFY | 发 ArrayNewInstr 时从 BoundArrayCreate 取 ElementType |
| `src/compiler/z42.Project/ZpkgWriter.cs` | MODIFY | `VersionMinor++`（zpkg 联动） |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | `ZBC_VERSION_MINOR++` + `ZPKG_VERSION_MINOR++` + 读 elem type tag |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | `Instruction::ArrayNew` 加 element type tag 字段 |
| `src/runtime/src/interp/exec_array.rs` | MODIFY | 用 `default_value_for(...)` 替代 `Value::Null` |
| `src/runtime/src/interp/exec_array_tests.rs` | NEW | 测试每种元素类型 default 值 |
| `src/runtime/src/jit/helpers/array.rs` | MODIFY | JIT helper 走同样路径 |
| `src/runtime/src/jit/translate.rs` | MODIFY | JIT translate.rs 模式匹配新字段 |
| `src/compiler/z42.Tests/IncrementalBuildIntegrationTests.cs` | MODIFY | 文件计数若受影响则同步 |
| `src/tests/zbc-format/generate-fixtures.sh` | (regen) | 6 fixture 全部重生（zbc minor 变了） |
| `src/tests/zpkg-format/generate-fixtures.sh` | (regen) | 4 fixture 全部重生（zpkg minor 联动） |
| `scripts/regen-golden-tests.sh` | (run) | 重生所有 stdlib + 测试 zbc |
| `docs/design/runtime/zbc.md` | MODIFY | Minor changelog 加一行 |
| `docs/design/runtime/zpkg.md` | MODIFY | Minor changelog 加一行 |
| `src/libraries/z42.core/src/Collections/Dictionary.z42` | MODIFY | 删 `bool[] occupied` 的 init 循环（清理 workaround） |
| `src/libraries/z42.crypto/src/Sha256.z42` | MODIFY | 删 `_zeroBytes` helper，直接用 `new byte[N]` |

**只读引用**：
- `src/runtime/src/metadata/types.rs` — 看 `default_value_for` 已有支持

## Out of Scope

- **多维数组**（`new int[N][M]`）—— z42 当前一维数组，多维待 L3
- **`new T[N]` 当 T 是用户类**：仍 default null（与 reference type 语义一致），不变
- **JIT 实现**：如果 JIT 还没实现 ArrayNew，本 spec 只覆盖 interp；JIT 路径见 design.md
- **GC 性能优化**：default 值填充用 `vec![v; n]`，不引入 memset intrinsic
- **`new T[N]{...}` literal 初始化**：本来就走 ArrayNewLit，不受影响

## Open Questions

无。`default_value_for` 行为表已经在 [`types.rs:28`](src/runtime/src/metadata/types.rs#L28) 固化，
elem_type_tag 用现有的 type tag 字符串（`"byte"` / `"int"` / `"i64"` etc.）即可。
