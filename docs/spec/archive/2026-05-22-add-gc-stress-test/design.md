# Design: GC Random-Workload Stress Test

## Architecture

```
              ┌────────────────────────────────────────┐
   seed: u64 ▶│ SmallRng::seed_from_u64(seed)         │
              │ → reproducible PRNG                   │
              └────────────────────────────────────────┘
                              │
                              ▼
              ┌────────────────────────────────────────┐
              │ for _ in 0..iters {                   │
              │   let op = pick_op(rng, &state);      │
              │   apply(op, &heap, &mut state);       │
              │ }                                     │
              └────────────────────────────────────────┘
                              │
                              ▼
           Each `apply` may call into ArcMagrGC ops;
           collect ops trigger C1 validator post-collect.
                              │
                              ▼
                       Any panic → reported with seed
```

State tracked between ops:
- `pins: Vec<RootHandle>` — live pin handles (for unpin selection)
- `objects: Vec<Value>` — live alloc'd Values (for field_set targets)
  capped at 200 to avoid unbounded growth

## Decisions

### Decision 1: Hand-rolled SmallRng over proptest/quickcheck

**问题**: 用 `proptest` / `quickcheck` 生成 op 序列还是手写 SmallRng?

**选项**:
- A — `proptest`: declarative shrinking on failure; dev-dep ~30 packages
- B — `quickcheck`: lighter; dev-dep ~10 packages
- C — Hand-rolled `rand::rngs::SmallRng` (already transitive dep
  via `criterion`): zero new deps; manual shrinking if needed

**决定**: C. Goals are simple — deterministic seed + reproducible op
stream. Shrinking is nice-to-have, not blocking. Hand-rolled keeps
the test code obvious + no new external surface to maintain.

### Decision 2: Bounded live-objects pool

**问题**: How to avoid OOM under stress?

If stress alloc'd indefinitely without bounds, heap would grow without
limit. Bounded the `objects` pool to capacity 200; when full, randomly
replace an existing entry (its old value is dropped — eligible for GC).

This mirrors typical workloads: live set fluctuates around a bound;
churn happens at the boundary.

### Decision 3: Operation weights are static + small

Op weights chosen to maximize collision with subtle invariant bugs:
- High alloc rate → many fresh young entries for generational tests
- High field_set rate → many barrier dispatches
- Moderate pin/unpin → testing root set churn
- Low force_collect rate → moves through alloc churn between collects
- Very low set_mode rate → so each mode sees ~enough ops to stress

Weights are constants — no env-var tuning to keep tests bit-stable
across seeds. Future spec can add weight tuning if specific patterns
are missed.

### Decision 4: Seed source priority

Per-test seeds are hard-coded constants (e.g. `42`, `0x1234`). The
`Z42_STRESS_SEED` env var, if set, overrides — useful for replaying a
specific CI failure locally.

Hard-coded base seeds → reproducible CI runs. Env override → reproducible
local debug.

### Decision 5: Validator runs at each force_collect, not after each op

C1's validator already runs at end of every collect via the
`collect_cycles` integration. Stress test relies on this — no manual
`debug_validate_invariants` calls between ops. Keeps the inner loop
fast (no validation between trivial ops like field_set).

**Tradeoff**: a bug that corrupts state between collects might
require multiple iters before the next collect surfaces it. Acceptable
because invariants are static (post-collect state) not transient
(mid-op state). Mid-op consistency is the responsibility of individual
op implementations, not stress.

### Decision 6: 4 tests = 3 single-mode + 1 mode-switching

Each single-mode test stress-tests its mode in isolation.
Mode-switching test exercises transitions specifically. 4 separate
tests instead of 1 mega-test → better failure isolation (failed test
name immediately identifies which mode is affected).

## Implementation Notes

### Op enum + workload driver

