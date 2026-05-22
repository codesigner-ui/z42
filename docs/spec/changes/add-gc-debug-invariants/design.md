# Design: GC Debug-Only Invariant Checks

## Architecture

```
                  collect_cycles / collect_cycles_with_context
                                │
                                ▼
                      mark + sweep + ...
                                │
                                ▼
              #[cfg(debug_assertions)]
              self.debug_validate_invariants()
                                │
                                ├─► region_object.validate()
                                ├─► region_array.validate()
                                └─► heap-wide checks (mark_queue, stats)
                                        │
                                        ▼
                              Result<(), Violation>
                                        │
                  debug_validate panics on Err with detail
```

Two-layer API:
- `Region<T>::validate(&self) -> Result<(), Violation>` — returns
  structured error (test-friendly).
- `ArcMagrGC::debug_validate_invariants(&self)` — panicking wrapper
  used in production code paths via `cfg(debug_assertions)`.

## Decisions

### Decision 1: Two-layer API (Result + panicking wrapper)

**问题**: 应该 panic 立即（`debug_assert!`）还是返回 `Result` 让 caller 处理？

**选项**:
- A — 只 panic：简单，但 tests 无法 inspect violation 类型
- B — 只 Result：调用方都要 unwrap；额外 verbosity
- C — 双 API：`validate()` 返 Result（test 友好）+ `debug_validate_invariants()` panic wrapper（production 路径用）

**决定**: C. tests want to assert a specific `Violation` variant fires
(`assert!(matches!(err, Violation::OldEntryInYoungList { .. }))`).
Production debug-mode wants fail-fast panic. Both shapes serve their
audience.

### Decision 2: cfg(debug_assertions) gating

**问题**: 用 `cfg(test)`, `cfg(debug_assertions)`, or feature flag?

**选项**:
- A — `cfg(test)`: only compiled during `cargo test`. Misses runtime
  debug builds.
- B — `cfg(debug_assertions)`: compiled in both debug builds AND
  tests. Standard for "expensive checks ok in debug".
- C — Feature flag (`feature = "gc-invariants"`): explicit opt-in.

**决定**: B (`cfg(debug_assertions)`). Matches Rust idioms — `debug_assert!`
itself is gated this way. Anyone running `cargo build` (default = debug)
gets the checks. Production release builds skip entirely.

### Decision 3: Validate frequency = once per collect

**问题**: How often to run validation?

**选项**:
- A — After every alloc: too slow; alloc is hot path.
- B — After every collect: bounded by collect frequency; ~µs validation
  on top of µs-ms collect is fine.
- C — Periodically (every Nth call): adds threshold complexity.

**决定**: B. Collect is the primary state-mutating GC operation.
Invariants either hold post-collect or never. Tests can additionally
invoke `validate()` at strategic points.

### Decision 4: Validation cost budget

Region validation walks all chunks + young_list + free_list once: O(N).
For typical heap (10k objects), that's ~10k entry checks ≈ 100 µs in
debug. Acceptable given collect time is typically µs–ms.

If cargo test slows significantly (e.g. > 2× baseline), we'd add a
throttle (validate every Nth collect). Initial measurement target:
unchanged `cargo test --lib gc::` time within 20%.

### Decision 5: Violation enum is exhaustive

Each scenario in spec.md corresponds to one `Violation` variant.
Future invariants add new variants. Tests pattern-match specific
variants to confirm the right check fired (`assert!(matches!(...
Violation::XxxYyy { .. }))`).

### Decision 6: Heap-wide invariants live on ArcMagrGC, not the trait

`MagrGC` trait stays clean. `debug_validate_invariants` is an
ArcMagrGC-specific implementation detail; other backends can add their
own checks but the public surface doesn't change.

## Implementation Notes

### Violation enum (in region.rs)

```rust
#[cfg(debug_assertions)]
#[derive(Debug)]
pub enum Violation {
    OldEntryInYoungList { chunk_idx: u16, entry_idx: u16, gen_age: u8 },
    YoungEntryNotInList { chunk_idx: u16, entry_idx: u16 },
    DuplicateInYoungList { chunk_idx: u16, entry_idx: u16 },
    AliveSlotInFreeList { chunk_idx: u16, entry_idx: u16 },
    LocationMismatch { chunk_idx: u16, entry_idx: u16, recorded: (u16, u16) },
    CardDirtyLengthMismatch { expected: usize, actual: usize },
    StaleMarkAfterSweep { chunk_idx: u16, entry_idx: u16 },
    StaleMarkQueue { len: usize },
}
```

### Region::validate sketch

