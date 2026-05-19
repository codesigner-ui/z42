# Design: fix `new T[N]` default initialization

## Architecture

```
Compile time:
  BoundArrayCreate(Size, ElemType: Z42Type)
        ↓ FunctionEmitterExprs.VisitArrayCreate
  ArrayNewInstr(Dst, Size, ElemType)              ← IR record, ElemType new
        ↓ ZbcWriter.Instructions
  [OP_ARRAY_NEW][dst type tag][dst u16][size u16][ElemTypeTag u8]
        ↓ wire (zbc 1.x → 1.(x+1))

Load time:
  ZbcReader (Rust + C#)
        → Instruction::ArrayNew { dst, size, elem_tag: u8 }

Execution:
  Interp  exec_array::array_new(dst, size, elem_tag)
  JIT     jit_array_new(frame, ctx, dst, size, elem_tag)
        ↓
  default_value_for_tag(elem_tag)  →  Value::I64(0) / Value::F64(0.0) / Value::Bool(false) / Value::Char('\0') / Value::Null
        ↓
  heap.alloc_array(vec![v; n])
```

## Decisions

### Decision 1: Wire-format encoding of element type — single u8 byte (TypeTag)

**问题：** ArrayNew opcode 需要在 wire 上携带 element type 信息。两种自然方案。

**选项：**

- **A. 单 u8 byte（`TypeTags`）** — 与其他 opcode 头部 `typ` byte 同一编码空间，使用既有 `TypeTags::Bool / I64 / F64 / Char / Object` 等常量；1 字节，无 string pool 间接
- **B. u32 string pool index** — 写入 `"byte" / "int" / ...` 字符串到 pool；reader 通过 `pool_str_owned` 拿回 String；与 `default_value_for(&str)` 直接对接

**决定：** 选 A（u8 TypeTag byte）。

**理由：**

1. **与现有约定一致**：opcode 头部已有 `typ` byte（dst 的类型 tag），element type 走同套 `TypeTags` 常量系统，读 / 写器无需新增映射机制
2. **节省字节**：每个 `new T[N]` 站点节省 3 字节（u8 vs u32）+ 一条 string pool 条目；stdlib 中 `new T[N]` 调用点上百个，累计可观
3. **足够表达**：`TypeTags` 涵盖 Bool / I8..I64 / U8..U64 / F32 / F64 / Char / Str / Object / Array，正好对应 `default_value_for` 的所有 case；用户类全部归入 `Object` 并 fall-back 到 `Null`，语义完全一致
4. **运行时实现简单**：在 `metadata/types.rs` 增 `default_value_for_tag(tag: u8) -> Value`，内部 match `TYPE_TAG_*` 常量，无 string compare

**Rejected B 的原因**：增加 string pool 间接性而无实际收益；`default_value_for(&str)` 的字符串 key 是历史包袱（来自 `FieldSlot.type_tag`），不应扩散到新代码

### Decision 2: ArrayNewLit 不动

**问题：** `ArrayNewLit` 也是分配数组，是否也需要 element type？

**决定：** 不动。`ArrayNewLit` 从 element register 列表分配，每个 slot 显式从 reg 读 Value；不存在"未初始化"语义，无 bug。保持现有 wire 不变 = 节省 minor bump 影响面。

### Decision 3: Tag derivation — 单一 Z42Type → byte 函数

**问题：** 从 `BoundArrayCreate.ElemType` (一个 `Z42Type`) 推导出 wire byte，存在多个候选路径：直接走 `Z42Type` 名字 → `TypeTags.FromString`，或先到 `IrType` 再 `TypeTagFromIrType`。

**决定：** 复用 `IrType` 中转。理由：

- `FunctionEmitterExprs` 已有 helper `ToIrType(Z42Type)`，覆盖所有原语映射
- `TypeTagFromIrType(IrType)` 已存在并测试稳定
- 全链路与其他 dst-type 走的路径完全一致 → 避免引入第二条独立 mapping，减少 drift 风险

`ArrayNewInstr` 的 IR 字段定义为：

```csharp
public sealed record ArrayNewInstr(
    TypedReg Dst,
    TypedReg Size,
    IrType ElemType   // ← new; default = IrType.Ref for back-compat 调用点
) : IrInstr;
```

写出时直接 `w.Write(TypeTagFromIrType(i.ElemType))`，读回时不还原到 IR record（Rust 侧也只关心 byte）。

### Decision 4: 无 fallback / 无兼容路径（pre-1.0 strict-pin）

旧 `.zbc` 用旧 minor，加载即报版本不匹配，**无元素类型缺省回填**。与 [philosophy.md 不为旧版本提供兼容](../../.claude/rules/philosophy.md) 一致。所有 stdlib + 测试 zbc 通过 `./scripts/regen-golden-tests.sh` 重生。

