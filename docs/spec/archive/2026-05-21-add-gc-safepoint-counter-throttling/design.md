# Design: Counter-throttled safepoint fast path

## Architecture

```
当前（after add-gc-safepoint-jit）：
  check_safepoint(ctx) {
      let phase = *ctx.core.gc_phase.lock();              ← Mutex lock
      if matches!(phase, Requested | Marking) { park; return; }
      if ctx.core.needs_auto_collect.swap(false, AcqRel) {
          ... safepoint-wrapped collect ...
      }
  }

本 spec 后：
  check_safepoint(ctx) {
      // Fast path: relaxed counter dec, no Mutex.
      let prev = ctx.safepoint_skip.fetch_sub(1, Relaxed);
      if prev > 1 { return; }    // 99.9% of calls hit here

      // Slow path: reset counter + run full check.
      ctx.safepoint_skip.store(THROTTLE_N, Relaxed);
      check_safepoint_slow(ctx);
  }

  fn check_safepoint_slow(ctx) {
      // ... existing body unchanged ...
  }
```

The fast path is `fetch_sub + compare + branch` (~3-5 ns); only the
slow path (every Nth call) takes the existing locks. Worker liveness
under GC request stays bounded by N iterations.

## Decisions

### Decision 1: THROTTLE_N = 1024

**问题**：N 太大 → 单个 mutator 响应慢 (collector 等更久); N 太小 → fast
path 节省减少。

**选项**：
- **A** N = 256 (256 iters max latency)
- **B** N = 1024 (default; OpenJDK-like)
- **C** N = 4096 (more aggressive throttling)

**决定**：**B (1024)**。理由：
- HotSpot / V8 / Go runtime 都用 1024 ~ 4096 范围
- 假设 z42 hot loop 单 iter ~50ns → 1024 iters = 51us 最坏 latency
- 远小于 GC 实际 collect 时间（10ms+ 量级），不是瓶颈
- fast path 1023/1024 ≈ 99.9% 命中，节省最大化

### Decision 2: u32 counter + saturating_sub 防溢出

**问题**：counter 用 u32 还是 u64？

**决定**：**u32**。u32::MAX / N = 4.2 亿 fast-path hits 之间才会一次"溢出"
（其实不溢出，每 N 次就 reset 回 N）。saturating_sub 防御性 — `fetch_sub`
本身会 wrap，但我们在到 0 前就 return，逻辑上不会 wrap。仍用 saturating
版避免任何边界 case。

### Decision 3: counter 放 VmContext 字段（不用 thread_local）

**问题**：counter 是 per-thread 还是 per-VmContext？

**决定**：**VmContext field**。理由：
- VmContext 已经是"per-thread state"（add-vmcontext-registry 后每 OS thread
  一个 VmContext）
- 与所有其他 per-thread state (pending_exception, call_stack, func_ref_slots)
  存放位置一致
- thread_local 需要 with(|cell| ...) 闭包；VmContext field 是 atomic field —
  无闭包开销
- 测试时直接 `ctx.safepoint_skip.load()` 检查，比 thread_local 容易

### Decision 4: Env override `Z42_SAFEPOINT_THROTTLE`

**问题**：调试时（latency-sensitive 测试 / multi-thread bug 重现）需要
disable throttling。

**决定**：加 env override。在 VmContext 构造时读 `Z42_SAFEPOINT_THROTTLE`：
- unset → 1024 (default)
- = "1" → counter 始终 1，每次 fast path 都 fall through (等价 disable)
- = N → use N
- invalid (non-numeric) → fall back 1024 + stderr warning

读一次后 cache 到 `static OnceLock<u32>`（不需要每次 ctx 构造都 reparse env）。

### Decision 5: 与 jit_check_safepoint 的关系

**问题**：JIT 走 `jit_check_safepoint(frame, ctx)` trampoline，trampoline 调
`gc::safepoint::check_safepoint(vm_ctx)`。throttle 优化直接在
`check_safepoint` 内部，trampoline 不用改 — 自动受益。

**决定**：trampoline 零改动。Counter throttling 透明应用到所有 caller（interp
+ JIT）。

## Implementation Notes

