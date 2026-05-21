# Proposal: Multi-collector arbitration (close auto-collect deadlock)

## Why

`add-gc-safepoint-counter-throttling` (2026-05-21) documented a deadlock
in the auto-collect drain protocol. Reproduces reliably when 2+ workers
share a tight `max_heap_bytes` and concurrently trip pressure:

1. Worker A's check_safepoint_slow swaps `needs_auto_collect` true → enters
   `request_gc_pause` → sets `gc_phase = Requested` → waits for
   `parked_count >= vm_contexts.len() - 1`
2. Meanwhile other workers' allocs trip threshold again → set flag back
   to true
3. Worker B (any other) hits its slow path, finds flag = true, swaps to
   false → also enters `request_gc_pause`
4. Both A and B now in the wait loop, each computing `need = total - 1`
   excluding only themselves
5. With 5 vm_contexts and 2 collectors not parked, max parkable = 3 (the
   3 mutators C/D/main). Both A and B wait for parked = 4. **Deadlock**

The previous spec narrowed `auto_collect_triggers_via_safepoint_no_race`
to 1 worker to avoid triggering. This spec fixes the protocol so 2+
workers (and concurrent Std.GC.Collect() callers) arbitrate correctly:
the first to claim becomes the unique collector; others fall back to
park-as-mutator.

## What Changes

- **VmCore gains `collector_active: AtomicBool`** — atomic "is a collector
  currently active" flag
- **`request_gc_pause` returns `Option<GcPauseGuard>`**:
  - `Some(guard)` — we claimed the collector role; proceed with collect
  - `None` — another collector is active; we already parked-as-mutator
    inside `request_gc_pause` and returned (caller continues without
    collecting)
- **`GcPauseGuard::drop`** releases `collector_active` in addition to
  setting `gc_phase = Idle` + notify_all
- **Callers updated**:
  - `corelib/gc.rs::builtin_gc_collect` / `builtin_gc_force_collect`:
    `if let Some(_pause) = request_gc_pause(ctx) { ctx.heap().collect_cycles(); }` —
    silent no-op when another collect is in progress (matches typical
    GC.Collect() semantics: best-effort, may be a no-op)
  - `gc/safepoint.rs::check_safepoint_slow`: same Option destructuring;
    if None, the flag stays drained (the active collector will handle the
    pending pressure; if more pressure arises post-collect, allocs will
    re-set the flag)
- **Test restored**: `auto_collect_triggers_via_safepoint_no_race` reverts
  to 4 concurrent workers — now passing because arbitration prevents
  multi-collector deadlock

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/vm_context.rs` | MODIFY | VmCore 加 `collector_active: AtomicBool`；构造路径初始化 false |
| `src/runtime/src/gc/safepoint.rs` | MODIFY | `request_gc_pause` 改返 `Option<GcPauseGuard>`；前置 CAS claim；失败 → park_until_idle + None；GcPauseGuard::drop 释放 collector_active；check_safepoint_slow 适配 Option |
| `src/runtime/src/gc/safepoint_tests.rs` | MODIFY | 既有测试改用 `if let Some(_) = request_gc_pause(&ctx)`；加 2 单测：`second_collector_falls_back_to_mutator_park` + `request_gc_pause_release_re-enables_next_collector` |
| `src/runtime/src/corelib/gc.rs` | MODIFY | `builtin_gc_collect` / `builtin_gc_force_collect` Option destructuring；docstring 注 best-effort 语义 |
| `src/runtime/tests/cross_thread_smoke.rs` | MODIFY | restore `auto_collect_triggers_via_safepoint_no_race` to 4 workers; add a new test `concurrent_gc_collect_callers_arbitrate` 验证 2 个显式 Std.GC.Collect 风格 caller 同时调，第二个 no-op |
| `docs/design/runtime/vm-architecture.md` | MODIFY | Safepoint v0 范围表"多 collector 仲裁"行 ⚠️ → ✅，描述 collector_active CAS 协议 |
| `docs/spec/changes/add-multi-collector-arbitration/` | NEW | 本 spec |

**只读引用**：

- `docs/spec/archive/2026-05-21-add-gc-safepoint-counter-throttling/tasks.md` 实施期发现 1（原死锁分析）
- `src/runtime/src/gc/safepoint.rs` 现 request_gc_pause / park_until_idle 实现

## Out of Scope

- **AOT mode collector arbitration**：AOT 还没实现
- **Distinguish failed-claim from would-block patterns**：v0 不区分；
  callers 直接用 Option None 处理
- **`try_request_gc_pause` 单独 API**：v0 合并到 request_gc_pause 单一 API
  返 Option；如果用户层未来需要"必须 collect"语义可加独立 spec

## Open Questions

- [ ] **CAS ordering**：`compare_exchange(false, true, AcqRel, Relaxed)` ——
      success AcqRel 给后续 phase 写入 happens-before；failure Relaxed
      因为我们 fallback park 自带 acquire fence。Design Decision 1
- [ ] **失败 collector 是否记录"我们要 collect"意图**：当 first collector
      完成后 second 是否再尝试？v0 简化：second 直接 fall back，下次
      pressure 触发会重新走流程。Design Decision 2
