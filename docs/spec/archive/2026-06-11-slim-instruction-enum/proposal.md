# Proposal: Slim the `Instruction` enum (~120 B → ≤ 32 B)

> 状态：DRAFT 待审（阶段 6.5 前）

## Why

`metadata::bytecode::Instruction` 是 SSA 指令的内存表示，VM 把每个 `Function`
的 body 存为 `Box<[Instruction]>` 并在 interp / JIT 热循环里顺序迭代。enum 的
size = **最大变体**：`CallNative { dst, module: String, type_name: String,
symbol: String, args: Box<[Reg]> }` 带 **3 个 `String`（72 B）+ `Box`（16 B）**，
把整个 enum 撑到 ~120 B。

但热路径上的算术 / 常量 / copy 变体（`Add` / `ConstI64` / `Copy`，3-4 个 `Reg`
≈ 16 B）每条都得**付 120 B**——其中 ~100 B 是纯 padding。大函数 N 条指令 ×
120 B → cache line 利用率差，interp dispatch 顺序读时多吃 cache miss。

review.md **E2.P4**（最终 P0-P5 表，P1/data）：Instruction ~120 B → ≤ 32 B。
把**冷变体**（带 `String` / 多字段的，FFI / 对象 / 字段名相关）的 payload 装箱
（`Variant(Box<VariantInsn>)`），enum 缩到最大**小变体**（~16 B）。热算术变体
保持 inline（不引入间接）。

## What Changes

- 给每个 inline-size > 32 B 的变体引入一个 boxed payload struct（如 `CallInsn`
  / `CallNativeInsn` / `ObjNewInsn` / `VCallInsn` / `FieldInsn` 等），变体改为
  `Call { #[serde(flatten)] data: Box<CallInsn> }`，字段及其 `typed_reg_serde`
  属性移入 payload struct（`#[serde(flatten)]` 保持 JSON wire format `{op, …}`
  不变）。
- 热小变体（`Add`/`Sub`/.../`Const*`/`Copy`/`ArrayGet/Set/Len`/`LoadLocalAddr`
  等纯-Reg/小标量）**不动**。
- 更新所有 match-site：`zbc_reader.rs` 构造端 + `interp/exec_instr.rs` +
  `jit/translate.rs` + `metadata/{loader,resolver,merge}.rs` 的被装箱变体 arm，
  由 `Instruction::Call { dst, func, args }` → `Instruction::Call { data }` 后
  `data.dst` / `data.func` / `data.args`（机械改写）。
- 加一个 `#[test]` 静态断言 `size_of::<Instruction>() <= 32`，防回归。

**关键不变量**：
- **无 zbc 格式 bump**：zbc wire format 由 `ZbcWriter.cs` / `zbc_reader.rs` 独立
  定义；本变更只改 Rust 内存布局，`zbc_reader` 从**同样的二进制字节**构造 boxed
  变体 → 字节不变、reader minor 不变、fixture 不 regen、z42c writer 不动。
- **JSON wire format 不变**：`#[serde(flatten)] Box<XxxInsn>` 让序列化输出仍是
  `{"op":"call","dst":…,"func":…,"args":…}`。
- **行为完全不变**：纯数据布局重构，VM 语义 / 执行结果 0 变化（vm goldens 全绿）。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | 装箱冷变体 + 新 payload struct；末尾挂 `bytecode_tests` |
| `src/runtime/src/metadata/bytecode_tests.rs` | NEW | size 断言 + serde round-trip 单测（slim-instruction-enum 实施期加入） |
| `src/runtime/src/metadata/mod.rs` | MODIFY | re-export 15 个 `<Variant>Insn` payload struct（实施期加入） |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | 被装箱变体构造改 `Instruction::Xxx(Box::new(XxxInsn{…}))` |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | 被装箱变体 match arm 解构 `let XxxInsn{…} = &**insn;` / 字段访问 |
| `src/runtime/src/jit/translate.rs` | MODIFY | 同上（JIT 翻译 match + dst-extractor） |
| `src/runtime/src/metadata/loader.rs` | MODIFY | 引用 `Call { func, .. }` 等被装箱变体处改 `insn.func` |
| `src/runtime/src/metadata/resolver.rs` | MODIFY | 同上（StaticGet\|StaticSet or-pattern 拆两 arm） |
| `src/runtime/src/metadata/merge.rs` | MODIFY | `LoadFnCached` slot 重映射改 `insn.slot_id` |
| `src/runtime/tests/native_interop_e2e.rs` | MODIFY | 构造端 `CallNative`/`FieldGet` 同步装箱（实施期加入） |
| `src/runtime/tests/native_opcode_trap.rs` | MODIFY | 同上（实施期加入） |
| `src/runtime/tests/native_pin_e2e.rs` | MODIFY | 同上（实施期加入） |
| `docs/design/runtime/ir.md` | MODIFY | 记 Instruction 内存表示的 hot/cold 装箱策略 + Deferred（不改 opcode 语义）|
| `docs/roadmap.md` | MODIFY | Deferred Backlog Index 加 slim-terminator-future / slim-instruction-stringid 索引行（实施期加入） |

**只读引用**：
- `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` — 确认 zbc 二进制格式不受影响
- `src/runtime/src/metadata/bytecode.rs` 的 `typed_reg_serde` — 理解字段 serde

## Out of Scope

- **不改 zbc / zpkg 格式**（无 version bump）——若实施中发现必须改格式，停下重审。
- **不改 opcode 语义 / 数量**——纯内存布局。
- **不碰热小变体**（Add/Const/Copy 等保持 inline）。
- StringId 化（E2.P3，把 `String` → `StringId(u32)`）是**独立后续**——本变更只
  装箱，不改 String 表示。两者正交，可叠加。

## Open Questions（已解，2026-06-11）

- [x] JSON serde 是否仍被任何路径用？确认无 `from_str::<Instruction>` 消费端。
  仍保守保 wire format，但用 internally-tagged **newtype**（serde 自动摊平）替代
  `#[serde(flatten)]`——更干净且 observable 输出相同（见 design Decision 2 精化）。
- [x] 装箱阈值：选「凡带 `String` 即装箱」（15 个），design Decision 1 = 选项 A。
  实测 enum 因此降到 32 B（由无 String 但带 `Box<[Reg]>` 的 `CallIndirect` /
  `CallNativeVtable` 决定），≤32 目标达成，二者留 inline。