```rust
// src/runtime/src/vm_context.rs

pub struct VmContext {
    // ... existing fields ...

    /// add-gc-safepoint-counter-throttling (2026-05-21): per-thread
    /// throttle counter. Decremented on each `check_safepoint` fast
    /// path; only the slow path runs when counter reaches 0 (every
    /// `THROTTLE_N` calls).
    pub(crate) safepoint_skip: std::sync::atomic::AtomicU32,
}

// In constructor (new_internal / new_with_core):
let init = crate::gc::safepoint::throttle_n();
// ... add to VmContext literal:
//   safepoint_skip: AtomicU32::new(init),
```

```rust
// src/runtime/src/gc/safepoint.rs

use std::sync::OnceLock;

/// Default throttle: every 1024 safepoint calls runs the slow path.
const DEFAULT_THROTTLE: u32 = 1024;

/// Cached throttle value (read once from env on first access).
static THROTTLE: OnceLock<u32> = OnceLock::new();

pub fn throttle_n() -> u32 {
    *THROTTLE.get_or_init(|| {
        match std::env::var("Z42_SAFEPOINT_THROTTLE") {
            Ok(s) => match s.parse::<u32>() {
                Ok(n) if n >= 1 => n,
                _ => {
                    eprintln!("z42: invalid Z42_SAFEPOINT_THROTTLE={s:?}; using {DEFAULT_THROTTLE}");
                    DEFAULT_THROTTLE
                }
            },
            Err(_) => DEFAULT_THROTTLE,
        }
    })
}

#[inline]
pub fn check_safepoint(ctx: &VmContext) {
    let prev = ctx.safepoint_skip.fetch_sub(1, Ordering::Relaxed);
    if prev > 1 {
        return; // fast path: counter still > 0 after decrement
    }
    // Slow path: reset counter, run full check.
    ctx.safepoint_skip.store(throttle_n(), Ordering::Relaxed);
    check_safepoint_slow(ctx);
}

#[inline(never)]
fn check_safepoint_slow(ctx: &VmContext) {
    // ── existing body: phase check + park, then auto_collect drain ──
    let phase = *ctx.core.gc_phase.lock();
    if matches!(phase, GcPhase::Requested | GcPhase::Marking) {
        park_until_idle(ctx);
        return;
    }
    if ctx.core.needs_auto_collect.swap(false, Ordering::AcqRel) {
        let _pause = request_gc_pause(ctx);
        ctx.heap().collect_cycles();
    }
}
```

### Edge case: counter race

Two threads share a `VmContext`? No — each VmContext is per-thread by
construction (add-multithreading-foundation invariant). The
`safepoint_skip` AtomicU32 is for `&VmContext` shared by FFI helpers on
the same thread; no cross-thread races.

But: even single-threaded, `fetch_sub(1, Relaxed)` is safe. Relaxed
ordering is enough — counter doesn't synchronize with anything else.

### Edge case: ctx dropped while in slow path

`check_safepoint_slow` borrows `&VmContext`; ctx outlives the call.
Drop in another thread can't happen (per-thread invariant). No race.

## Testing Strategy

- **Rust unit** (`gc/safepoint_tests.rs`):
  - `check_safepoint_fast_path_decrements_counter` — call 5 times, verify
    `safepoint_skip` decremented 5
  - `check_safepoint_slow_path_runs_every_n_calls` — set throttle to 4
    via env; call 8 times; verify slow path ran twice (e.g., via
    needs_auto_collect drain or gc_cycles)
  - `throttle_env_override_honored` — set `Z42_SAFEPOINT_THROTTLE=7`,
    construct VmContext, verify `safepoint_skip == 7` and
    `throttle_n() == 7`
- **Cross-thread integration** (`runtime/tests/cross_thread_smoke.rs`):
  existing `gc_collect_with_concurrent_mutators_no_race` covers protocol
  correctness with throttling now active. May need bumped iters per
  worker (200 → 2048+) to ensure at least one slow-path hit per worker.
- **No new stdlib tests needed** — throttling is invisible to z42 users.

## Deferred / Future Work

### `add-gc-safepoint-jit-inline-counter`
- Inline the counter decrement into JIT-emitted code, avoiding the
  `jit_check_safepoint` trampoline call per check. Yields another factor
  of 2-3x perf on tight loops. Requires careful register allocation +
  spill-free counter pointer threading

### `add-gc-safepoint-adaptive-throttle`
- Adapt `N` based on observed GC pressure: low pressure → larger N; high
  pressure → smaller N. Trade-off: complexity vs latency. Only worth
  doing if static N proves wrong for real workloads