### Decision 5: Runtime `Instruction::ArrayNew` 字段命名

```rust
ArrayNew {
    #[serde(with = "typed_reg_serde")] dst: Reg,
    #[serde(with = "typed_reg_serde")] size: Reg,
    elem_tag: u8,   // TypeTag byte from zbc
}
```

`elem_tag` 而非 `elem_type_tag`，与项目内 `typ` byte 简洁命名习惯一致。

### Decision 6: `default_value_for_tag` 函数

新函数（不替换现有 `default_value_for(&str)`，后者仍服务 `FieldSlot.type_tag` String 路径）：

```rust
pub fn default_value_for_tag(tag: u8) -> Value {
    use crate::metadata::type_tags::*;
    match tag {
        TAG_BOOL => Value::Bool(false),
        TAG_I8 | TAG_I16 | TAG_I32 | TAG_I64
      | TAG_U8 | TAG_U16 | TAG_U32 | TAG_U64 => Value::I64(0),
        TAG_F32 | TAG_F64 => Value::F64(0.0),
        TAG_CHAR => Value::Char('\0'),
        _ => Value::Null,   // Str, Object, Array, Unknown, 0x??
    }
}
```

`TAG_*` 常量在 `metadata/type_tags.rs`（如已存在则复用；否则新增小模块；Rust 侧目前只有 zbc_reader 内部 `TAG_I64` 等常量）。如需新建模块，**放在 `metadata/` 下而非 `interp/`**，便于跨模块共享。

## Implementation Notes

### 编译器侧（C#）