```rust
#[derive(Debug)]
enum Op {
    AllocObject,
    AllocArray,
    FieldSetWithObject,    // heap-ref write → barrier dispatch
    FieldSetWithPrimitive, // primitive write → no barrier
    ArrayElemSetWithObject,
    PinRandom,
    UnpinRandom,
    ForceCollect,
    SetModeRandom, // only in mode-switching test
}

fn pick_op(rng: &mut SmallRng, allow_mode_switch: bool) -> Op {
    // Weighted distribution (literal constants below for clarity).
    // Sum 100. Adjust to fit experiment.
    let w = rng.gen_range(0..100);
    match w {
        0..=14   => Op::AllocObject,           // 15%
        15..=29  => Op::AllocArray,            // 15%
        30..=44  => Op::FieldSetWithObject,    // 15%
        45..=54  => Op::FieldSetWithPrimitive, // 10%
        55..=64  => Op::ArrayElemSetWithObject,// 10%
        65..=77  => Op::PinRandom,             // 13%
        78..=89  => Op::UnpinRandom,           // 12%
        90..=94  => Op::ForceCollect,          //  5%
        95..=99 if allow_mode_switch => Op::SetModeRandom, // 5%
        _        => Op::AllocObject,           // overflow fallback
    }
}

fn apply(op: Op, heap: &ArcMagrGC, state: &mut State, rng: &mut SmallRng) {
    use crate::metadata::{TypeDesc, NativeData, Value};

    match op {
        Op::AllocObject => {
            let v = heap.alloc_object(state.dummy_td.clone(),
                vec![Value::Null; rng.gen_range(0..=3)], NativeData::None);
            state.add_object(v);
        }
        Op::AllocArray => {
            let len = rng.gen_range(0..=5);
            let v = heap.alloc_array(vec![Value::Null; len]);
            state.add_object(v);
        }
        Op::FieldSetWithObject => {
            // Pick an object from state, write a (possibly different)
            // object into one of its slots. Triggers barrier dispatch.
            if let (Some(owner), Some(new)) = (state.pick_object(rng), state.pick_object(rng)) {
                if let Value::Object(gc) = &owner {
                    let slot_count = gc.borrow().slots.len();
                    if slot_count > 0 {
                        let slot = rng.gen_range(0..slot_count);
                        gc.borrow_mut().slots[slot] = new.clone();
                        heap.write_barrier_field(&owner, slot, &new);
                    }
                }
            }
        }
        Op::FieldSetWithPrimitive => { /* similar but Value::I64(...) */ }
        Op::ArrayElemSetWithObject => { /* similar for arrays */ }
        Op::PinRandom => {
            if let Some(v) = state.pick_object(rng) {
                let h = heap.pin_root(v);
                state.pins.push(h);
            }
        }
        Op::UnpinRandom => {
            if !state.pins.is_empty() {
                let idx = rng.gen_range(0..state.pins.len());
                let h = state.pins.swap_remove(idx);
                heap.unpin_root(h);
            }
        }
        Op::ForceCollect => {
            heap.force_collect();
            // C1 validator runs at end of collect — implicit.
        }
        Op::SetModeRandom => {
            let new_mode = match rng.gen_range(0..3) {
                0 => GcMode::StwMarkSweep,
                1 => GcMode::ConcurrentMarkSweep,
                _ => GcMode::GenerationalMarkSweep,
            };
            heap.set_mode(new_mode);
        }
    }
}
```

### Failure path

```rust
fn gc_stress_run_seeded(seed: u64, iters: usize, mode: GcMode) {
    let mut rng = SmallRng::seed_from_u64(seed);
    let heap = ArcMagrGC::new();
    heap.set_mode(mode);
    let mut state = State::new();

    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        for _ in 0..iters { apply(pick_op(...), &heap, &mut state, &mut rng); }
    }));
    if let Err(panic) = result {
        panic!("stress test panicked under seed={}, mode={:?}: {:?}",
               seed, mode, panic);
    }
}
```

### Bounded object pool

```rust
struct State {
    objects: Vec<Value>,
    pins: Vec<RootHandle>,
    dummy_td: Arc<TypeDesc>,
}

impl State {
    fn add_object(&mut self, v: Value) {
        if self.objects.len() >= 200 {
            // Replace a random one — old value drops here.
            let idx = self.objects.len() / 2;
            self.objects[idx] = v;
        } else {
            self.objects.push(v);
        }
    }

    fn pick_object(&self, rng: &mut SmallRng) -> Option<Value> {
        if self.objects.is_empty() { None }
        else { Some(self.objects[rng.gen_range(0..self.objects.len())].clone()) }
    }
}
```

## Testing Strategy

### Per-mode stress tests (P0)

- `stress_seeded_stw_short` — seed 42, 2000 iters, StwMarkSweep
- `stress_seeded_concurrent_short` — seed 0x1234, 2000 iters,
  ConcurrentMarkSweep
- `stress_seeded_generational_short` — seed 0xC0DE, 2000 iters,
  GenerationalMarkSweep
- `stress_seeded_mode_switching_short` — seed 0xBEEF, 3000 iters,
  mode-switching enabled

### Coverage gates

After each stress test, assert that:
- At least 50 force_collects happened (op weight × iters / 100 ≈ 100,
  defensive lower bound of 50)
- At least 100 field_sets / 100 alloc_objects (similar lower bounds)

These prevent silent regressions where op weights drift to skip
meaningful coverage.

### Env-var overrides

- `Z42_STRESS_ITERS=N` — override default iters
- `Z42_STRESS_SEED=N` — override base seed (useful for replays)

## Phasing

- **P0**: stress generator + 4 tests + integration with C1 validator
- **P1**: gc.md "Stress testing" subsection + Phase table + C2 entry
  landed; archive

2 commits total. Spec is small.

## Deferred / Future Work

### add-gc-coverage-guided-fuzz
- Use cargo-fuzz / libFuzzer with coverage feedback to find rare op
  sequences. Requires `#[no_mangle]` harness + fuzz target. Future
  spec; current hand-rolled randomness is sufficient as a tripwire.

### add-gc-stress-multi-thread
- Multi-thread stress running operations across threads. Could
  combine with `cross_thread_smoke.rs` patterns. Future spec.

### add-gc-stress-ci
- Run stress for millions of iters in CI (nightly job). Currently
  the 2000-iter tests run as regular `cargo test`. Long-form fuzz
  would be a separate CI target.

### add-gc-property-shrinking
- Adopt `proptest` for failure case shrinking (minimal reproducer
  generation). Future if hand-rolled debugging hits limits.
