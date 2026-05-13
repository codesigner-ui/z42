# Design: Numeric Cast Lowering

## Architecture

```
Source                    AST           Bound           IR              VM Value
──────────────────────────────────────────────────────────────────────────────────
(long)d                   CastExpr  →   BoundCast   →   ConvertInstr →  Value::I64
                                          │                 │
                                          ▼                 ▼
                                     TypeChecker        VM exec_value::convert
                                     CheckCastLegal     dispatch on (Value, tag)
                                          │
                                          ▼ (if illegal)
                                     E0501 IllegalCast
```

## Decisions

### Decision 1: 新 IR 指令 `Convert`，不复用已有 `AsCast`

**问题**：是否在现有 `AsCast`（对象类型 cast）上扩展支持数值，还是新指令？

**选项**：
- A — 扩展 AsCast：opcode `0x72` 复用；class_name 字段空时按数值处理。**优**：少一个 opcode。**缺**：AsCast 携带 class_name 字符串（数值 cast 浪费），且 VM dispatch 两路分叉混在一处，可读性差
- **B（选）— 新 `Convert` 指令**：opcode `0xB1`。**优**：语义清晰（dst.Type tag 是唯一目标信息）、无字符串 payload、JIT helper 单一职责

**决定**：B。

### Decision 2: 不在指令里编码 from_tag

**问题**：VM 需要"从某 tag 到某 tag"做 dispatch；是把 from_tag 写进指令字节流，还是 VM 运行时从 `Value` variant 读取？

**选项**：
- A — 编码 from_tag：opcode + dst_tag + from_tag + dst_reg + src_reg。**优**：dispatch 表查找快（先 from 后 to 二维表）。**缺**：编码膨胀；与现有"VM 运行时 reads Value variant for type"模式不一致
- **B（选）— 不编码**：opcode + dst_tag + dst_reg + src_reg。VM 用 `match (frame.get(src), to_tag)` dispatch

**决定**：B。VM 已是动态类型 Value 模型；from_tag 在编码时就重复信息。

### Decision 3: f64 → int 用 Rust `as i64` 语义（saturating + NaN→0）

**问题**：浮点超界 / NaN 该如何处理？

**选项**：
- A — 抛 InvalidCastException：与 C# `checked` 一致
- B — 截断 + 溢出未定义（C `(long)d`）
- **C（选）— Rust `as i64` saturating**：NaN → 0；+Inf → i64::MAX；-Inf → i64::MIN；超界 → saturate

**决定**：C。理由：
- VM 已用 Rust `as` 语义运行其它 wrapping_* 算术
- C# `unchecked` 行为是实现定义；不抛错与现有 z42 "unchecked by default" 一致
- 抛 InvalidCastException 需要额外 try/catch 周围支持，复杂度收益不匹配

记入 `language-overview.md` 类型 cast 段："数值 cast saturating + NaN → 0；用 `int.TryParse` / 用户自行校验做严格转换"。

### Decision 4: int → char 严格校验（surrogate / 超界抛错）

**问题**：char 是 Rust `char`（仅有效 Unicode scalar）；i32 → char 必须校验。

**实施**：使用 `char::from_u32(x as u32)`：
- 有效 scalar → `Value::Char(c)`
- surrogate（U+D800 ~ U+DFFF）或 > U+10FFFF → 返回 None → VM 抛 anyhow error 含 InvalidCastException 信息

短期：`bail!()` 现有 anyhow 错误；用户用 `try {} catch (Exception)` 兜底。未来：mapping 到 `Std.InvalidCastException` 类（exception 体系完整时）。

### Decision 5: TypeChecker 校验严格度

**问题**：什么场景算"非法 cast" 在编译期拒？

**矩阵确认**：
- ✅ 允许：数值 ↔ 数值；char ↔ 数值；Z42UnknownType ↔ 任何（fallback）
- ❌ 拒绝（E0501）：bool ↔ 数值/char；string ↔ 数值/char；class/interface ↔ 数值/char（class cast 已走 AsCast 路径，不到这里）

**实施位置**：`TypeChecker.Exprs.cs` 的 `CastExpr` 分支，在 `return new BoundCast(...)` 之前。

**为何 Z42UnknownType 放行**：现有 stdlib 大量用 `(long)object` / `(int)(long)object` —— `object` 在 z42 是 `Z42PrimType("object")` 还是 Z42UnknownType？需要 verify。若是 prim object，校验时显式 allow `object → 数值`（VM 动态解 Value variant）。

### Decision 6: 身份 cast 保留 no-op 优化

`fromIr == toIr` → 不发射 ConvertInstr，直接返回源寄存器。理由：
- 用户在泛型代码 / 模板里写的"防御性 cast"（如 `(T)x` where T 实例化等于 x 类型）应零开销
- 不发射 IR 指令既减少 zbc 体积，也保持现有 IR dump 简洁