1. **`ArrayNewInstr` record 加 `IrType ElemType`**（[`IrModule.cs:274`](../../../../src/compiler/z42.IR/IrModule.cs#L274)）：默认值不设，所有构造点显式传值
2. **`FunctionEmitterExprs.VisitArrayCreate`**（[`FunctionEmitterExprs.cs:281`](../../../../src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs#L281)）：
   ```csharp
   var elemIr = _e.ToIrType(ac.ElemType);
   _e.Emit(new ArrayNewInstr(dst, sizeReg, elemIr));
   ```
3. **`ZbcWriter.Instructions.cs`**（line 185）：
   ```csharp
   case ArrayNewInstr i:
       w.Write(Opcodes.ArrayNew);
       w.Write(TypeTagFromIrType(i.Dst.Type));  // dst tag (existing, = Object)
       WriteReg(w, i.Dst);
       WriteReg(w, i.Size);
       w.Write(TypeTagFromIrType(i.ElemType));   // ← new: element tag
       break;
   ```
4. **`ZbcReader.Instructions.cs:205`**：
   ```csharp
   case Opcodes.ArrayNew:
       var size = RU(r.ReadUInt16());
       var elemTag = r.ReadByte();
       return new ArrayNewInstr(d, size, ToIrType(elemTag));
   ```
   `ToIrType(byte)` 已存在（line 261–276 of ZbcReader.Instructions.cs）。
5. **`ZbcWriter.cs` / `ZpkgWriter.cs` — VersionMinor++**，注释一行说明 spec name
6. **`IrVerifier.cs`** 中 ArrayNewInstr 模式匹配若解构字段需补 `_`，否则忽略

### 运行时侧（Rust）

1. **`metadata/bytecode.rs:519`**：
   ```rust
   ArrayNew {
       #[serde(with = "typed_reg_serde")] dst: Reg,
       #[serde(with = "typed_reg_serde")] size: Reg,
       elem_tag: u8,
   },
   ```
2. **`metadata/zbc_reader.rs:860`**：
   ```rust
   OP_ARRAY_NEW => {
       let size = c.read_u16()? as u32;
       let elem_tag = c.read_u8()?;
       Instruction::ArrayNew { dst, size, elem_tag }
   }
   ```
   同步 bump `ZBC_VERSION_MINOR` (32 → ...) 和 `ZPKG_VERSION_MINOR`。
3. **`metadata/types.rs`** — 加 `default_value_for_tag(u8)`，或在 `zbc_reader.rs` 现有 `TAG_*` 常量公开后 import 使用
4. **`interp/exec_array.rs:12`**：
   ```rust
   pub(super) fn array_new(ctx: &VmContext, frame: &mut Frame, dst: u32, size: u32, elem_tag: u8) -> Result<()> {
       let n = to_usize(frame.get(size)?, "ArrayNew size")?;
       let default = default_value_for_tag(elem_tag);
       frame.set(dst, ctx.heap().alloc_array(vec![default; n]));
       Ok(())
   }
   ```
5. **`interp/exec_instr.rs`** — match arm `Instruction::ArrayNew` 传 `elem_tag`
6. **`interp/mod.rs`** — 同样更新签名
7. **`jit/translate.rs:660`**：
   ```rust
   Instruction::ArrayNew { dst, size, elem_tag } => {
       let d = ri!(*dst); let s = ri!(*size);
       let t = builder.ins().iconst(types::I8, *elem_tag as i64);
       let inst = builder.ins().call(hr_array_new, &[frame_val, ctx_val, d, s, t]);
       ...
   }
   ```
   `hr_array_new` signature 加 `i8` 参数。
8. **`jit/helpers/array.rs:9`**：
   ```rust
   pub unsafe extern "C" fn jit_array_new(
       frame: *mut JitFrame, ctx: *const JitModuleCtx,
       dst: u32, size: u32, elem_tag: u8,
   ) -> u8 {
       ...
       let default = default_value_for_tag(elem_tag);
       (*frame).regs[dst as usize] = vm_ctx_ref(ctx).heap().alloc_array(vec![default; n]);
       0
   }
   ```
9. **`jit/translate.rs` `jit_array_new` signature 注册处** — 函数签名加 `I8`

### Workaround 清理

1. **`src/libraries/z42.core/src/Collections/Dictionary.z42`** — 删除：
   - 构造函数中 `while (i < 8) { this.occupied[i] = false; i = i + 1; }`（line 19–22）
   - `Grow()` 中 `while (j < newCap) { newOccupied[j] = false; j = j + 1; }`（line 146）
   - 配套注释 `// VM 默认初始化 bool[] 为 Null；显式置 false`

2. **`src/libraries/z42.crypto/src/Sha256.z42`** — 删除：
   - `_zeroBytes` static helper（line 88–96）
   - 配套注释 `// **gotcha**: new byte[N] in z42 leaves elements as null ...`（line 12–15）
   - 所有调用点 `_zeroBytes(n) → new byte[n]`：`Hash`（line 30）、`HashStringHex` 空串分支（line 52）、`_pad` 内部（line 104）

3. 其他 stdlib **不主动巡查**；遇到 `// VM .* null` 注释或显式 zero-loop pattern 由后续单独 spec 清理（避免 Scope 蔓延）

## Testing Strategy

### 单元测试

- **`src/runtime/src/interp/exec_array_tests.rs`**（新文件，遵循
  [runtime-rust.md](../../../../.claude/rules/runtime-rust.md) 测试拆分规则）：
  - byte / int / long / short / sbyte / ubyte → I64(0)
  - bool → Bool(false)
  - double / float → F64(0.0)
  - char → Char('\0')
  - object / unknown → Null
  - len = 0 → 空 vec
  - 大 len（1000）每个 slot 都正确

### Golden tests

- 新建 `src/tests/array/default-init/`：每个原语类型一个最小 z42 source；
  expected_output 验证读取未写过的 slot 不 crash 且值正确
- 现有 zbc-format 6 个 fixture + zpkg-format 4 个 fixture 全部 regen，FormatGoldenTests
  通过

### 回归测试

- `dotnet test --filter "Z42.Tests"` 全绿，含 `IncrementalBuildIntegrationTests` /
  `Zbc*` / `Zpkg*`
- `./scripts/test-vm.sh` 全绿（interp + JIT）
- `./scripts/test-cross-zpkg.sh` 全绿
- `./scripts/test-stdlib.sh` 全绿；Sha256 NIST vectors 5 / 6 通过（"" 边界 case
  仍有独立 Utf8 bug，参见已记录的 follow-up）

### 演示

- `examples/` 加一个最小 `array_default_init.z42`：`byte[] arr = new byte[4]; print(arr[0]);` → 输出 `0`，无 crash

## Compatibility

- pre-1.0 strict-pin：旧 minor 的 zbc 不被新 reader 接受 → 重新编译 stdlib + 测试 zbc
- 无 fallback 路径，无 deprecated alias
- 升级路径：跑一次 `./scripts/regen-golden-tests.sh` + `./src/tests/zbc-format/generate-fixtures.sh` + `./src/tests/zpkg-format/generate-fixtures.sh`

## Out of Scope / Deferred

- **多维数组**（`new int[N][M]`）：z42 当前一维数组，多维待 L3
- **Memset intrinsic**：当前 `vec![v; n]` 已足够；GC 性能优化是独立工作
- **泛型 type-arg 推导默认值**：`new T[N]` where `T` 是泛型参数 → 走 `DefaultOf` opcode 路径已有的 type-arg 解析，本 spec 不动；现状是 `T` 在 codegen 时若已 monomorphize 则 `ElemType` 落到具体原语 tag，否则落到 `Object` → `Null`，与"用户类型默认 null"一致
- **Utf8.GetBytes("") 空串边界 bug**（`Sha256.HashStringHex("")` 触发的另一个独立问题）：留作独立 spec
