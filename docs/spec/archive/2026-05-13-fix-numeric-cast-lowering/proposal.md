# Proposal: Fix Numeric Cast Lowering

## Why

z42 当前 `(long)d` 等显式数值类型转换在 IR / VM 层是 **no-op**：
1. Parser 创建 `CastExpr(operand, targetType)`
2. TypeChecker 创建 `BoundCast(operand, castType, span)` — **无任何合法性校验**
3. Codegen `VisitCast` 直接 `return EmitExpr(cast.Operand)` — **不发射任何 IR 转换指令**
4. VM 寄存器仍持原 `Value::F64`，被声明为 i64 的目标读取时类型不匹配 → VM error

实施 z42.time 时直接撞到：`(long)(2.5 * 1e9)` crash。被迫把 `TimeSpan.FromSeconds(double)` 改成 `FromSeconds(long)`，丧失亚秒级精度的友好 API。

任何未来 stdlib 包（z42.encoding 的字节运算、z42.json 数字 parse、z42.math 数值转换）都会复制相同 workaround。**这是 foundational compiler bug，必须在更多 stdlib 之前修。**

## What Changes

### Compiler
1. **TypeChecker `BoundCast` 合法性校验**：发射 `E0501` 当 cast 不合法
2. **Codegen `VisitCast` 真实发射**：当 `fromIr != toIr` 时 emit `ConvertInstr`；相等时保持 no-op
3. **新增 IR 指令 `ConvertInstr(Dst, Src)`**：目标类型由 `Dst.Type` 携带，源类型由 VM 运行时从 `Value` variant 决定
4. **新增 opcode 字节**：`Convert = 0xB1`（落在 0xB0-0xBF generic runtime 段）
5. **新增诊断码 `E0501 IllegalCast`**

### Runtime / VM
1. **新增 `Instruction::Convert { dst, src }`**：interp 走 `exec_value::convert` 新函数，dispatch on (源 Value variant, 目标 IrType tag)
2. **JIT 加 `hr_convert` helper**：与 `hr_as_cast` 同款 Rust helper 调用模式
3. **zbc reader/writer 同步**

### 格式版本
- zbc `0.9` → `0.10`（新 opcode 字节；旧 VM 无法解码 `0xB1`，新 VM 不读旧格式 — pre-1.0 无 compat 路径，旧 zbc 用 `regen-golden-tests.sh` 重生）

## Cast 矩阵

### 合法（emit Convert）
| from → to | 语义 | VM 实现 |
|---|---|---|
| f64 → i8/i16/i32/i64 | 向零截断 | `Value::I64(f as i64)` 然后按 narrowing 规则截位 |
| f32 → i8/i16/i32/i64 | 同上 | 同上 |
| i64 → i8/i16/i32 | 截低位 + 符号扩展（C# 一致） | `Value::I64((x as i32) as i64)` 等 |
| i64 → u8/u16/u32 | 截低位 + 零扩展 | `Value::I64((x as u32) as i64)` 等 |
| i*/u* → f64/f32 | 整数→浮点 | `Value::F64(x as f64)` |
| char → i32/i64 | Unicode scalar 值 | `Value::I64(c as u32 as i64)` |
| i32/i64 → char | 验证有效 Unicode scalar；非法触发 VM error | `char::from_u32(x as u32).ok_or_err()` |

### 非法（TypeCheck E0501）
| from / to | 提示 |
|---|---|
| bool ↔ 任何数字类型 | `cannot cast bool ↔ numeric; use explicit conditional` |
| string ↔ 任何数字类型 | `cannot cast string ↔ numeric; use Parse / ToString` |
| 任何对象类型 ↔ 数字类型 | `cannot cast object ↔ numeric` |
| Z42UnknownType（fallback） | 允许（兼容现有 `(long)object` 模式 — VM 动态解 Value variant） |

### 身份 cast（no-op，与现状一致）
- `fromIr == toIr` → 不发射 ConvertInstr，直接返回操作数寄存器

## Scope（允许改动的文件）

