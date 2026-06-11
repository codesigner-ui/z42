# Tasks: slim-instruction-enum

> 状态：🟢 已完成 | 创建：2026-06-11 | 完成：2026-06-11 | 类型：vm（数据布局重构，行为保持）
> 子系统锁：runtime（见 ACTIVE.md）。

## 进度概览
- [x] 阶段 1: bytecode.rs 装箱 15 冷变体 + payload struct + size 断言
- [x] 阶段 2: 更新 match-site（zbc_reader / interp / jit / loader/resolver/merge + test files）
- [x] 阶段 3: 验证（size ≤32 + serde round-trip + vm goldens + fixture 无 delta）

## 阶段 1: 装箱（bytecode.rs）
- [x] 1.1 量基线：实测旧最大变体 = **96 B**（`CallNative`，三个 inline `String`）。spec 草稿的 ~120 B 估高，正解 96 B。
- [x] 1.2 为 15 个带 String 变体各建 `<Variant>Insn` struct（原字段 + `typed_reg_serde` 属性搬入）
- [x] 1.3 变体改 newtype `Variant(Box<XxxInsn>)`（**非** `#[serde(flatten)]` —— 见备注，internally-tagged newtype 自动摊平，更干净）
- [x] 1.4 `CallIndirect`/`CallNativeVtable`（无 String 带 Box）实测 inline 即 ≤32 B → **保持 inline**（它们决定 32 B 上限；装箱无收益）
- [x] 1.5 加 `#[test] instruction_size_is_slim`（≤32 B）+ 3 个 serde JSON round-trip 单测（Call / ObjNew type_args / StaticSet）→ `metadata/bytecode_tests.rs`

## 阶段 2: match-site 改写（机械）
- [x] 2.1 `zbc_reader.rs`：被装箱变体构造改 `Variant(Box::new(XxxInsn{…}))`
- [x] 2.2 `interp/exec_instr.rs`：多行 arm 头 `let XxxInsn{…} = &**insn;` 一次性解构（arm 体不动），单行 arm 用 `insn.field` 字段访问
- [x] 2.3 `jit/translate.rs`：dst-extractor `Some(insn.dst)` + body arm 同 2.2
- [x] 2.4 `loader.rs` / `resolver.rs` / `merge.rs`：`Call{func,..}` 等引用改 `insn.func`（resolver 的 StaticGet|StaticSet or-pattern 拆成两 arm，因 box 类型不同不能共享绑定）
- [x] 2.5 test files：`native_interop_e2e.rs` / `native_opcode_trap.rs` / `native_pin_e2e.rs` 构造端 `CallNative`/`FieldGet` 同步装箱

## 阶段 3: 验证
- [x] 3.1 cargo build runtime —— 无编译错误（default + test 目标）
- [x] 3.2 cargo test runtime —— 全绿（含 size 断言 + 3 serde round-trip）
- [x] 3.3 `z42 xtask.zpkg test vm` —— interp + JIT goldens 0 变化（行为不变权威门）
- [x] 3.4 `./src/tests/zbc-format/generate-fixtures.sh` —— git diff **无 delta**（证无格式漂移）
- [x] 3.5 `docs/design/runtime/ir.md` 同步 hot/cold 装箱策略 + Terminator/StringId deferred + roadmap Backlog Index 索引
- [x] 3.6 spec scenarios 逐条覆盖确认

## 备注

- **serde 机制（design Decision 2 精化）**：原 design 选 A=`#[serde(flatten)] data: Box<XxxInsn>`
  保守保 wire format。实施时确认 JSON serde 无消费端后，改用更干净的 **internally-tagged
  newtype `Variant(Box<XxxInsn>)`**：`#[serde(tag="op")]` 下 newtype 变体的内层 struct
  字段被 serde **自动摊平进 tag 对象**，输出与旧 struct 变体逐字符相同（`bytecode_tests`
  round-trip 守门），且避免了 flatten 的 Content-buffering 与字段级 `typed_reg_serde`
  的交互。observable wire format 不变，故等价于 design 的目标。
- **size 实测**：96 B → 32 B（3× 收缩）。32 B 由 `CallIndirect`/`CallNativeVtable`
  （无 String 但带 `Box<[Reg]>`）决定；装箱它们可再降到 ~24 B 但 review.md 目标 ≤32 已达成，
  按 design Decision 1 留 inline。
- **fixture 无字节 delta**（3.4）确认「内存布局 vs zbc 格式解耦」假设成立。
- Terminator（Br/BrCond 带 String label）本变更不动 → ir.md Deferred
  `slim-terminator-future`（per-block 非热数组，收益低）+ roadmap Backlog Index。
- StringId 化（E2.P3）正交后续 → ir.md Deferred `slim-instruction-stringid`。
