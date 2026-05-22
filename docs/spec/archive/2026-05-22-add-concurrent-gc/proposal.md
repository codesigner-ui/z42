# Proposal: Concurrent Mark + Selectable GC Mode

## Why

z42 currently runs a single GC algorithm (STW mark-sweep, just landed
`add-mark-sweep-collector`). Largest STW pause measured is ~1.5 ms on
the `large_array_10k` workload (P4 bench, macOS arm64). For workloads
that demand sub-millisecond pauses, the mark phase must run
concurrently with mutators (sweep can remain STW).

This spec lands two things together:

1. **Concurrent mark phase**: replaces the STW BFS with a tricolor
   incremental-update marker. Initial root snapshot is STW (microseconds);
   the bulk of the BFS runs on a background thread while mutators
   continue executing; a short STW handshake drains the mark queue at
   the end before sweep. Sweep stays STW.

2. **Selectable GC mode**: a runtime switch (`GcMode` enum + env var
   `Z42_GC_MODE`) lets users pick `stw-mark-sweep` (default, proven
   path) or `concurrent-mark-sweep` (new path). The default stays the
   safe choice; concurrent is opt-in via env var or programmatic config.
   Future modes (generational, semispace, etc.) plug in through the same
   switch without further refactoring.

The switch is the deliberately conservative shape: the new algorithm
landing doesn't risk regressing existing production paths. STW
mark-sweep remains the default until concurrent has burn-in time.

## What Changes

- New `GcMode` enum in `src/runtime/src/gc/mod.rs` with `StwMarkSweep`
  (default) and `ConcurrentMarkSweep` variants
- New `ArcMagrGC::set_mode(GcMode)` API + `Z42_GC_MODE` env var
  resolution
- `run_cycle_collection` branches on mode — STW path unchanged; concurrent
  path is new code
- Tricolor invariant: write barrier override fires only in
  `ConcurrentMarkSweep` mode; marks `new` value gray when written into
  a heap slot
- Mark queue (Mutex<Vec<Value>>) collects gray objects; mark thread
  pops + traces; barrier pushes
- Atomic ordering audit on `marked: AtomicU8` — current `Relaxed` is
  sufficient for STW but needs `Acquire/Release` on the mark-thread/
  mutator handshake under concurrent
- New STW handshake at end of concurrent mark — short pause to drain
  remaining gray + verify termination
- Documentation in `vm-architecture.md` for the switch + concurrent path
  + when to use each mode

**Default behavior is unchanged**: opting into concurrent is an explicit
action. Existing tests + stdlib + golden continue to use STW path and
must stay GREEN.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/gc/mod.rs` | MODIFY | Export `GcMode` enum; re-export `set_mode` / `mode()` API |
| `src/runtime/src/gc/heap.rs` | MODIFY | Add `set_mode` / `mode()` to `MagrGC` trait (default impl returns StwMarkSweep — keeps non-Arc heap impls compatible) |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | Add `mode: AtomicU8` field; `set_mode` / `mode` impl; `run_cycle_collection` branches on mode |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | Override `write_barrier_field` / `write_barrier_array_elem` — when mode==Concurrent, shade-gray (mark new + push to mark queue); otherwise no-op |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | Add concurrent mark loop: spawn worker, drain queue, STW handshake |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | Add `mark_queue: Mutex<Vec<Value>>` field |
| `src/runtime/src/gc/refs.rs` | MODIFY | If atomic-ordering audit demands it, raise `marked` field ops from `Relaxed` to `AcqRel` (or document why Relaxed suffices with the handshake) |
| `src/runtime/src/gc/safepoint.rs` | MODIFY | Extend `GcPhase` enum with `ConcurrentMarking` (between `Idle` and `Sweeping`); document state transitions |
| `src/runtime/src/gc/arc_heap_tests/concurrent_mark.rs` | NEW | Unit tests: mode switch, barrier dispatch under each mode, cycle correctness under concurrent, termination handshake |
| `src/runtime/src/gc/arc_heap_tests/mod.rs` | MODIFY | Register `concurrent_mark` module |
| `src/runtime/benches/gc_cycle_bench.rs` | MODIFY | Add concurrent-mode variants of the 3 existing workloads; report STW pause time vs concurrent STW-handshake time |
| `docs/design/runtime/vm-architecture.md` | MODIFY | New "GC mode selection" section + "Concurrent mark protocol" subsection (algorithm, tricolor invariant, when to use which mode) |
| `docs/design/runtime/vm-architecture.md` | MODIFY | Update Phase table: add `add-concurrent-gc` row; A4 moves from "future" to "landed" |

**只读引用**（理解上下文必须读，但不修改）：

- `src/runtime/src/gc/safepoint.rs` — safepoint protocol + multi-collector arbitration (already supports mark thread; need to read to understand handshake integration)
- `src/runtime/src/metadata/types.rs` — `Value::is_heap_ref()` / `trace_children()` (used by barrier + mark queue)
- `src/runtime/src/interp/exec_object.rs` + `exec_array.rs` — write barrier call sites (no change; barrier override is on `ArcMagrGC`)
- `src/runtime/src/jit/helpers/object.rs` + `array.rs` — JIT barrier call sites (no change)

## Out of Scope

- **Generational GC (A3)**: separate spec; depends on this + write barriers + custom allocator
- **Concurrent sweep**: sweep stays STW in this spec; concurrent sweep is a follow-up
- **SATB barrier (snapshot-at-the-beginning)**: this spec uses incremental update (post-barrier on `new` value). SATB would need pre-barrier with `old` value — out of scope; not required for correctness
- **Lock-free mark queue (segqueue / per-thread queue)**: v1 uses `Mutex<Vec>` for simplicity; lock-free upgrade is a perf spec if contention measured
- **Concurrent root scanning**: initial root snapshot stays STW (microseconds); large-stack workloads might need concurrent scanning but not now
- **JIT-specific tricolor handling**: existing JIT write barrier dispatch (already wired in `add-write-barriers`) routes through the same `MagrGC` trait method; no JIT codegen changes needed
- **Auto-mode-switching based on workload**: user picks the mode explicitly; no heuristic

## Open Questions

无 — 设计要点（switch / tricolor / termination handshake / Sweep STW）均在 design.md 展开。实施期若出现细节决策必须停下汇报（per feedback_problem_first_then_defer）。
