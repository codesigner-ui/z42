# Tasks: add RuntimeObserver — push-based non-GC event stream (Phase 1)

> 状态：🟢 已完成 | 创建：2026-05-26 | 完成：2026-05-26 | 模式：minimal-mode (5 文件, refactor)

**变更说明**：introduce `RuntimeObserver` trait + `RuntimeEvent` enum mirroring
existing `GcObserver` pattern but covering **non-GC** runtime activity (module
loads, future JIT compiles, exceptions, native calls). Phase 1 lands trait +
1 event variant (`ModuleLoaded`) + 1 demo emit site in main.rs.

**原因**：docs/review.md Part 4 D3 — final remaining ops/devex item. GcObserver
existed since Phase 1 GC, but every other subsystem has zero push-based event
hookup (only pull-based RuntimeCounters from D6). Embedders / monitoring tools
that want "tell me when X happens" — not "what's the current value of Y" — have
no API.

**文档影响**：`docs/workflow/debugging.md` 加 observer 段；`docs/review.md` Part
4 D3 状态 🟡 → ✅ Phase 1。

## Tasks

- [x] 1.1 `src/runtime/src/observer.rs` NEW (~230 LOC) — `RuntimeEvent` enum (`ModuleLoaded` + `Custom` escape hatch) + `RuntimeObserver` trait + `RuntimeObserverRegistry` (snapshot-then-fire pattern matching `gc/arc_heap.rs::fire_event`) + 5 unit tests (含 reentrant-add safety)
- [x] 1.2 `src/runtime/src/lib.rs` MODIFY — `pub mod observer;` with module doc
- [x] 1.3 `src/runtime/src/vm_context.rs` MODIFY — `VmCore.runtime_observers: RuntimeObserverRegistry` (uses internal `parking_lot::Mutex` not exposed) + `VmContext::add_runtime_observer` + `VmContext::fire_runtime_event` accessors
- [x] 1.4 `src/runtime/src/main.rs` MODIFY — Phase 1 emit ModuleLoaded for boot-time loaded modules (z42.core + user artifact); recorded as `loaded_for_replay: Vec<(String, Option<u64>)>` during load, drained after VmContext::with_module installs the registry (registry only exists post-VmCore creation). Lazy-loaded zpkgs deferred to Phase 2.
- [x] 1.5 `docs/workflow/debugging.md` — append `RuntimeObserver` section with full embedder example + callback constraints
- [x] 1.6 `docs/review.md` — Part 4 D3 status ❌ → ✅ Phase 1 + priority table updated
- [x] 1.7 Build + tests + commit + push（692/692 lib tests including 5 new observer tests）

## Implementation notes (deviations from initial plan)

- Used **dedicated `RuntimeObserverRegistry` struct** wrapping the mutex rather than exposing `Mutex<Vec<Arc<dyn ...>>>` directly on VmCore — gives a cleaner API (add / fire / len / is_empty), keeps the lock type implementation-private, and provides the natural home for unit tests.
- `RuntimeObserverRegistry` uses **`parking_lot::Mutex`** for consistency with neighboring VmCore fields (vm_contexts / static_fields use parking_lot).
- Phase 1 demo emit deviates slightly from initial sketch — **replay-emit pattern** rather than per-load synchronous emit, because the observer registry only exists after `VmContext::with_module` runs (it's on VmCore which is built inside that call). Boot loads happen before, so we buffer + drain. Lazy-loaded zpkgs (post-VmContext) would fire synchronously — that's Phase 2.

## Phase 2 follow-ups (independent small refactors, not in this commit)

- `RuntimeEvent::JitCompiled { func, ir_instrs, code_bytes, duration_us }` ← `jit::compile_module` last line
- `RuntimeEvent::ExceptionThrown` / `ExceptionCaught` ← `exception::*` (throw + catch sites)
- `RuntimeEvent::NativeCallEntered { module, symbol }` ← `interp::exec_native::call_native`
- Lazy loader emit ← `metadata::lazy_loader::load_zpkg_file` end of happy path
- Adapter wrapping `GcObserver → RuntimeObserver` (optional convenience)
- Built-in JSON-lines exporter CLI flag (`--event-trace=<path>`) for diagnostics without writing custom embedder code
- OTLP / EventPipe binary format (deferred to dedicated spec — wire format design)

## Design notes (inline because minimal-mode skips design.md)

- **Why not unify with GcObserver?**: GcObserver is published API; merging would be breaking. Phase 2 can build an adapter that fans GcEvent → RuntimeObserver if there's demand.
- **`Custom { source, message }` escape hatch**: lets users (and internal experimental code) emit ad-hoc events without enum-bumping the public API; CoreCLR EventPipe does the same with arbitrary provider+payload.
- **Lock pattern**: snapshot observers → release lock → iterate-and-fire (avoids reentrancy borrow conflicts; matches GcObserver `fire_event` in `gc/arc_heap.rs:509`).
- **Demo emit site = ModuleLoaded in main.rs**: deterministic, exercises wiring end-to-end without needing JIT/exception/native infrastructure that Phase 2 will hit individually.

## Phase 2 follow-ups (separate small refactors)

- JitCompiled event ← `jit::compile_module`
- ExceptionThrown / ExceptionCaught ← `exception::*`
- NativeCallEntered ← `interp::exec_native`
- Adapter that wraps GcObserver as RuntimeObserver (optional)
- `--print-events-to=<file>` CLI flag for built-in JSON-lines exporter
- OTLP / EventPipe compat (deferred to dedicated spec)

## Why not full spec process

- Pure additive infrastructure, no wire format / opcode / public language change
- Mirrors existing GcObserver pattern (proven design)
- 5 files = minimal-mode threshold per workflow.md
- Phase 2 emit-site migrations are 1-2 file refactors each, no spec needed
