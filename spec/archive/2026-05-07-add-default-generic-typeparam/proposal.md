# Proposal: add-default-generic-typeparam (D-8b-3 Phase 2)

## Why

Phase 1（`spec/archive/2026-05-06-add-default-expression/`）落地的 `default(T)` 仅支持 fully-resolved T —— 任何 generic type-parameter（如 `class Foo<R> { R x = default(R); }` 中的 `R`）都被 TypeChecker 用 E0421 InvalidDefaultType 直接拒绝，错误信息明确指向 Phase 2 deferred。

Phase 2 解锁泛型 type-param `default(T)`，落地后立即触发：

- D-8b-1 完整通路：`MulticastFunc<T,R>.Invoke(continueOnException=true)` 全跑完后返回 `R[]`，失败位置填 `default(R)`，多失败聚合抛 `MulticastException<R>`（design `delegates-events.md` K8 完整语义）；当前 stdlib 仍抛非泛型 base
- 任意泛型容器 / framework 代码可写 `T x = default(T);` 而不再被编译期阻断
- 泛型字段默认值 init（`class Foo<T> { public T x = default(T); }`）

## What Changes

- 新 IR 指令 `DefaultOf(dst: Reg, type_param_index: byte)` —— 操作数是当前 receiver class 的 `type_params` 数组中的 0-based 索引
- TypeChecker 解除 `Z42GenericParamType` 的 E0421 gate，改为：
  - 在 instance method / ctor body 内、当 T 是 receiver class 的 type-param → 生成 `DefaultOf(dst, idx)`（`idx` 在 codegen 时确定）
  - 其他场景（free generic function / 方法级 type-param `m<U>` / static method on generic class）仍 emit DefaultOf 但 runtime 走 graceful-degradation 路径（fall through 到 Null）
- VM interp：`DefaultOf` handler 走 `frame.regs[0] -> Object -> type_desc.type_args[idx] -> default_value_for(...)`；非 Object / idx OOB 时退化为 `default_value_for("unknown")` = Null
- VM JIT：新 helper `jit_default_of`，与 interp 等价
- zbc 版本 0.8 → 0.9（新 opcode 不向后兼容；pre-1.0 直接 bump）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/compiler/z42.IR/BinaryFormat/Opcodes.cs` | MODIFY | 加 `DefaultOf = 0xA3`（next free byte 在 address-load 段）|
| `src/compiler/z42.IR/IrModule.cs` | MODIFY | 加 `DefaultOfInstr(TypedReg Dst, byte ParamIndex) : IrInstr` record |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.cs` | MODIFY | `VersionMinor` 0.8 → 0.9 |
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs` | MODIFY | 加 `DefaultOfInstr` 编码分支 |
| `src/compiler/z42.IR/BinaryFormat/ZbcReader.Instructions.cs` | MODIFY | 加 `DefaultOf` 解码分支 |
| `src/compiler/z42.IR/BinaryFormat/ZasmWriter.cs` | MODIFY | 加 `DefaultOfInstr` 文本格式 `%dst = default.of  $idx` |
| `src/compiler/z42.Semantics/TypeCheck/TypeChecker.Exprs.cs` | MODIFY | 解除 `Z42GenericParamType` E0421 gate；记录 in-class type-param + 索引 |
| `src/compiler/z42.Semantics/Codegen/FunctionEmitterExprs.cs` | MODIFY | `BoundDefault` 走 generic 分支时 emit `DefaultOfInstr`；其他保持 Phase 1 Const* 路径 |
| `src/runtime/src/metadata/bytecode.rs` | MODIFY | 加 `Instruction::DefaultOf { dst, param_index }` 变体 + decode 分支 |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | 加 `DefaultOf` dispatch 分支 |
| `src/runtime/src/jit/translate.rs` | MODIFY | 加 `DefaultOf` JIT 编码分支 |
| `src/runtime/src/jit/helpers.rs` 或新文件 | MODIFY/NEW | 新 helper `jit_default_of` |
| `src/compiler/z42.Tests/DefaultExpressionTests.cs` | MODIFY | 加 generic-T 通过 / generic-T method-level 退化 testcase |
| `src/tests/operators/default_generic_param/` | NEW | golden — `class Foo<R>` 在 method body / ctor body 内用 `default(R)` |
| `src/tests/operators/default_generic_param_field_init/` | NEW | golden — 泛型字段 `T x = default(T);` |
| `src/tests/errors/421_invalid_default_type/` | MODIFY | 把 generic-T case 从 E0421 改成"Phase 2 已解锁"的对照案例（保留 unknown-type 的 E0421）|
| `docs/design/default-expression.md` 或 `language-overview.md` | MODIFY | 同步 generic-T 支持 |
| `docs/deferred.md` | MODIFY | 把 D-8b-3 Phase 2 移到"已落地" |
| `docs/design/delegates-events.md` | MODIFY | §7 把 `default(R)` 的"待 Phase 2"备注改为"Phase 2 已解锁"|

**只读引用**（理解上下文必需，不修改）：

- `spec/archive/2026-05-06-add-default-expression/` — Phase 1 设计 / E0421 gate 当前位置
- `src/runtime/src/metadata/types.rs` — `TypeDesc.type_args / type_params / default_value_for`
- `src/runtime/src/interp/exec_instr.rs` `FieldGet` 分支 — 复用"this → Object → type_desc"模式
- `src/runtime/src/jit/helpers_object.rs` `jit_field_get` — JIT helper shape 参考

## Out of Scope

- **泛型 free function `void f<T>() { T x = default(T); }`**：由于无 `this`，需要新 calling convention 把 type_args 注入 callee frame；本变更走 graceful-degradation（编译通过 + 运行时返回 Null），完整支持留给后续 spec
- **方法级 type-param `class Foo { void m<U>() { U x = default(U); } }`**：与 free function 同理，需要 method-level type_args 计参；同 graceful-degradation
- **Static method on generic class `class Foo<T> { static T defaultT() { return default(T); } }`**：static method 无 `this`，同上
- **新 IR `DefaultOf` 之外的 generic 运行时反射 API**（`typeof(T)` / `T.GetType()`）：纯然 out of scope
- **Monomorphization**：z42 走 erasure model（`docs/design/generics.md`），本变更不引入 mono；runtime type_args 查表是 erasure 模型下的标准做法

## Open Questions

- [ ] `param_index` 用 byte（255 个 type-param 上限）还是 ushort？建议 byte，C# / Rust 实际 generic class 极少超过 4 个 type-param（typical 1-3）
- [ ] `DefaultOf` opcode byte 选 `0xA3` 还是另起新段？取决于现有 0xA0-0xA2 的语义聚类
