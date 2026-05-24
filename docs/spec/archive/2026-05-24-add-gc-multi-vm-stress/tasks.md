# Tasks: GC Multi-Heap Isolation Stress (C3)

> 状态：🟢 已完成 | 创建：2026-05-24 | 归档：2026-05-24 | 类型：test
> 变更说明：扩展 C2 stress driver 到多 `ArcMagrGC` 实例并行场景，
> 验证 GC 状态跨 heap 实例零互相污染。
> 原因：嵌入用户可能创建多 VM；Phase 3 多实例隔离设计需要兜底测试。
> 文档影响：`docs/design/runtime/gc.md` C3 backlog 条目 "future → landed" +
> Phase 路线表加 C3 行。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/gc/arc_heap_tests/multi_vm.rs` | NEW | 多 heap 并行隔离测试 |
| `src/runtime/src/gc/arc_heap_tests/mod.rs` | MODIFY | 注册 `multi_vm` module |
| `docs/design/runtime/gc.md` | MODIFY | C3 entry "future → landed"；Phase 表加行 |

**只读引用**：

- `src/runtime/src/gc/arc_heap_tests/stress.rs` — 参考已有 stress 模式
- `src/runtime/src/gc/arc_heap.rs` — `ArcMagrGC::default` / `alloc_object` /
  `force_collect` / `set_mode` / `stats` API
- `src/runtime/src/gc/arc_heap_tests/send_sync.rs` — 验证 `Box<dyn MagrGC>`
  已是 `Send + Sync`，可跨线程

## Out of Scope

- 多 VmContext **共享同一 VmCore**（同 heap）场景 — 不是 C3 测试目标
- 真正 concurrent collect 跨线程协调（已由 `concurrent_mark` 测试覆盖）
- Multi-thread mutator on same heap（C2 `stress_seeded_concurrent_short`
  + `concurrent_mark` 覆盖）

## 测试目标

每个测试验证一类隔离不变式：

1. **两 heap 状态互不感染**：在 h1 上 alloc + collect，h2 的
   `stats().allocations / gc_cycles / used_bytes` 全 0
2. **N 线程各自 heap 并行 stress**：每线程独立 seed，独立 mode，独立
   ops 序列；join 后每个 heap 的 stats 单独合理（allocations ≥ workload
   阈值，gc_cycles ≥ 1），互不串扰
3. **每 heap 选不同 GcMode 不污染**：3 线程分别用
   `StwMarkSweep` / `ConcurrentMarkSweep` / `GenerationalMarkSweep`；
   join 后 invariants 各 heap 仍 ✅（debug build：`debug_validate_invariants`
   在每次 collect 末尾自动跑）

## 任务

- [x] 1.1 NEW `src/runtime/src/gc/arc_heap_tests/multi_vm.rs`：
       - `two_heaps_alloc_no_cross_contamination`
       - `concurrent_threads_independent_heaps_stw`
       - `concurrent_threads_independent_heaps_generational`
       - `multi_heaps_mixed_modes`
       - `pause_histograms_per_heap_isolation` (B5 cross-check)
- [x] 1.2 MODIFY `arc_heap_tests/mod.rs` — `mod multi_vm;`
- [x] 1.3 `cargo test --lib gc::` GREEN（含新 5 tests, 240 total)
- [x] 1.4 `test-all.sh --scope=full` GREEN
- [x] 1.5 MODIFY `docs/design/runtime/gc.md`：C3 backlog "future → landed"
       + Phase 路线表加一行
- [x] 1.6 Archive 到 `docs/spec/archive/2026-05-24-add-gc-multi-vm-stress/`
- [x] 1.7 Commit + push

## 备注

测试目标是隔离 — 仅创建多 `ArcMagrGC` 直接调 API，不需要起完整 VmContext /
VmCore（heap 已是 `Send + Sync` 验证过）。比 C2 简单，~150 LOC。
