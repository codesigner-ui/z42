# Proposal: Counter-throttled safepoint fast path

## Why

`add-gc-safepoint` (2026-05-20) inserts `check_safepoint(ctx)` at every
backward branch + Call return. The Idle fast path is small but not free:
one `parking_lot::Mutex` lock + one enum compare ≈ 10-20 ns per call.

For tight compute loops (e.g., bench code, matrix kernels, parser hot
paths) the safepoint check fires at every iteration. A 100 M-iteration
loop carries ~1-2 s of safepoint overhead — a real cost for users
profiling z42 against C# / Rust baselines.

`add-gc-safepoint` Decision 1 explicitly flagged this as a follow-up
optimization (`add-gc-safepoint-counter-throttling`). The plan there:
maintain a counter; only probe `gc_phase` when `counter % N == 0`
(e.g. N = 1024). Tight loops respond to a GC request within bounded
latency (~1024 iterations × per-iter cost) while paying near-zero
per-iteration safepoint overhead in the common case.

## What Changes

- **VmContext gains `safepoint_skip: AtomicU32`** — per-VmContext throttle
  counter. Reset to N on each real check; decremented on each fast-path
  hit; only when 0 does the real `gc_phase` probe run.
- **`check_safepoint(ctx)` becomes two-tier**: fast path is a single
  `compare_exchange` (or relaxed load + non-zero check + relaxed store).
  Slow path (every N-th call) runs the existing phase / drain logic.
- **Throttle constant N = 1024** (Design Decision 1). Compile-time const;
  tunable via `Z42_SAFEPOINT_THROTTLE` env override (Decision 2).
- **Correctness invariant maintained**: when collector calls
  `request_gc_pause`, it must wait for `parked_count >= mutator_count - 1`.
  Throttled mutators may take up to N iters longer to park, but the
  collector waits patiently on the Condvar (existing semantics) — no
  changes needed on the collector side.
- **3 unit tests** verifying fast-path counter decrement, slow-path
  protocol invocation every N-th call, env override honored.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/vm_context.rs` | MODIFY | VmContext 加 `safepoint_skip: std::sync::atomic::AtomicU32`；构造路径初始化为 N (1024) |
| `src/runtime/src/gc/safepoint.rs` | MODIFY | `check_safepoint` 两层：fast path 减 counter；counter 到 0 时 reset + 调原 phase/auto_collect 检查（即现在的整个 body） |
| `src/runtime/src/gc/safepoint_tests.rs` | MODIFY | 加 3 单测：fast path 不动 phase / 每 N 次调一次真 check / env override 改 N |
| `docs/design/runtime/vm-architecture.md` | MODIFY | Safepoint 协议章节加 throttle 段，说明 throttle = 1024 (默认) + env override |
| `docs/spec/changes/add-gc-safepoint-counter-throttling/` | NEW | 本 spec |

**只读引用**：

- `src/runtime/src/gc/safepoint.rs` 现有 check_safepoint 结构
- `src/runtime/src/jit/helpers/control.rs` jit_check_safepoint trampoline（不需改 — 调同一函数）

## Out of Scope

- **JIT 内联 throttle 检查**：v0 仍走 `jit_check_safepoint` trampoline；
  trampoline 内部已经吃 throttle 优化。后续若 profile 显示 trampoline
  调用本身就是瓶颈（罕见），独立 spec `add-gc-safepoint-jit-inline-counter`
  内联 counter decrement 到 native code
- **每个 VmContext 不同 throttle 值**：v0 全 VmContext 用同一常量
- **抖动 / 偏移**：N 次后是否随机化下次 N 防止多个 worker 同步触发？v0 不抖动，
  让 collector Condvar 等就行

## Open Questions

- [ ] **N 默认值**：1024 vs 4096 vs 256？trade-off：N 大 → 单 mutator 响应慢；
      N 小 → fast path 节省少。Design Decision 1
- [ ] **counter overflow**：u32 ≈ 4B 次循环 = 4B × N = 4T 次实际 hits 才溢出；
      不可达。但用 saturating_sub 避免负值。Design Decision 2
- [ ] **counter location**：VmContext 字段 vs thread-local？VmContext 更
      consistent（同所有其他 per-context state）；thread-local 略快但需
      cleanup。Design Decision 3
