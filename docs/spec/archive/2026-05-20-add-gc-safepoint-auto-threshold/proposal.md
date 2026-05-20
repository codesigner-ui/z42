# Proposal: Safepoint-aware auto-threshold GC trigger

## Why

`add-gc-safepoint` (2026-05-20) wrapped the **script-explicit** `Std.GC.Collect()`
/ `Std.GC.ForceCollect()` entry points with `request_gc_pause` so concurrent
mutators correctly park before mark+sweep. But the **internal auto-threshold**
path (`arc_heap.rs::maybe_auto_collect` → `self.collect_cycles()`) still fires
inline from inside `alloc_*` with no `&VmContext` available — meaning the
scanner can race with another thread's `frame.regs` writes when memory
pressure trips auto-collect under multi-threaded workloads.

This is the last data race in the multi-thread surface. The previous spec's
Decision 6 explicitly deferred it as a known v0 limitation; now's the time
to close it.

## What Changes

- **Deferred auto-collect via flag**: instead of `maybe_auto_collect` calling
  `self.collect_cycles()` inline, set a `needs_auto_collect: AtomicBool` flag
  on VmCore. The flag is drained at the next `check_safepoint(ctx)` —
  whichever thread reaches a safepoint first becomes the collector for this
  round (atomic CAS / swap on the flag claims ownership).
- **Trait extension (default no-op)**: add `MagrGC::set_external_needs_collect_flag(&self, Arc<AtomicBool>)` so ArcMagrGC can be wired with VmCore's flag at construction. Other backends use the default no-op (only ArcMagrGC observes thresholds today).
- **Wiring**: `VmCore::new`/its constructors call `heap.set_external_needs_collect_flag(Arc::clone(&core.needs_auto_collect))` so the heap can flip the flag without needing back-pointer to VmCore.
- **Targeted test**: `cross_thread_smoke.rs` adds `auto_collect_triggers_via_safepoint_no_race` — 4 workers loop alloc with `max_bytes` set tight enough to trip auto-threshold repeatedly; assert no race / no deadlock / heap stays consistent.

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/vm_context.rs` | MODIFY | VmCore 加 `needs_auto_collect: Arc<AtomicBool>`，构造路径初始化 false；构造完 VmCore 后立即 `heap.set_external_needs_collect_flag(Arc::clone(...))` |
| `src/runtime/src/gc/heap.rs` | MODIFY | `MagrGC` trait 加 `fn set_external_needs_collect_flag(&self, _flag: Arc<AtomicBool>) {}` 默认 no-op |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | ArcMagrGC 加内部字段 `external_needs_collect: Mutex<Option<Arc<AtomicBool>>>`；实现 trait 方法存入；`maybe_auto_collect` 改为：if pressure-trip && flag present → `flag.store(true, Release)` 而非 `self.collect_cycles()` |
| `src/runtime/src/gc/safepoint.rs` | MODIFY | `check_safepoint(ctx)` fast path扩展：Idle 时检查 `ctx.core.needs_auto_collect.swap(false, AcqRel)`；为 true → 当前线程承担 collect = `request_gc_pause(ctx) + ctx.heap().collect_cycles()` |
| `src/runtime/src/gc/safepoint_tests.rs` | MODIFY | 加 `auto_collect_flag_drained_at_next_safepoint` 单测 |
| `src/runtime/tests/cross_thread_smoke.rs` | MODIFY | 加 `auto_collect_triggers_via_safepoint_no_race` —— 设小 max_bytes，4 workers 反复 alloc，自然触发 auto-threshold 多次 |
| `docs/design/runtime/concurrency.md` | MODIFY | "Runtime foundation 现状" 表 "并发 GC" 行 🟡 → ✅（auto-threshold 也 safepointed） |
| `docs/design/runtime/vm-architecture.md` | MODIFY | Safepoint 协议章节"v0 范围"表里的 auto-threshold 行从 unguarded 改为 deferred-flag-based |
| `docs/spec/changes/add-gc-safepoint-auto-threshold/` | NEW | 本 spec |

**只读引用**：

- `src/runtime/src/gc/safepoint.rs` — 看 check_safepoint 当前结构
- `docs/spec/archive/2026-05-20-add-gc-safepoint/design.md` Decision 6 amendment

## Out of Scope

- **JIT-mode auto-threshold**：JIT 不在本 spec 覆盖范围；JIT 的 safepoint 实施
  在 `add-gc-safepoint-jit`
- **取代 collect_cycles 的 fundamental trait 签名扩展**：本 spec 用 flag-via-Arc
  最小侵入，不改 alloc_* 方法签名

## Open Questions

- [ ] **Single-thread fast path**：当只有一个 VmContext 时，是否需要异步化？
      答：可以同步化（vm_contexts.len() == 1 时 request_gc_pause 立即进 Marking），
      flag-based 路径在单线程下也正确。Design Decision 1 记录
- [ ] **flag set 后到 next safepoint 之间多 alloc 怎么办？** 若分配速度远超
      safepoint 频率，pressure 持续超阈值，flag 反复被 set（idempotent）；
      collect 完成后 maybe_reset_near_limit_warned 会重置 last_auto_collect_used
      —— 不连续触发。Design Decision 2
