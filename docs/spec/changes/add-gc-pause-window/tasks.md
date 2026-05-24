# Tasks: GC Pause-Time Rolling Window

> 状态：🟡 进行中（spec-only commit start）| 创建：2026-05-24 | 类型：vm

**总体策略**：tight extension to B5 (PauseHistogram). Adds rolling
`VecDeque<u64>` window + 2 new builtins. Pure observability, zero
GC algorithm change.

**总工作量估算**：~0.5-1 session / ~150 LOC.

## 进度概览

- [ ] 阶段 1-6: spec 文档
- [ ] 阶段 6.5: User 确认
- [ ] 阶段 7: 实施 P0
- [ ] 阶段 8: GREEN
- [ ] 阶段 9: 归档

## P0: PauseHistogram window + builtins (~0.5 session)

- [ ] P0.1 MODIFY `src/runtime/src/gc/types.rs`:
       - `PauseHistogram` derives drop `Copy` (VecDeque<u64> isn't Copy)
       - Add `recent_pauses: VecDeque<u64>` + `window_cap: usize` fields
       - Add `PAUSE_WINDOW_DEFAULT_CAP: usize = 1024` const
       - Add `pub fn pause_window_cap_from_env() -> usize`
       - Update `Default` to seed `recent_pauses` + `window_cap`
       - Update `record(pause_us)` to push_back + pop_front-if-full
       - `HeapStats` derives drop `Copy` (transitive on PauseHistogram)
- [ ] P0.2 MODIFY `src/runtime/src/gc/types_tests.rs`:
       - Update existing `default_is_empty` to also check `recent_pauses`
       - Update `record_saturates_on_overflow` if needed (still uses Copy?
         Verify)
       - New tests:
         - `default_has_empty_window_with_default_capacity`
         - `record_appends_to_window_in_chronological_order`
         - `window_evicts_oldest_at_capacity`
         - `pause_window_cap_from_env_clamps_and_falls_back`
- [ ] P0.3 MODIFY `src/runtime/src/gc/arc_heap_tests/pause_histogram.rs`:
       - Add `recent_pauses_visible_in_stats_snapshot`
       - Add `recent_pauses_does_not_exceed_capacity`
- [ ] P0.4 MODIFY `src/runtime/src/corelib/gc.rs`:
       - Add `builtin_gc_recent_pauses`
       - Add `builtin_gc_pause_window_capacity`
- [ ] P0.5 MODIFY `src/runtime/src/corelib/mod.rs`:
       - Append `("__gc_recent_pauses", gc::builtin_gc_recent_pauses)`
       - Append `("__gc_pause_window_capacity", gc::builtin_gc_pause_window_capacity)`
- [ ] P0.6 MODIFY `src/libraries/z42.core/src/GC/GC.z42`:
       - Add `extern long[] RecentPauses()`
       - Add `extern long PauseWindowCapacity()`
- [ ] P0.7 NEW `src/libraries/z42.io/tests/gc_pause_window.z42`:
       - `test_recent_pauses_length_grows_with_collects`
       - `test_pause_window_capacity_positive`
- [ ] P0.8 Audit `HeapStats: Copy` callers — grep + fix any binding
       that relies on Copy semantic. Likely zero (stats() returns by
       value, no `let s: HeapStats = *something` patterns expected)
- [ ] P0.9 `cargo --lib gc::` GREEN
- [ ] P0.10 `test-all.sh --scope=full` GREEN
- [ ] P0.11 Commit P0

## P1: gc.md docs + archive

- [ ] P1.1 MODIFY `docs/design/runtime/gc.md`:
       - Add "Rolling window" sub-subsection inside the existing
         "Pause histogram" section
       - Phase 路线表 add row for add-gc-pause-window
       - In B5 (add-gc-pause-histogram) Deferred sub-list, flip
         `add-gc-pause-window` "future → landed"
- [ ] P1.2 Archive 到 `docs/spec/archive/2026-05-24-add-gc-pause-window/`
- [ ] P1.3 Final `test-all.sh --scope=full` GREEN
- [ ] P1.4 Commit + push

## 备注

实施期发现入 commit message + 备注 section.
