# Design: JIT type specialization (C2 P1)

> Status: 🟡 P0 shipped (REGT wire format, 8ef184e6 2026-05-27); P1
> design under review pending Value layout decision.

## Why this design exists

P0 made per-register `IrType` info available to the Rust JIT
(`Function::reg_types: Box<[IrType]>`). P1 is the consumer: replace
`extern "C"` helper calls with native Cranelift IR (`iadd` / `fadd` /
`icmp` / `band` / ...) when both operands have known primitive type.

The bottleneck this design must clear is **how to load and store
`Value` payloads from raw memory** inside Cranelift-generated code.
Today every arithmetic / comparison / logical op routes through a
helper because the helper hides Rust's enum layout — neither cranelift
nor the JIT cares what bytes live at `frame.regs[i]`. Eliminating the
helper call requires committing to a memory layout the JIT can emit
loads / stores against.

## Architecture

```
                 Cranelift translate.rs
                          ┃
                          ▼
              ┌──────────────────────────────┐
              │ for each Instruction::Add:   │
              │   if reg_types[a,b,dst] I64: │
              │     iadd via raw load/store  │  ← P1 fast path (new)
              │   else:                      │
              │     call hr_add helper       │  ← existing path
              └──────────────────────────────┘
                          ┃
                          ▼ (loads / stores raw bytes)
              ┌──────────────────────────────┐
              │ frame.regs[i]: Value         │
              │   layout MUST be stable      │
              └──────────────────────────────┘
```

The contract: the JIT must know `(size_of::<Value>, offset_of::<I64 payload>)`
at codegen time. Rust's default enum layout doesn't promise this.

## Layout options surveyed (2026-05-27)

| Option | Value size | Pros | Cons |
|---|---|---|---|
| **A.** Apply `#[repr(C, u8)]` to current Value | 48 B (was 40 B) | Smallest code change. JIT just needs to know tag offset = 0, payload offset = 8. | **+8 B per register slot = ~+20 % per Frame**. Closure / Ref payloads grow the max-variant slot from 32 B → 40 B (because `GcRef` is 16 B, not 8 B — `entry: NonNull` + `generation: u32` + pad). |
| **B.** Box cold variants then `#[repr(C, u8)]` | 24 B | Hot-path slot shrinks vs current 40 B (-40 %). Aligns with review.md C1 (hot/cold variant split). | Heap allocation per closure / ref creation. Touches every site that constructs those variants. ~3-4 days. |
| **C.** Parallel `repr(C) struct RegSlot { tag, payload }` + `Frame.regs: Vec<RegSlot>` + `cold: Vec<Value>` | 16 B per slot, full Value lives in `cold` table indexed by an `OtherIdx` payload | Hot slot is half the current size; JIT specialization is trivial because RegSlot is `repr(C)` by construction. | Huge refactor — every `frame.regs[i]: Value` read / write site changes. Interp, JIT, helpers, every snapshot. 5-7 days. |
| **D.** Keep Value enum as-is; add helper `jit_add_i64_typed(frame, dst, a, b)` that skips the variant match but still pays call ABI | 40 B (no change) | Smallest change. No layout assumption. Already-existing pattern (helpers/arith.rs). | Marginal win — keeps the `call` overhead. Cranelift can't inline `extern "C"`. Probably <10 % speedup on hot loops vs the helper's existing I64 fast path. |
| **E.** Use Cranelift's `load`/`store` against the helper-call result | 40 B (no change) | Forward-compat (no layout commitment). | Equivalent to D — still pays call ABI, just structured differently. |

## Decision

**Defer the decision pending User input.** Options B and C both deliver
the perf win cleanly but are multi-day refactors that intersect review.md
C1 (Value hot/cold variants). Option A is the smallest code change but
ships a +20 % per-Frame memory regression in exchange for the
specialization win — unclear whether the trade is positive without a
microbench, and the regression hits every Frame including non-numeric
code paths.

Option D is the cheapest, but the spec target is "≥2× speedup on hot
numeric loops" — D's marginal gain doesn't meet that bar.

**Recommended next step**: pick option **B** (box cold variants), because:

1. Hot-path slot shrinks (24 B < 40 B current) → wins on both perf
   AND memory.
2. The cold variants (`Closure` / `Ref` / `PinnedView` / `StackClosure`)
   are created on uncommon paths — heap allocation cost is dominated by
   the GC traffic they already generate.
3. Aligns with the future review.md C1 plan to reach 16 B Value.
4. The boxing refactor is straightforward (touches a fixed set of
   constructor + match-arm sites), no novel data structures.

But this is a 3-4 day refactor and intersects C1. **Stop here for User
direction.**

## What P0 already gives us (no change needed)

- `Function::reg_types: Box<[IrType]>` reaches `translate_function`.
- `IrType` enum mirrors C# side, byte-stable.
- Test fixtures regen'd to carry REGT bytes.

## What P1 will add (per option B sketch)

1. **Box cold variants** in `metadata::types::Value`:
   - `Closure(Box<ClosureData>)`, `StackClosure(Box<StackClosureData>)`
   - `Ref(Box<RefKind>)`, `PinnedView(Box<PinnedViewData>)`
   - `FuncRef(Box<str>)`  (already smallish but bonus -8 B)
   - All match-arms / constructors updated mechanically.
2. **`#[repr(C, u8)]` on `Value`** with explicit discriminants.
3. **`value_layout_tests.rs`** pinning size + payload offsets.
4. **`translate.rs::Instruction::Add` and siblings** branch on
   `reg_types` and emit native ops via raw memory access when typed.
5. **Microbench** verifying ≥2× speedup on the spec's reference loop.

## Open questions (User input requested)

1. Take option B (box cold variants now)? Or wait until C1 is scheduled
   to do them as a single refactor?
2. Is the +20 % memory cost of option A acceptable as a stepping stone
   while we benchmark? (Easy revert if bench is bad.)
3. Time budget: 3-4 days for B + JIT specialization; 2-3 weeks for full
   C1+P1 combined.

## Implementation Notes

- Cranelift native `iadd` is wrapping by default — matches z42 semantics
  (`vm-wrapping-int-arith`, 2026-04-28). Aligned.
- `Value::Str(Arc<str>)` (post-C1+C3) is 16 B, fits the 16 B hot slot
  in option C without boxing. String concat fast path requires the
  helper for allocation regardless; only the type check is inlinable.

## Testing Strategy

- Layout tests pinning size / payload offset / discriminants.
- All existing interp + JIT tests stay green (specialization preserves
  observable behavior).
- Microbench: `for i in 0..1_000_000 { sum += i; }` interp + JIT mode,
  before / after; expect ≥2× speedup on JIT.
- Cross-zpkg vcall tests unchanged (specialization only touches arith).
