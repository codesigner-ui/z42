# Tasks: JIT type specialization (C2)

> Status: 🟡 proposal-only — waiting on User Option A/B decision
> Created: 2026-05-27

## P0 (foundation) — preserve per-register type info

- [x] 0.1 User decides Option A (zbc bump) vs B (load-time inference) — **Option A** chosen (2026-05-27)
- [x] 0.2 Add `IrType` enum in `src/runtime/src/metadata/ir_type.rs`
       (mirror C# `IrType : byte` — 16 variants) + `Function.reg_types: Box<[IrType]>` field (empty until REGT lands). 6 unit tests verify discriminant lockstep with C# side.
- [x] 0.3 (Option A) Added REGT section (NOT per-WriteReg byte — adopted parallel `Box<[IrType]>` per function to keep per-instruction layout intact). zbc bumped 1.7 → 1.8; zbc.md changelog row added.
- [x] 0.4 (Option A) `zbc_reader.rs` reads REGT section after FUNC; populates `Function.reg_types: Box<[IrType]>`. zpkg packed-mode reader reads per-member `regt_data`. Coupled bump zpkg 0.8 → 0.9; zpkg.md changelog row added.
- [x] 0.5 (Option A) Regen zbc-format + zpkg-format fixtures + 370 golden .zbc files.
- [~] 0.6 (Option B) — superseded by A.
- [x] 0.7 Verify dotnet test 1397/1397 + cargo test 679/679 + sidecar tests 11/11.

## P1 (specialization core)

- [ ] 1.1 Cranelift helper: emit_value_i64 / emit_value_f64 /
       emit_value_bool (write the `Value` enum bytes directly given a
       Cranelift register holding the raw value).
- [ ] 1.2 `translate.rs::Instruction::Add` — when `a.type == b.type ==
       IrType::I64`, emit `iadd` + `emit_value_i64`. Same for
       Sub/Mul/Div/Rem.
- [ ] 1.3 Comparison ops (Eq/Ne/Lt/Le/Gt/Ge) — emit Cranelift `icmp` /
       `fcmp` directly when typed.
- [ ] 1.4 Logical ops (And/Or/Not) on `IrType::Bool` — emit `band` /
       `bor` / `bnot`.
- [ ] 1.5 String concat (Add on two `IrType::Str`) — inline the
       `Arc::clone` + alloc path.
- [ ] 1.6 Run perf microbench (`for i in 0..1_000_000 { sum += i; }`)
       before / after — should show ≥2× speedup.

## P2 (cleanup)

- [ ] 2.1 Remove `Value::I64` fast-path branch from helpers — now cold
       (only mixed-type / unknown-type ops reach the helper).
- [ ] 2.2 Doc note in `vm-architecture.md` describing the
       specialization decision tree.
- [ ] 2.3 Verify benchmarks didn't regress on the cold path.

## Notes

- Per-reg type tag must NOT break the existing IC slots (FieldIC /
  VCallIC) — they key on receiver TypeId, not register IrType.
- Cranelift `iadd` is wrapping by default — matches z42 semantics
  (`vm-wrapping-int-arith`, 2026-04-28).
- `Value::Str(Arc<str>)` (post-C1+C3) makes the str-concat fast path
  much cheaper but still needs a helper call for the allocation; only
  the type-check is inlinable.
