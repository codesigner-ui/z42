# Design: Multi-collector arbitration

## Architecture

```
当前（after counter-throttling）：
  request_gc_pause(ctx) {
      *gc_phase.lock() = Requested;
      loop {
          let need = vm_contexts.len() - 1;     // ← excludes only `self`!
          if parked_count >= need { break; }
          gc_phase_cv.wait();
      }
      *gc_phase.lock() = Marking;
      return GcPauseGuard;
  }

  // Two concurrent callers A and B:
  // - A: need = 4 (5 ctxs - 1 self = A)
  // - B: need = 4 (5 ctxs - 1 self = B)
  // - But both A and B are "self" simultaneously → only 3 others park
  // - Both wait forever → DEADLOCK

本 spec 后：
  VmCore.collector_active: AtomicBool                              ← NEW

  request_gc_pause(ctx) -> Option<GcPauseGuard> {
      if !collector_active.compare_exchange(false, true).is_ok() {
          // Another collector active. Park as mutator + return None.
          park_until_idle(ctx);
          return None;
      }
      // We're the unique collector.
      *gc_phase.lock() = Requested;
      loop {
          let need = vm_contexts.len() - 1;
          if parked_count >= need { break; }
          gc_phase_cv.wait();
      }
      *gc_phase.lock() = Marking;
      return Some(GcPauseGuard { ctx });
  }

  GcPauseGuard::drop():
      *gc_phase.lock() = Idle;
      gc_phase_cv.notify_all();
      collector_active.store(false, Release);    ← NEW
```

The CAS ensures exactly ONE thread is the collector at a time. Losing
threads park as mutators (contribute to parked_count) until the active
collector finishes, then return without collecting themselves.

## Decisions

### Decision 1: CAS ordering — AcqRel / Relaxed

**问题**：`compare_exchange(false, true, success_order, failure_order)` —
怎么选？

**决定**：`compare_exchange(false, true, AcqRel, Relaxed)`.
- success **AcqRel**: Acquire pairs with previous collector's `Release`
  store on `collector_active.store(false)` in GcPauseGuard::drop — gives
  us visibility on heap state changes from previous collect. Release
  pairs with subsequent `*gc_phase.lock() = Requested` so workers see
  Requested when they read phase
- failure **Relaxed**: we're going to park_until_idle anyway, which takes
  a Mutex (full acquire); no need for fence on failure path

### Decision 2: Losing collector — fall back to park-as-mutator

**问题**：B 的 CAS 失败时怎么办？

**选项**：
- A) park_until_idle + return None — caller skips collect
- B) busy-spin retry CAS — bad (uses CPU; first collector might be long)
- C) error-return ("collect already in progress") — caller writes
  retry-or-fail logic. Awkward for auto-collect drain

**决定**：**A**. park_until_idle contributes to parked_count so the active
collector's wait condition is met faster. After active drops guard,
phase = Idle, our park wakes, returns from request_gc_pause. The flag
(`needs_auto_collect`) was already drained by the active collector path
or remains set if our caller drained it before checking — either way
correct.

### Decision 3: Std.GC.Collect 行为 — silent no-op or error?

**问题**：用户 explicit `Std.GC.Collect()` 调用，正撞到另一个 collector
active：no-op 还是抛异常？

**决定**：**silent no-op**. 理由：
- C# `GC.Collect()` is best-effort, may be queued / merged
- Rust `std::alloc::System` 没有 explicit collect 概念
- 抛异常会破坏 z42 用户预期（"我调 Collect 就 Collect，不该抛")
- 实践影响小：active collector 完成后 used_bytes 已下降，第二个调用本来
  也找不到可回收对象

docstring 注明 "best-effort, may be no-op if another collect is in progress".

### Decision 4: 不引入 `try_request_gc_pause` 独立 API

**问题**：原 spec 提到的 try 变体，是否独立 API？

**决定**：v0 不分离。`request_gc_pause` 直接返 Option 统一表达。如果
未来需要"必须 collect"语义（blocking wait for previous collector），加
独立 `request_gc_pause_blocking` spec。

### Decision 5: park-as-mutator inside request_gc_pause 是否计 parked_count

**问题**：losing collector 在 request_gc_pause 内部 park，期间它算 mutator
还是 collector？

**决定**：算 **mutator** — park_until_idle 增 parked_count，notify active
collector。active collector 看到 parked_count 增加，更接近 need。Losing
collector 的"自己"现在被算入"others" 因为它不是 active collector。