实施：`VisitCast` 先比较 `fromIr` 与 `toIr`，相等直接 `return src`。

### Decision 7: zbc 版本 bump `0.9 → 0.10`

**问题**：新 opcode 不向后兼容；版本应该 minor 还是 major？

**选项**：
- A — patch `0.9 → 0.9.1`：z42 当前 zbc 没用 patch 位
- **B（选）— minor `0.9 → 0.10`**：与现有 `[u16; 2] = [major, minor]` 风格一致
- C — major `0.9 → 1.0`：留给真正稳定时机

**决定**：B。Pre-1.0 仍处于"任何 minor 都可破坏 compat"阶段。

### Decision 8: JIT 走 Rust helper 调用而非 inline lowering

**问题**：JIT 直接 lower `Convert` 到 Cranelift `fcvt_to_sint` / `sextend` / `ireduce` 指令？还是调 Rust helper？

**选项**：
- A — inline Cranelift 指令：**优**：JIT 性能好；**缺**：每个 (from, to) 对都要 Cranelift codegen；NaN/超界 saturating 在 Cranelift 表达复杂
- **B（选）— Rust helper `hr_convert`**：与 `hr_as_cast` / `hr_is_instance` 同款，单一 extern "C" 入口；**缺**：每次 cast 一次函数调用

**决定**：B for v0。JIT 性能优化留独立 spec（当 cast 出现在 hot path 时再 inline）。一致性 + 实现简单度优先。

## Implementation Notes

### TypeChecker: CheckCastLegal

```csharp
// 新加到 TypeChecker.Exprs.cs（CastExpr 分支前置 helper）
private bool CheckCastLegal(Z42Type from, Z42Type to, Span span)
{
    // 身份允许
    if (from.Equals(to)) return true;

    // Unknown 放行（兼容 object 模式）
    if (from is Z42UnknownType || to is Z42UnknownType) return true;
    if (from is Z42PrimType { Name: "object" } || to is Z42PrimType { Name: "object" }) return true;

    bool fromNum  = IsNumericOrChar(from);
    bool toNum    = IsNumericOrChar(to);
    bool fromBool = from is Z42PrimType { Name: "bool" };
    bool toBool   = to   is Z42PrimType { Name: "bool" };
    bool fromStr  = from is Z42PrimType { Name: "string" };
    bool toStr    = to   is Z42PrimType { Name: "string" };

    // 数值 ↔ 数值（含 char）
    if (fromNum && toNum) return true;

    // 非法组合
    if (fromBool || toBool)
    {
        _diags.Error(DiagnosticCodes.IllegalCast,
            $"cannot cast `{from}` ↔ `{to}`; use explicit conditional", span);
        return false;
    }
    if (fromStr || toStr)
    {
        _diags.Error(DiagnosticCodes.IllegalCast,
            $"cannot cast `{from}` ↔ `{to}`; use Parse / ToString", span);
        return false;
    }
    // 其他（class、interface 等）已经走 AsCast / IsInstance 路径，不进这里
    // fallthrough：保留向后兼容（保守 allow，避免破坏未列举的 generic 场景）
    return true;
}

private static bool IsNumericOrChar(Z42Type t) => t is Z42PrimType pt
    && pt.Name is "int" or "long" or "short" or "byte" or "sbyte"
                or "uint" or "ulong" or "ushort"
                or "double" or "float" or "i8" or "i16" or "i32" or "i64"
                or "u8" or "u16" or "u32" or "u64" or "f32" or "f64"
                or "char";
```

`CastExpr` 分支：
```csharp
case CastExpr cast:
{
    var operand  = BindExpr(cast.Operand, env);
    var castType = ResolveType(cast.TargetType);
    CheckCastLegal(operand.Type, castType, cast.Span);
    return new BoundCast(operand, castType, cast.Span);
}
```

### Codegen: VisitCast 真实发射

```csharp
protected override TypedReg VisitCast(BoundCast cast)
{
    var src = _e.EmitExpr(cast.Operand);
    var fromIr = ToIrType(cast.Operand.Type);
    var toIr   = ToIrType(cast.Type);
    if (fromIr == toIr) return src;  // 身份 cast，no-op
    var dst = _e.Alloc(toIr);
    _e.Emit(new ConvertInstr(dst, src));
    return dst;
}
```

### VM: exec_value::convert

