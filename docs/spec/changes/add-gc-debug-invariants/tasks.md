# Tasks: GC Debug-Only Invariant Checks

> 状态：🟡 进行中（spec-only commit start）| 创建：2026-05-22 | 类型：vm

**总体策略**：debug-only safety net for GC subsystem. `cfg(debug_assertions)`-gated;
release builds compile out entirely.

**总工作量估算**：~2-3 sessions / ~600 LOC. Small spec; mostly testing.

## 进度概览

- [ ] 阶段 1: 探索（已完成；前置 = A1/A2/A3/A4 全部落地，invariants 来自 4 个 spec design 段）
- [ ] 阶段 2-6: spec 文档（proposal / spec / design / tasks，本文件）
- [ ] 阶段 6.5: User 确认
- [ ] 阶段 7: 实施 P0-P1
- [ ] 阶段 8: 每 phase + 最终 GREEN
- [ ] 阶段 9: 归档

## P0: Region<T>::validate() + Violation enum (~1 session)

- [ ] P0.1 `gc/region.rs` 加 `#[cfg(debug_assertions)] pub enum Violation`
       (8 variants per design D5)
- [ ] P0.2 `Region<T>::validate(&self) -> Result<(), Violation>` 实现
       per design.md "Region::validate sketch"
- [ ] P0.3 region_tests.rs 加测试：
       - `validate_healthy_region_passes`
       - `validate_detects_old_in_young_list`
       - `validate_detects_young_not_in_list`
       - `validate_detects_duplicate_in_young_list`
       - `validate_detects_alive_in_free_list`
       - `validate_detects_location_mismatch`
       - `validate_card_dirty_length_mismatch`
- [ ] P0.4 cargo --lib gc::region GREEN
- [ ] P0.5 commit

## P1: ArcMagrGC::debug_validate_invariants + integration + tests (~1 session)

- [ ] P1.1 `gc/arc_heap.rs` 加 `debug_validate_invariants` per design
       "ArcMagrGC::debug_validate_invariants" sketch
- [ ] P1.2 `collect_cycles` 尾部加 `#[cfg(debug_assertions)] self.debug_validate_invariants()`
- [ ] P1.3 `collect_cycles_with_context` 各 mode 分支尾部加同上
- [ ] P1.4 单测在 `arc_heap_tests/invariants.rs`:
       - `healthy_heap_passes_validation`
       - `stale_mark_after_sweep_detected` (manually keep mark=1 + try
         to validate, expect panic)
       - `stale_mark_queue_detected`
       - `concurrent_mode_collect_validates_cleanly`
       - `generational_mode_collect_validates_cleanly`
- [ ] P1.5 集成验证：
       - `cargo test --lib gc::` 全 GREEN（现有 4 个算法不违反任何 invariant）
       - `Z42_GC_MODE=concurrent test-all.sh --scope=runtime` GREEN
       - `Z42_GC_MODE=generational test-all.sh --scope=runtime` GREEN
       - `test-all.sh --scope=full` 6 stages GREEN
- [ ] P1.6 commit

## P2: Docs + archive (~0.5 session)

- [ ] P2.1 `docs/design/runtime/gc.md` 加新 "Debug invariants" subsection：
       - 列出 8 个 Violation 变种 + 每个的检测时机
       - 何时调用 `debug_validate_invariants`
       - 如何添加新 invariant（开发指南）
       - cfg(debug_assertions) 行为说明
- [ ] P2.2 archive 到 `docs/spec/archive/YYYY-MM-DD-add-gc-debug-invariants/`
- [ ] P2.3 final `test-all.sh --scope=full` GREEN
- [ ] P2.4 commit + push

## 备注

实施期发现写在每个 P-commit message + 必要时回本文件 备注 节。

## 后续 spec 依赖关系

| 后续 spec | 依赖本 spec 的什么 |
|----------|-------------------|
| `add-gc-property-based-stress` (C2) | validate() primitive — random workload 跑完后调用 |
| `add-gc-runtime-checks` | 同样的 invariant 集合，feature-flag 控制开启 |
| 任何未来 GC 改动 | invariants 作为回归测试网 — 新算法不得违反已有 invariants（或显式更新 invariants） |
