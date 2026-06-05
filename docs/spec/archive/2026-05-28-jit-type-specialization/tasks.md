# Tasks: JIT type specialization (C2)

> Status: 🟢 P0 + P1 shipped. P2 partially complete.
> Created: 2026-05-27. Closed (P0+P1): 2026-05-28.

## P0 (foundation) — preserve per-register type info

- [x] 0.1 User decides Option A (zbc bump) vs B (load-time inference) — **Option A** chosen (2026-05-27)
- [x] 0.2 Add `IrType` enum in `src/runtime/src/metadata/ir_type.rs` (`9c179530`)
- [x] 0.3 REGT section in zbc; zbc 1.7 → 1.8 (`8ef184e6`)
- [x] 0.4 `zbc_reader.rs` reads REGT; `Function.reg_types`; zpkg 0.8 → 0.9
- [x] 0.5 Regen zbc-format + zpkg-format fixtures + 370 golden .zbc files
- [~] 0.6 (Option B) — superseded by A
- [x] 0.7 GREEN verified

## P1 (specialization core) — all shipped

- [x] 1.0 Box cold variants (chunks 1-5) — Value 48 B → 24 B
      (`abfcf7c3` / `3908c04f` / `1b6801b0` / `2db9d9a9` / `c7eaeab7`)
- [x] 1.0.5 `#[repr(C, u8)]` + layout pin tests (`a41050b8`)
- [x] 1.2 Add/Sub/Mul native `iadd`/`isub`/`imul` when I64 (`98426e40`).
      Div/Rem kept on helper (i64 /0 must throw a catchable z42 exception
      rather than SIGFPE).
- [x] 1.3 Comparisons native `icmp <pred>` when I64 (`fc3936f0`)
- [x] 1.4 Logical ops native `band` / `bor` / `bxor 1` when Bool (`fc3936f0`)
- [~] 1.5 String concat — kept on helper. The hot-path win was the
      type-check (skip variant match) which IS now done via reg_types
      gating, but the actual `Arc::clone` + alloc still lives in the
      helper; not worth a separate emit path.
- [x] 1.6 Microbench: `bench/scenarios/04_c2_p1_arith_loop.z42`.
      Measured **1.51× on 10M-iter SumSquares** (M-series macOS, 5-run
      avg). Below the spec's 2-5× aspiration — see P2.3 notes.
- [x] 1.7 BrCond fast path when cond is Bool-typed (`3727e469`).
      Not in original spec but was the dominant remaining helper-call
      cost in tight loops.
- [x] 1.8 ConstI32/I64/F64/Bool inline (`6b76130a`). ConstStr kept on
      helper (needs `ctx.string_pool` lookup + Arc::clone).

## P2 (cleanup) — partial

- [ ] 2.1 Remove `Value::I64` fast-path branch from helpers — deferred.
      The helpers still see ~10 % of calls from `Unknown` / mixed-type
      sites; removing the inline branch from `jit_add` etc. would
      regress those cases. Keep the branch; revisit if `reg_types`
      coverage approaches 100 % (currently ~80-90 % per spot-check on
      stdlib zbc).
- [x] 2.2 Doc note in `vm-architecture.md` — completed in this archive.
- [~] 2.3 Cold-path bench — no observable regression on the canonical
      tight loop (the cold path adds an `is_i64_typed` predicate which
      is a constant-time array index + 3 comparisons; far cheaper than
      the helper call it sometimes replaces).

## Out-of-scope items deferred for future spec

- **`jit_check_safepoint` inline** — at every backward branch this
  still costs ~5 ns for the helper call + atomic fetch_sub. Inlining
  the atomic + branch via Cranelift `atomic_rmw` would push the
  benchmark toward ~1.8×; not implemented because it needs careful
  block-splitting (fast-path takes the new fallthrough block;
  slow-path branches to a helper call site). 1-2 day spec.
- **F64 specialization** — same shape as I64 (load f64 + native
  `fadd`/`fsub`/etc. + store TAG_F64 + payload), but no F64-heavy
  benchmark exists yet to motivate. Add when needed.

## Notes

- Per-reg type tag must NOT break the existing IC slots (FieldIC /
  VCallIC) — they key on receiver TypeId, not register IrType.
  ✅ Verified: no regression in VCall / FieldGet tests.
- Cranelift `iadd` is wrapping by default — matches z42 semantics
  (`vm-wrapping-int-arith`, 2026-04-28).
- `Value::Str(Arc<str>)` (post-C1+C3) makes the str-concat fast path
  much cheaper but still needs a helper call for the allocation; only
  the type-check is inlinable.
