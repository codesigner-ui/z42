# Proposal: 实施 `ref` / `out` / `in` 参数修饰符的运行时语义

## Why

前置 spec [`define-ref-out-in-parameters-typecheck`](../../archive/2026-05-05-define-ref-out-in-parameters-typecheck/)（2026-05-05 归档）已完成编译期验证，但留下过渡期不一致：用户写 `Increment(ref c)` 编译期通过、运行时 callee 修改**不传回** caller（codegen 走普通 by-value `Call`）。本 spec 补齐运行时语义——通过 `Value::Ref` + 透明 deref 让 ref/out/in 在运行时按设计语义工作。

设计基础已在前置 spec 的 `design.md` 中决议（Decisions 1/2/3/5/8/9 由 User 审批，本 spec 直接实施无需重新决策）。

## What Changes

- **新增** Rust VM `Value::Ref { kind: RefKind }` variant + `RefKind` 枚举（`Stack { frame_idx, slot }` / `Array { gc_ref, idx }` / `Field { gc_ref, field_name }`）
- **新增** GC mark walker 处理 `Value::Ref`：Stack frame 自然存活；Array/Field 的 `gc_ref` 加入根
- **新增** 跨 frame 索引：`VmContext.frame_state_at(idx)` 返回当前调用栈第 idx 层的 frame.regs Vec 指针
- **新增** 3 个 IR opcodes：`LoadLocalAddr` / `LoadElemAddr` / `LoadFieldAddr`
- **新增** VM 透明 deref：`frame.get/set` 检测寄存器持有 `Value::Ref` 时自动跟随
- **新增** C# IR 3 个 `IrInstr` types + zbc binary serde
- **新增** C# `IrFunction.ParamModifiers: List<byte>?` 字段
- **新增** C# `FunctionEmitterCalls.EmitBoundCall`：检测 `BoundModifiedArg` → 根据 `Inner` 形态 emit `LoadXxxAddr` 产生 Ref 寄存器作为 callee arg
- **新增** 7 个 golden tests `tests/golden/run/21_ref_out_in/{a..g}/`
- **更新** `docs/design/parameter-modifiers.md` "Runtime Implementation" 段从 future → current
- **更新** `docs/design/ir.md` + `vm-architecture.md` + `roadmap.md`

## Scope

| 文件路径 | 变更 | 说明 |
|---|---|---|
| `src/runtime/src/metadata/types.rs` | MODIFY | `Value::Ref` + `RefKind` + PartialEq |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | 3 新 Instruction variants |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | 3 新 OP 解码 + IrFunction.param_modifiers |
| `src/runtime/src/gc/rc_heap.rs` | MODIFY | scan_object_refs 处理 Value::Ref |
| `src/runtime/src/vm_context.rs` | MODIFY | `frame_state_at(idx)` API |
| `src/runtime/src/interp/mod.rs` | MODIFY | 透明 deref helper + 3 新 opcode 实现 |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | 3 新 opcode dispatch |
| `src/runtime/src/jit/translate.rs` | MODIFY | 3 新 opcode 占位 fallback |
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | 3 新 IrInstr + IrFunction.ParamModifiers |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs` | MODIFY | 3 新指令编码 + ParamModifiers |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.Instructions.cs` | MODIFY | 3 新指令解码 + ParamModifiers |
| `src/compiler/z42.IR/BinaryFormat/ZasmWriter.cs` | MODIFY | 3 新指令文本格式 |
| `src/compiler/z42.IR/IrVerifier.cs` | MODIFY | 3 新指令 verifier |
| `src/compiler/z42.IR/BinaryFormat/Opcodes.cs` | MODIFY | 新 opcode 编号 |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterCalls.cs` | MODIFY | 检测 BoundModifiedArg → emit LoadXxxAddr |
| `src/compiler/z42.Semantics/Codegen/IrGen.cs` | MODIFY | fn.Params modifier 写入 IrFunction.ParamModifiers |
| `src/runtime/tests/golden/run/21_ref_out_in/{a..g}/` | NEW | 7 端到端 golden |
| `docs/design/parameter-modifiers.md` | MODIFY | Runtime Implementation 段从 future → current |
| `docs/design/ir.md` | MODIFY | 3 新 opcode + Value::Ref 表达 |
| `docs/design/vm-architecture.md` | MODIFY | Ref 数据结构 + frame stack lookup + GC 协调 |
| `docs/roadmap.md` | MODIFY | ref/out/in 行 IrGen/VM ⏸ → ✅ |

**只读引用**：
- `spec/archive/2026-05-05-define-ref-out-in-parameters-typecheck/design.md` — Decisions 设计基础
- `src/compiler/z42.Semantics/Bound/BoundExpr.cs` — `BoundModifiedArg` / `BoundOutVarDecl`
- `src/compiler/z42.Semantics/TypeCheck/Z42Type.cs` — `Z42FuncType.ParamModifiers`

## Out of Scope

- 编译期 lang 验证：已在前置 spec 完成
- JIT / AOT 后端对 `Value::Ref` 的支持：CLAUDE.md "interp 全绿前不碰 JIT/AOT"
- 6 项设计期延后特性（D1-D6）：维持前置 spec 的 deferred 决策
- pre-existing `error-codes.md` Z 前缀 vs E 前缀不一致：独立 fix spec
- async / iterator 与 ref 的运行时交互：当前 z42 未实现

## Open Questions

- [ ] **R1**：`RefKind::Stack` 用 `frame_idx`（栈索引）—— 推荐 ✓
- [ ] **R2**：透明 deref 位置：`frame.get/set` 内部 —— 推荐 ✓（单点 dispatch，所有指令统一）
- [ ] **R3**：`out` 参数 caller 端 init —— 已有 `Frame::new` `vec![Null; size]`，无需额外指令
- [ ] **R4**：`out var x` callsite —— TypeChecker 已注册为 caller local（reg），Codegen emit `LoadLocalAddr` 指向该 reg
- [ ] **R5**：`Field RefKind` 用 field_name（与 FieldGet 一致）—— 推荐 ✓

R 系列有倾向但归档为 design.md 决议，可在 design.md 起草时确认。
