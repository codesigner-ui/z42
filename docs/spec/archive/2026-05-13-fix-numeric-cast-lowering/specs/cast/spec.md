# Spec: Numeric Cast Lowering

## ADDED Requirements

### Requirement: Float → Integer cast 真实截断

#### Scenario: 正浮点 → long 向零截断
- **WHEN** `double d = 3.7; long n = (long)d;`
- **THEN** `n == 3`

#### Scenario: 负浮点 → long 向零截断
- **WHEN** `double d = -3.7; long n = (long)d;`
- **THEN** `n == -3`

#### Scenario: 浮点表达式 → long
- **WHEN** `long n = (long)(2.5 * 1000000000.0);`
- **THEN** `n == 2500000000`

#### Scenario: 浮点 → int 截断
- **WHEN** `double d = 3.7; int n = (int)d;`
- **THEN** `n == 3`

#### Scenario: NaN → integer
- **WHEN** `double d = 0.0 / 0.0; long n = (long)d;`
- **THEN** `n == 0`（Rust `as i64` 对 NaN 返回 0；保持平台行为）

#### Scenario: 超大正浮点 → long
- **WHEN** `double d = 1e30; long n = (long)d;`
- **THEN** `n == i64::MAX` 即 `9223372036854775807`（Rust `as i64` saturating；与 C# 实现定义但常见行为一致）

---

### Requirement: Integer → Integer narrowing 截低位 + 符号扩展

#### Scenario: long → int 普通范围
- **WHEN** `long x = 1000; int n = (int)x;`
- **THEN** `n == 1000`

#### Scenario: long → int 高位丢失（C# 一致）
- **WHEN** `long x = 100000000000L; int n = (int)x;`
- **THEN** `n == 1215752192`（`100_000_000_000 as i32` 的位模式）

#### Scenario: long → short 截断
- **WHEN** `long x = 70000L; short n = (short)x;`
- **THEN** `n == 4464`（`70000 as i16`）

#### Scenario: long → byte (u8) 截断
- **WHEN** `long x = 300L; byte n = (byte)x;`
- **THEN** `n == 44`

---

### Requirement: Integer → Float 扩展

#### Scenario: int → double
- **WHEN** `int x = 5; double d = (double)x;`
- **THEN** `d == 5.0`

#### Scenario: 大 long → double（精度可能损失，仍合法）
- **WHEN** `long x = 9007199254740993L; double d = (double)x;`
- **THEN** `d == 9007199254740992.0`（f64 仅 53-bit 尾数；超界损失精度但不抛错）

---

### Requirement: Char ↔ Integer 转换

#### Scenario: char → int 给 Unicode scalar
- **WHEN** `char c = 'A'; int n = (int)c;`
- **THEN** `n == 65`

#### Scenario: int → char 普通范围
- **WHEN** `int n = 65; char c = (char)n;`
- **THEN** `c == 'A'`

#### Scenario: int → char surrogate 范围抛错
- **WHEN** `int n = 0xD800; char c = (char)n;`
- **THEN** VM 抛 `Std.InvalidCastException` 含信息 "0xD800 not a valid Unicode scalar"

#### Scenario: int → char 超界（> U+10FFFF）抛错
- **WHEN** `int n = 0x110000; char c = (char)n;`
- **THEN** VM 抛 `Std.InvalidCastException`

---

### Requirement: 身份 cast 保持 no-op

#### Scenario: int → int
- **WHEN** `int x = 5; int y = (int)x;`
- **THEN** Codegen 不发射 `ConvertInstr`（直接返回源寄存器；IR 文本 dump 应无 `convert` 行）

#### Scenario: double → double
- **WHEN** `double d = 1.5; double e = (double)d;`
- **THEN** 同上

---

### Requirement: 非法 cast 触发 E0501

#### Scenario: bool → int
- **WHEN** `bool b = true; int n = (int)b;`
- **THEN** Diagnostic `E0501 IllegalCast` 含信息 `cannot cast bool ↔ numeric; use explicit conditional`

#### Scenario: string → int
- **WHEN** `string s = "5"; int n = (int)s;`
- **THEN** Diagnostic `E0501` 含信息 `cannot cast string ↔ numeric; use Parse / ToString`

#### Scenario: int → bool
- **WHEN** `int n = 0; bool b = (bool)n;`
- **THEN** Diagnostic `E0501`

#### Scenario: int → string
- **WHEN** `int n = 5; string s = (string)n;`
- **THEN** Diagnostic `E0501`

---

### Requirement: Unknown 源类型 fallback（向后兼容现有 `(long)object` 用法）

#### Scenario: Unknown → 数值类型（现有 stdlib pattern）
- **WHEN** `object o = ...; long x = (long)o;`（如 `(long)raw[0]` 数组下标）
- **THEN** TypeCheck 通过；Codegen 发射 `ConvertInstr`；VM 按运行时 `Value` variant 解析（若实际为 I64 则身份返回；F64 则真实转换；不匹配则 VM error）

---

## MODIFIED Requirements

### Requirement: BoundCast Codegen 语义

**Before:** `VisitCast(BoundCast cast) => EmitExpr(cast.Operand)` — 纯 no-op，操作数寄存器原样返回。

