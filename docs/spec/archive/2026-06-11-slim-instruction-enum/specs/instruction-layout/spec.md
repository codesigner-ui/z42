# Spec: Instruction 内存布局瘦身

> 状态：DRAFT 待审。本变更是**行为保持的数据布局重构**——场景以「不变量」+
> 「size 收缩」表达，不引入新 VM 行为。

## ADDED Requirements

### Requirement: Instruction enum size ≤ 32 B

#### Scenario: 静态 size 断言
- **WHEN** 编译 runtime crate 并跑 `size_of::<Instruction>()`
- **THEN** 结果 ≤ 32 B（实施前基线 **实测 96 B**，旧最大变体 `CallNative`；
  实施后 32 B；`instruction_size_is_slim` 单测守门）

#### Scenario: 热小变体保持 inline
- **WHEN** 检视 `Add` / `Sub` / `ConstI64` / `Copy` / `ArrayGet` 等纯-Reg/小标量变体
- **THEN** 它们**不**经 `Box` 间接（payload 直接 inline 在 enum 里），dispatch
  热路径不增一次指针解引用

#### Scenario: 冷变体装箱
- **WHEN** 检视带 `String` 的变体（`Call` / `CallNative` / `ObjNew` / `VCall` /
  `FieldGet` / `StaticGet` / `IsInstance` 等 15 个）
- **THEN** 各自 payload 移入 `<Variant>Insn` struct，变体为 internally-tagged
  newtype `Variant(Box<XxxInsn>)`（serde `tag="op"` 自动摊平内层 struct 字段，
  wire format 不变；见 design Decision 2 精化备注）

## MODIFIED Requirements

### Requirement: zbc wire format 不变

**Before:** `zbc_reader::read_instr` 构造 `Instruction::Call { dst, func, args }`（inline）
**After:** 构造 `Instruction::Call(Box::new(CallInsn { dst, func, args }))`
——**读的二进制字节序列完全相同**

#### Scenario: fixture 无字节漂移
- **WHEN** 跑 `./src/tests/zbc-format/generate-fixtures.sh`
- **THEN** 6 个 fixture 的 `source.zbc` git diff **无 delta**（zbc minor 不 bump）

#### Scenario: zbc/zpkg minor 不变
- **WHEN** 检视 `zbc_reader.rs::ZBC_VERSION_MINOR` / `ZbcWriter.cs::VersionMinor`
- **THEN** 与实施前一致（本变更不进 version-bumping.md 流程）

### Requirement: JSON wire format 不变

**Before:** `serde_json` 序列化 `Call` → `{"op":"call","dst":…,"func":…,"args":…}`
**After:** 经 internally-tagged newtype `Call(Box<CallInsn>)`（serde 自动摊平内层
struct 字段进 tag 对象），输出**完全相同**

#### Scenario: serde round-trip
- **WHEN** 把一条 `Call` 指令 serde→JSON→serde
- **THEN** 往返相等，JSON 文本与实施前逐字符一致（flatten round-trip 单测守门）

### Requirement: VM 执行行为不变

#### Scenario: vm goldens 全绿
- **WHEN** 跑 `z42 xtask.zpkg test vm`（interp + JIT）
- **THEN** 全部 golden 端到端执行结果与实施前**逐字节相同**（0 行为变化）

#### Scenario: 全 runtime 单测不回归
- **WHEN** 跑 `cargo test`（runtime）
- **THEN** 全绿（含 interp/jit/loader/resolver/merge 涉及被装箱变体的路径）

## IR Mapping

无新 opcode；无 zbc opcode/section 变化。仅 Rust 内存表示（hot/cold 装箱）。

## Pipeline Steps

受影响的 pipeline 阶段：
- [ ] Lexer —— 不涉及
- [ ] Parser / AST —— 不涉及
- [ ] TypeChecker —— 不涉及
- [ ] IR Codegen（C# ZbcWriter）—— **不涉及**（zbc 字节不变）
- [x] VM 反序列化（`zbc_reader.rs` 构造端）—— 改 boxed 构造
- [x] VM interp（`interp/exec_instr.rs` match arm）—— 改 `data.field` 解构
- [x] VM JIT（`jit/translate.rs` match arm）—— 同上
- [x] VM metadata（`loader/resolver/merge.rs` 引用被装箱变体处）—— 同上