```rust
#[cfg(debug_assertions)]
pub fn validate(&self) -> Result<(), Violation> {
    // 1. card_dirty length
    if self.card_dirty.len() != self.chunks.len() {
        return Err(Violation::CardDirtyLengthMismatch { ... });
    }

    // 2. young_list: no duplicates, all gen_age < threshold
    let mut seen = std::collections::HashSet::with_capacity(self.young_list.len());
    for &(ci, ei) in &self.young_list {
        if !seen.insert((ci, ei)) {
            return Err(Violation::DuplicateInYoungList { ci, ei });
        }
        let entry = unsafe { self.chunks[ci as usize][ei as usize].assume_init_ref() };
        if entry.gen_age() >= PROMOTION_THRESHOLD {
            return Err(Violation::OldEntryInYoungList { ci, ei, gen_age: entry.gen_age() });
        }
    }

    // 3. Every young alive entry in young_list
    for (ci, chunk) in self.chunks.iter().enumerate() {
        for ei in 0..CHUNK_SIZE {
            if !self.initialized[ci][ei] { continue; }
            let entry = unsafe { chunk[ei].assume_init_ref() };
            if entry.alive.load(Ordering::Acquire)
                && entry.gen_age() < PROMOTION_THRESHOLD
                && !seen.contains(&(ci as u16, ei as u16))
            {
                return Err(Violation::YoungEntryNotInList { ci: ci as u16, ei: ei as u16 });
            }
            // 4. location matches storage
            if entry.location != (ci as u16, ei as u16) {
                return Err(Violation::LocationMismatch { ... });
            }
        }
    }

    // 5. free_list slots all alive=false
    for &(ci, ei) in &self.free_list {
        let entry = unsafe { self.chunks[ci as usize][ei as usize].assume_init_ref() };
        if entry.alive.load(Ordering::Acquire) {
            return Err(Violation::AliveSlotInFreeList { ci, ei });
        }
    }

    Ok(())
}
```

### ArcMagrGC::debug_validate_invariants

```rust
#[cfg(debug_assertions)]
pub(crate) fn debug_validate_invariants(&self) {
    self.region_object.lock().validate()
        .unwrap_or_else(|v| panic!("region_object invariant violation: {:?}", v));
    self.region_array.lock().validate()
        .unwrap_or_else(|v| panic!("region_array invariant violation: {:?}", v));

    // Heap-wide
    let q_len = self.mark_queue.lock().len();
    if q_len != 0 {
        // Acceptable mid-cycle in concurrent mode; otherwise stale.
        // We're called post-collect, so queue must be empty.
        panic!("mark_queue stale: {} entries left", q_len);
    }

    // Stale mark bits: iterate both regions, check any alive+marked entry.
    self.region_object.lock().iterate_alive(|h, e| {
        if e.is_marked() {
            panic!("stale mark bit after sweep: chunk={}, entry={}",
                h.chunk_idx, h.entry_idx);
        }
    });
    self.region_array.lock().iterate_alive(|h, e| {
        if e.is_marked() {
            panic!("stale mark bit after sweep: chunk={}, entry={}",
                h.chunk_idx, h.entry_idx);
        }
    });
}
```

### Integration in collect_cycles

```rust
fn collect_cycles(&self) {
    // ... existing mark + sweep + stats ...
    #[cfg(debug_assertions)]
    self.debug_validate_invariants();
}
```

Same for `collect_cycles_with_context` (after each mode's sweep
completes).

## Testing Strategy

### Unit tests (P0-P1 region + heap)

- `validate_healthy_region_passes` — fresh + alloc/tombstone, all OK
- `validate_detects_old_in_young_list` — manually inject old entry into
  young_list, assert Violation::OldEntryInYoungList
- `validate_detects_young_not_in_list` — alloc, manually remove from
  young_list, assert violation
- `validate_detects_duplicate_in_young_list`
- `validate_detects_alive_in_free_list`
- `validate_detects_location_mismatch`
- `validate_card_dirty_length` (mismatch via direct field tweak)
- `validate_detects_stale_mark_after_sweep`
- `validate_detects_stale_mark_queue`

### Integration (P2)

- Existing GC tests run with invariants active — must all pass
  (regression: the 4 GC algorithms we just landed don't violate any)
- `Z42_GC_MODE=concurrent / generational` stdlib runs (under debug) —
  invariants checked at every collect

## Phasing

3 phases:
- **P0**: Region<T>::validate() + Violation enum + region invariant
  tests
- **P1**: ArcMagrGC::debug_validate_invariants() + heap-wide checks +
  collect_cycles integration + invariant tests
- **P2**: Archive + docs update (gc.md "Debug invariants" subsection)

## Deferred / Future Work

### add-gc-runtime-checks
- Opt-in production-mode invariants via feature flag; selected checks
  always enabled for paranoid users. Cost: per-collect O(N) overhead.

### add-gc-property-based-stress (C2)
- Generate random workloads + apply invariants. Builds on this spec's
  validate() primitive.

### add-gc-invariant-history
- Track which invariant was violated in which test/commit (audit trail
  for regressions over time).