时序：
- active A 调 request_gc_pause → CAS 成功 → collector_active = true
- 之后 B 调 request_gc_pause → CAS 失败 → park_until_idle → parked_count++
- A's wait loop: need = vm_contexts.len() - 1。注意：A 自己不在 parked_count
  里（A 没 park）；B 在了。其他 mutators (C, D, main) 也会 park。
- need = 4 (5 - 1)，parked_count 最终 = 4 (B + C + D + main)。A 通过。

## Implementation Notes

### VmCore field

```rust
// src/runtime/src/vm_context.rs
pub struct VmCore {
    // ... existing fields ...
    /// add-multi-collector-arbitration (2026-05-21): exclusive collector
    /// claim. Only the thread that successfully CAS-es false→true via
    /// `request_gc_pause` becomes the active collector for one round.
    /// Cleared by `GcPauseGuard::drop`.
    pub(crate) collector_active: AtomicBool,
}
```

### request_gc_pause Option return

```rust
pub fn request_gc_pause(ctx: &VmContext) -> Option<GcPauseGuard<'_>> {
    if ctx.core.collector_active
        .compare_exchange(false, true, Ordering::AcqRel, Ordering::Relaxed)
        .is_err()
    {
        // Another collector is active. Park as a mutator and return
        // None so the caller skips the collect call.
        park_until_idle(ctx);
        return None;
    }

    *ctx.core.gc_phase.lock() = GcPhase::Requested;

    let mut phase = ctx.core.gc_phase.lock();
    loop {
        let total = ctx.core.vm_contexts.lock().len();
        let need = total.saturating_sub(1);
        if ctx.core.parked_count.load(Ordering::Acquire) >= need {
            break;
        }
        ctx.core.gc_phase_cv.wait(&mut phase);
    }
    *phase = GcPhase::Marking;
    drop(phase);

    Some(GcPauseGuard { ctx })
}

impl Drop for GcPauseGuard<'_> {
    fn drop(&mut self) {
        *self.ctx.core.gc_phase.lock() = GcPhase::Idle;
        self.ctx.core.gc_phase_cv.notify_all();
        self.ctx.core.collector_active.store(false, Ordering::Release);
    }
}
```

### Caller updates

```rust
// gc/safepoint.rs check_safepoint_slow
if ctx.core.needs_auto_collect.swap(false, Ordering::AcqRel) {
    if let Some(_pause) = request_gc_pause(ctx) {
        ctx.heap().collect_cycles();
    }
    // None: another collector is active and has parked us; nothing more to do.
}

// corelib/gc.rs
pub fn builtin_gc_collect(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    // best-effort — silent no-op if another collector is in progress
    if let Some(_pause) = crate::gc::safepoint::request_gc_pause(ctx) {
        ctx.heap().collect_cycles();
    }
    Ok(Value::Null)
}
```

### Edge case: only-self caller

When `vm_contexts.len() == 1` (only the collector itself), `need = 0`,
the wait loop sees `parked_count >= 0` immediately and breaks. Same
behavior as before — no regression.

### Edge case: GcPauseGuard never dropped

If a worker panics holding the guard, Drop still runs (Rust unwind),
collector_active released. Other threads can proceed.

If the worker LEAKS the guard (mem::forget) — collector_active stays true
forever, all future request_gc_pause park forever. Don't leak the guard.
No existing code path does this.

## Testing Strategy

- **Rust unit** (`gc/safepoint_tests.rs`):
  - `request_gc_pause_returns_some_when_uncontested`
  - `second_collector_falls_back_to_mutator_park_returns_none`
  - `release_re_enables_next_collector` — A drops, B's request succeeds
  - `parked_count_includes_failed_collector` — count consistency check
- **Cross-thread integration** (`runtime/tests/cross_thread_smoke.rs`):
  - Restore `auto_collect_triggers_via_safepoint_no_race` to 4 workers
  - Add `concurrent_gc_collect_callers_arbitrate` — 2 threads each call
    `request_gc_pause` + collect_cycles; assert one runs, one no-ops
- **Existing tests**: unit tests calling request_gc_pause directly need
  to handle Option (unwrap when expecting Some, e.g. in `request_pause_waits_for_other_mutators_to_park`)

## Deferred / Future Work

### `add-multi-collector-coalesce`
- Instead of N independent CAS losers each parking, batch them under one
  collector and coalesce their requests. Only useful if many concurrent
  Std.GC.Collect() callers become common