**After:** `VisitCast` 检查 `fromIr := ToIrType(cast.Operand.Type)` 与 `toIr := ToIrType(cast.Type)`：
- 相等 → 返回源寄存器（保留现有 no-op 优化）
- 不等且属合法对 → 分配新寄存器，发射 `ConvertInstr(dst, src)`，返回 dst
- 不等且属非法对 → 不应到达（TypeChecker 已在 BindExpr CastExpr 分支报 E0501；Codegen 此时仍 emit ConvertInstr 让流水线继续，下游使用未定义但已有诊断）

### Requirement: zbc 版本

**Before:** `ZBC_VERSION = [0, 9]`

**After:** `ZBC_VERSION = [0, 10]`。0.9 zbc 无法被 0.10 VM 读取（旧 magic 通过但 version mismatch 报错）；旧 golden zbc 需 `./scripts/regen-golden-tests.sh` 重生。

---

## IR Mapping

新 opcode：`Convert = 0xB1`（generic runtime 段 0xB0-0xBF 空槽）

```
Convert  dst_type_tag  dst_reg  src_reg
```

- `dst_type_tag` (1 byte) — 目标类型，使用 `TypeTags`（I8/I16/I32/I64/U8/U16/U32/U64/F32/F64/Char）
- `dst_reg` (varint) — 目标寄存器
- `src_reg` (varint) — 源寄存器

不带 from_tag —— VM 运行时从源 `Value` variant 决定。

C# IR record:
```csharp
public sealed record ConvertInstr(TypedReg Dst, TypedReg Src) : IrInstr;
```

Rust Instruction variant:
```rust
Convert {
    #[serde(with = "typed_reg_serde")] dst: Reg,
    #[serde(with = "typed_reg_serde")] src: Reg,
}
```

VM dispatch（伪 Rust）：
```rust
pub(super) fn convert(frame: &mut Frame, dst: u32, src: u32, to_tag: IrTypeTag) -> Result<()> {
    let v = frame.get(src)?.clone();
    let result = match (v, to_tag) {
        (Value::F64(f), TAG_I8..=TAG_I64)  => Value::I64(narrow_int(f as i64, to_tag)),
        (Value::F64(f), TAG_U8..=TAG_U32)  => Value::I64(narrow_int(f as i64, to_tag)),
        (Value::I64(x), TAG_F64 | TAG_F32) => Value::F64(x as f64),
        (Value::I64(x), TAG_I8..=TAG_I64)  => Value::I64(narrow_int(x, to_tag)),
        (Value::I64(x), TAG_U8..=TAG_U32)  => Value::I64(narrow_int(x, to_tag)),
        (Value::Char(c), TAG_I32|TAG_I64)  => Value::I64(c as u32 as i64),
        (Value::I64(x), TAG_Char)          => {
            char::from_u32(x as u32)
                .map(Value::Char)
                .ok_or_else(|| anyhow!("InvalidCastException: 0x{:X} not a valid Unicode scalar", x as u32))?
        },
        // ... 其余 widening / cross-int / 同号 narrowing
        (v, tag) => bail!("InvalidCastException: cannot convert {:?} to tag {}", v, tag),
    };
    frame.set(dst, result);
    Ok(())
}
```

`narrow_int(x: i64, tag: u8) -> i64` 按 tag 截位：
- I8: `(x as i8) as i64`
- I16: `(x as i16) as i64`
- I32: `(x as i32) as i64`
- I64: `x`
- U8: `(x as u8) as i64`
- U16: `(x as u16) as i64`
- U32: `(x as u32) as i64`
- U64: `x`

## Pipeline Steps

- [ ] Lexer — 无变动
- [ ] Parser / AST — 无变动（CastExpr 已存在）
- [ ] TypeChecker — `CastExpr` 分支加 `CheckCastLegal` 调用 + E0501 发射
- [ ] IR Codegen — `VisitCast` 真实发射 `ConvertInstr`
- [ ] zbc writer / reader — 新 opcode 编解码
- [ ] VM interp — `exec_value::convert` dispatch 表
- [ ] VM JIT — `hr_convert` Rust helper + `Instruction::Convert` translate.rs 分支

## Testing Strategy

- **C# 单元测试**：`src/compiler/z42.Tests/NumericCastTests.cs` NEW
  - 合法 cast 矩阵：每个表内组合一个 IR + Bound 断言
  - 非法 cast 矩阵：每个非法对一个 E0501 断言
- **VM golden test**：`src/tests/casts/<name>/` NEW
  - `float_to_int`：`(long)3.7`、`(long)-3.7`、`(long)NaN`、`(long)1e30`
  - `int_widen_narrow`：long→int 高位丢失、long→short、long→byte
  - `char_int`：`(int)'A'`、`(char)65`、`(char)0xD800` 应抛 InvalidCastException
  - 全部 interp + JIT 两 mode
- **Rust 单元测试**：`src/runtime/src/interp/exec_value_tests.rs`（若不存在则新建）
  - `convert` 函数表 dispatch 全覆盖
- **回归**：现有 320 golden + 1233 C# 测试全绿；现有 stdlib 中 `(long)object` / `(int)(long)object` 用法继续工作