```rust
use crate::metadata::TypeTag;  // or IrTypeTag — name 待 check

pub(super) fn convert(frame: &mut Frame, dst: u32, src: u32, to_tag: u8) -> Result<()> {
    let v = frame.get(src)?.clone();
    let result = match v {
        Value::F64(f)  => convert_from_f64(f, to_tag)?,
        Value::I64(x)  => convert_from_i64(x, to_tag)?,
        Value::Char(c) => convert_from_char(c, to_tag)?,
        other => bail!("InvalidCastException: cannot convert {:?} to tag {:#x}", other, to_tag),
    };
    frame.set(dst, result);
    Ok(())
}

fn convert_from_f64(f: f64, to_tag: u8) -> Result<Value> {
    match to_tag {
        T_F32 | T_F64 => Ok(Value::F64(f)),  // 身份 / f32 在 VM 中也是 F64 存
        T_I8  => Ok(Value::I64((f as i8) as i64)),
        T_I16 => Ok(Value::I64((f as i16) as i64)),
        T_I32 => Ok(Value::I64((f as i32) as i64)),
        T_I64 => Ok(Value::I64(f as i64)),
        T_U8  => Ok(Value::I64((f as u8) as i64)),
        T_U16 => Ok(Value::I64((f as u16) as i64)),
        T_U32 => Ok(Value::I64((f as u32) as i64)),
        T_U64 => Ok(Value::I64(f as i64)),
        T_Char => {
            let u = f as u32;
            char::from_u32(u).map(Value::Char)
                .ok_or_else(|| anyhow!("InvalidCastException: 0x{:X} not a valid Unicode scalar", u))
        }
        _ => bail!("InvalidCastException: cannot convert f64 to tag {:#x}", to_tag),
    }
}

fn convert_from_i64(x: i64, to_tag: u8) -> Result<Value> {
    match to_tag {
        T_I8  => Ok(Value::I64((x as i8) as i64)),
        T_I16 => Ok(Value::I64((x as i16) as i64)),
        T_I32 => Ok(Value::I64((x as i32) as i64)),
        T_I64 => Ok(Value::I64(x)),
        T_U8  => Ok(Value::I64((x as u8) as i64)),
        T_U16 => Ok(Value::I64((x as u16) as i64)),
        T_U32 => Ok(Value::I64((x as u32) as i64)),
        T_U64 => Ok(Value::I64(x)),
        T_F32 | T_F64 => Ok(Value::F64(x as f64)),
        T_Char => {
            let u = x as u32;
            char::from_u32(u).map(Value::Char)
                .ok_or_else(|| anyhow!("InvalidCastException: 0x{:X} not a valid Unicode scalar", u))
        }
        _ => bail!("InvalidCastException: cannot convert i64 to tag {:#x}", to_tag),
    }
}

fn convert_from_char(c: char, to_tag: u8) -> Result<Value> {
    let u = c as u32;
    match to_tag {
        T_I8  => Ok(Value::I64((u as i8) as i64)),
        T_I16 => Ok(Value::I64((u as i16) as i64)),
        T_I32 => Ok(Value::I64(u as i32 as i64)),
        T_I64 => Ok(Value::I64(u as i64)),
        T_U8  => Ok(Value::I64((u as u8) as i64)),
        T_U16 => Ok(Value::I64((u as u16) as i64)),
        T_U32 => Ok(Value::I64(u as i64)),
        T_U64 => Ok(Value::I64(u as i64)),
        T_Char => Ok(Value::Char(c)),  // 身份
        _ => bail!("InvalidCastException: cannot convert char to tag {:#x}", to_tag),
    }
}
```

### JIT helper

```rust
// jit/helpers.rs
#[unsafe(no_mangle)]
pub extern "C" fn hr_convert(frame: *mut Frame, ctx: *const VmContext, dst: u32, src: u32, to_tag: u8) {
    unsafe {
        let frame = &mut *frame;
        if let Err(e) = crate::interp::exec_value::convert(frame, dst, src, to_tag) {
            (*(ctx as *mut VmContext)).set_pending_error(e);
        }
    }
}
```

translate.rs 分支：
```rust
Instruction::Convert { dst, src } => {
    let d = ri!(*dst);
    let s = ri!(*src);
    let to_tag = builder.ins().iconst(types::I8, dst.type_tag() as i64);
    builder.ins().call(hr_convert, &[frame_val, ctx_val, d, s, to_tag]);
}
```

### Diagnostic codes

```csharp
// DiagnosticCodes.cs
public const string IllegalCast = "E0501";

// DiagnosticCatalog.cs
{ "E0501", "Illegal type cast — only numeric ↔ numeric / char ↔ numeric / object are supported" },
```

## Testing Strategy

详见 spec.md "Testing Strategy" 段。重点：
1. C# 单元测试覆盖 cast 合法 / 非法矩阵
2. VM golden 在 interp + JIT 双 mode 覆盖语义（NaN / 超界 / surrogate 等边界）
3. Rust 单元测试覆盖 `convert` dispatch 表
4. 现有 1233 C# + 320 VM golden + cross-zpkg + stdlib dogfood 零回归
