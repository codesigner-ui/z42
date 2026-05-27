# Proposal: JIT type specialization (review.md C2)

## Why

Every arithmetic / comparison / logical op in JIT-compiled code calls
through a C helper (`jit_add`, `jit_eq`, `jit_and`, …) rather than
emitting the native Cranelift `iadd` / `icmp eq` / `band` etc.
The helper does its own `Value` match + `Box<str>` / `Arc` walk for
non-`I64` types and falls back to a `clone()` slow path otherwise.

For tight numeric loops (e.g. `for i in 0..n { sum += a[i]; }`) every
iteration pays:

- C function-call ABI (push args, call, restore)
- Match dispatch + branch in helper body
- A `Value` clone on the cold path

Profiling z42 vs CoreCLR / Java JIT: CoreCLR emits `add r10, r11`
direct (three bytes). We emit a `call` to a Rust helper. **Estimated
2-5× speedup on hot numeric loops** when we can emit the native op
directly.

## What Changes

Three-part work:

**P0 (foundation) — per-register type info reaches JIT translate**.
Currently the C# IR carries `TypedReg(Id, IrType)`, but
`ZbcWriter.Instructions.cs::WriteReg` writes only `(ushort)reg.Id` —
the `Type` field is dropped at the binary boundary. Even the JSON
serde path on the Rust side strips it (`bytecode_serde.rs::to_reg`).
This makes C2 impossible by construction: the JIT translator has
nothing to specialize on.

Two options:

  - **Option A (zbc bump)**: add a per-register type byte to every
    `WriteReg` site. Bumps zbc minor; ZbcWriter (C#) + zbc_reader
    (Rust) update; per-Instruction `Reg` fields become `(u32, IrType)`
    pairs (or a parallel `Vec<IrType>` per Function).
  - **Option B (load-time inference)**: keep zbc layout; reconstruct
    type info on Rust side by walking blocks and propagating from
    `Const*` opcodes + `param_types`. Cheaper format-wise but more
    complex Rust code.

Recommend **Option A** — single zbc bump, simpler downstream.

**P1 (specialization core) — Cranelift native emission**.
In `jit::translate::translate_function`, for each arithmetic /
comparison / logical opcode:

  - If `a.type == b.type == IrType::I64`, emit Cranelift `iadd` /
    `icmp eq` / `band` / etc. directly. Skip the helper.
  - Same for `IrType::F64` → `fadd` / `fcmp eq`.
  - Same for `IrType::Bool` (logical ops) → `band` / `bor` / `bnot`.
  - For mixed / `Unknown` types, fall through to existing
    `hr_add` / `hr_eq` / `hr_and` helper call.

The fast path must still write back to `frame.regs[dst]` with the
proper `Value` discriminant (I64 / F64 / Bool) — Cranelift can compute
this by hand-coding the `Value` enum layout. Hot path becomes ~5
instructions instead of a function call + match.

**P1.5 (str concat path)** — `Value::Str(Arc<str>)` clones became
`Arc::clone` (atomic incr) after C1+C3 landed; the `String` fast path
in `jit_add` (string concatenation) can be inlined similarly once the
common case (both `Str`) is detected via type info.

**P2 (cleanup)** — once specialization handles >90 % of arithmetic
sites, the helpers themselves become cold and can drop their fast-path
branches (existing `Value::I64` check becomes redundant).

## Scope

P0 alone:

| File | Change |
|------|--------|
| `src/compiler/z42.IR/BinaryFormat/ZbcWriter.Instructions.cs`     | `WriteReg` writes `u16 id + u8 type` |
| `src/runtime/src/metadata/zbc_reader.rs`                          | matching read; new `TypedReg { id, ty }` struct or parallel per-fn `Box<[IrType]>` |
| `src/runtime/src/metadata/bytecode.rs`                            | `Reg` field type updated; `IrType` enum added |
| `src/runtime/src/metadata/bytecode_serde.rs`                      | preserve `type` in JSON path too |
| `docs/design/runtime/zbc.md`                                      | minor bump changelog row |
| `src/tests/zbc-format/generate-fixtures.sh`                       | regen all 6 fixtures + `expected.json` |

P1 builds on P0 and adds `src/runtime/src/jit/translate.rs` edits +
small Cranelift emit helpers.

## Out of Scope

- Heap-typed (`Value::Object`, `Value::Array`) specialization — those
  still go through the GC heap regardless; helper overhead is dwarfed
  by allocation.
- Generic / boxed primitives — those go through `Value::I64` already.

## Open Questions

- [ ] Option A vs B (per-reg byte in zbc vs load-time inference)?
- [ ] `IrType` representation in Rust — mirror C# `enum byte` 16
      variants, or compress to a smaller set?
- [ ] Time budget: estimate 2 days for P0, 3-4 days for P1 = ~1 week
      total. Worth interleaving with smaller items?

## References

- review.md Part 2 C2 (the original analysis)
- `src/compiler/z42.IR/IrModule.cs::TypedReg` (C# side)
- `src/runtime/src/jit/helpers/arith.rs::jit_add` (current helper)