**MODIFY**
| 文件 | 说明 |
|---|---|
| `src/compiler/z42.IR/IrModule.cs` | 加 `ConvertInstr` record |
| `src/compiler/z42.IR/IrVerifier.cs` | 加 ConvertInstr 处理（dst/src 寄存器列表）|
| `src/compiler/z42.IR/BinaryFormat/Opcodes.cs` | 加 `public const byte Convert = 0xB1;` |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs` | 加 case：写 opcode + tag + dst_reg + src_reg |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.Instructions.cs` | 对称解码 |
| `src/compiler/z42.IR/BinaryFormat/ZasmWriter.cs` | 文本渲染 |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` | `CastExpr` 分支调用 `CheckCastLegal` |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` 或新文件 | 加 `CheckCastLegal(from, to, span)` 校验函数 |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` | `VisitCast` 真实发射 `ConvertInstr` |
| `src/compiler/z42.Core/Diagnostics/DiagnosticCodes.cs` | 加 `E0501 IllegalCast` |
| `src/compiler/z42.Core/Diagnostics/DiagnosticCatalog.cs` | E0501 条目 |
| `src/runtime/src/metadata/bytecode.rs` | 加 `Instruction::Convert { dst, src }` variant |
| `src/runtime/src/metadata/zbc_reader.rs` | 解码 0xB1 |
| `src/runtime/src/metadata/zbc_writer.rs`（若存在）| 同步 |
| `src/runtime/src/interp/exec_instr.rs` | dispatch 到 `exec_value::convert` |
| `src/runtime/src/interp/exec_value.rs` | 新 `convert(frame, dst, src, to_tag)` 函数 |
| `src/runtime/src/jit/translate.rs` | 加 Instruction::Convert 处理 + `hr_convert` 调用 + `dst` 收集 |
| `src/runtime/src/jit/helpers.rs` | 加 `extern "C" fn hr_convert(...)` |
| `src/runtime/src/metadata/formats.rs` | `ZBC_VERSION = [0, 10]` |

**NEW**
| 文件 | 说明 |
|---|---|
| `src/compiler/z42.Tests/NumericCastTests.cs` | 单元测试：合法 cast 各组合 + E0501 各非法组合 |
| `src/tests/casts/float_to_int/` | golden test：`(long)3.7 == 3` |
| `src/tests/casts/int_widen_narrow/` | golden test：narrowing + widening |
| `src/tests/casts/char_int/` | golden test：char ↔ int 往返 |

**MODIFY (docs)**
| 文件 | 说明 |
|---|---|
| `docs/design/runtime/ir.md` | 加 `Convert` 指令文档（操作数 / 语义 / 伪代码示例）|
| `docs/design/language/language-overview.md` | 类型 cast 段：补"显式数值 cast 合法矩阵 + 拒绝矩阵" |
| `docs/spec/changes/add-z42-time/tasks.md` | 备注："numeric cast lowering 已落地，可恢复 FromSeconds(double) API" |

**只读引用**
- `src/runtime/src/interp/ops.rs` — 理解 `int_binop` 现有自动 widening
- `src/runtime/src/metadata/types.rs` — Value enum
- `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs:241` — 现 no-op VisitCast

## Out of Scope（不在 v0）

- **decimal / BigInt 类型**：z42 当前无；不引入
- **`checked` / `unchecked` 上下文**：C# 风格的算术溢出控制；统一走 "unchecked"（与现有 `int_binop` wrapping_* 一致）
- **隐式数值 cast**：z42 当前不做隐式 widening；本 spec 不引入。混合运算靠 VM `int_binop` 自动 promote 在 runtime 层覆盖
- **string ↔ num 自动 cast**：用户走 `int.Parse(s)` / `Convert.ToInt32(s)` / `x.ToString()`
- **以下复杂场景**：跨包导入的 cast 行为（ABI 已稳定，单纯 IR/VM 改动不触及）

均记入 `docs/design/runtime/ir.md` Convert 段后的 Deferred 注释。

## Open Questions

无（设计决策已在 Cast 矩阵中明确）。
