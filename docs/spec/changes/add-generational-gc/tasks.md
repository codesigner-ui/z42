# Tasks: Generational Mark-Sweep GC

> 状态：🟡 进行中（spec-only commit start）| 创建：2026-05-22 | 类型：vm

**总体策略**：基于已落地的 `add-custom-allocator` Region 基础 + 已有
write barrier call-site wiring，加 generational 分代。logical
promotion（gen_age 字段，不物理移动 entry，保 `GcRef` NonNull 契约）。

**总工作量估算**：~8-12 个 work session / ~1200-1500 LOC（Explore agent
推算，已扣除前置 spec 已就绪的部分）。

## 进度概览

- [ ] 阶段 1: 探索（已完成）
- [ ] 阶段 2-6: spec 文档（proposal / spec / design / tasks，本文件）
- [ ] 阶段 6.5: User 确认
- [ ] 阶段 7: 实施 P0-P4
- [ ] 阶段 8: 每 phase + 最终 GREEN
- [ ] 阶段 9: 归档

## P0: Region<T> gen_age + young_list + card 基础设施 (~2 sessions)

- [ ] P0.1 `RegionEntry<T>` 加 `gen_age: AtomicU8` 字段（默认 0）
- [ ] P0.2 `Region<T>` 加 `young_list: Vec<(u16, u16)>` 字段
- [ ] P0.3 `Region<T>` 加 `card_dirty: Vec<u32>` 字段（per-chunk bit）
- [ ] P0.4 `Region::alloc` 修改：push 到 `young_list`；free-list pop 路径
       也 reset gen_age 到 0 + push young_list（重用槽即新对象）
- [ ] P0.5 `Region::promote(handle)` 实现：gen_age++，到达阈值 (2)
       后从 young_list 移除
- [ ] P0.6 `Region::iterate_young(visit)` —— 走 young_list 直接 index
       到 chunks（O(young) 不是 O(total)）
- [ ] P0.7 `Region::mark_card_dirty(chunk_idx)` —— bit set in `card_dirty`
- [ ] P0.8 `Region::is_card_dirty(chunk_idx)` + `Region::clear_card_dirty()`
- [ ] P0.9 `Region::iterate_dirty_cards(visit)` —— yields all entries
       in dirty chunks (不 filter gen_age，让 minor GC 自己判断)
- [ ] P0.10 `Region::tombstone_via_entry` 同步移除 entry 在 young_list
       中的位置（若是 young）
- [ ] P0.11 region_tests.rs 加测试：
       - `alloc_pushes_to_young_list`
       - `promote_increments_gen_age`
       - `promote_at_threshold_removes_from_young_list`
       - `mark_card_dirty_sets_bit_at_correct_offset`
       - `iterate_dirty_cards_yields_chunk_entries`
       - `clear_card_dirty_resets_all_bits`
       - `tombstone_via_entry_removes_young_entry`
- [ ] P0.12 `GcRef::gen_age()` accessor 在 refs.rs（atomic load）
- [ ] P0.13 cargo --lib + region_tests + refs GREEN
- [ ] P0.14 commit

## P1: GcMode::GenerationalMarkSweep + barrier override (~2 sessions)

- [ ] P1.1 `gc/mode.rs` 加 `GcMode::GenerationalMarkSweep` variant
- [ ] P1.2 `GcMode::from_env()` 解析 `Z42_GC_MODE=generational`
- [ ] P1.3 `GcMode::from_u8` 加新 discriminant 映射
- [ ] P1.4 ArcMagrGC write_barrier_field override 加 GenerationalMarkSweep
       分支：检查 `owner.gen_age >= 1 && new.gen_age == 0` → mark card
- [ ] P1.5 同上 write_barrier_array_elem
- [ ] P1.6 单测：
       - `generational_mode_set_observable`
       - `barrier_marks_card_on_cross_gen_write`
       - `barrier_no_op_on_young_to_young_write`
       - `barrier_no_op_on_old_to_old_write`
       - `barrier_no_op_in_stw_mode_even_under_cross_gen`
- [ ] P1.7 行为 parity 验证：StwMarkSweep 模式下所有现有 174 GC 测试
       通过（young_list bookkeeping inert 不影响）
- [ ] P1.8 commit

## P2: Minor GC 分发 + 老→新 root 扫描 + promotion (~3 sessions)

- [ ] P2.1 `ArcMagrGC` 实现 `collect_minor()`：
       - request_gc_pause (STW)
       - snapshot pinned + external roots
       - iterate_dirty_cards 加入 root（再 filter old → 持有 young 的引用）
       - mark BFS（只 mark reachable from roots）
       - sweep_phase_young_only（walk young_list，不 marked 的 tombstone）
       - promote_survivors（survive 的 young 增 gen_age）
       - clear_card_dirty
- [ ] P2.2 `collect_cycles_with_context` 加 `GenerationalMarkSweep` 分支：
       默认走 collect_minor；触发条件命中 major 走 collect_major
- [ ] P2.3 `sweep_phase_young_only` 实现：iterate young_list，
       tombstone unmarked + fire finalizer + remove from young_list
- [ ] P2.4 promote_survivors 实现：iterate young_list，对每个 alive +
       marked 的调 `region.promote(handle)`
- [ ] P2.5 单测：
       - `minor_only_scans_young_list_not_all_entries`
       - `minor_promotes_after_2_survivals`
       - `minor_does_not_touch_unrooted_old_entries`
       - `cross_gen_target_survives_minor_via_card_marking`
       - `intra_old_write_no_marker_target_not_re_rooted`
       - `tombstoned_young_entries_removed_from_young_list`
       - `minor_clears_card_dirty_after_complete`
- [ ] P2.6 commit

## P3: Major GC + escalation + auto-collect 触发 (~2 sessions)

- [ ] P3.1 `collect_major()` 实现：basically existing
       `run_cycle_collection_stw` rebadged + 显式 clear_card_dirty 在结尾
- [ ] P3.2 `should_escalate_to_major(minor_survival_rate: f32) -> bool`
       heuristic：>= 0.75 触发 major
- [ ] P3.3 `collect_cycles_with_context` 在 minor 完成后检查 escalation
- [ ] P3.4 `maybe_auto_collect`/`needs_auto_collect` 加 young pressure
       判断：young_list capacity 满足条件触发 minor；否则按既有 old
       pressure 触发 major
- [ ] P3.5 env var `Z42_GC_TENURE` (promotion threshold, default 2)
       + `Z42_GC_MINOR_THRESHOLD` (escalation, default 0.75) 解析
- [ ] P3.6 单测：
       - `minor_escalates_to_major_when_young_dense`
       - `auto_collect_young_pressure_triggers_minor_not_major`
       - `auto_collect_total_pressure_triggers_major_directly`
       - `tenure_threshold_env_var_overrides_default`
- [ ] P3.7 集成测试：`Z42_GC_MODE=generational test-all.sh --scope=full`
       6 stages GREEN
- [ ] P3.8 commit

## P4: Bench + docs + archive (~1-2 sessions)

- [ ] P4.1 `gc_cycle_bench.rs` 扩 generational workloads：
       - `gc_minor/pure_churn_100k` —— 全 young alloc/die，无 old
       - `gc_minor/mixed_workload_10k_old_100k_young` —— 长寿 + 短命
       - `gc_major/large_old_gen_with_periodic_minor` —— 大 old
       - 对比基线 (StwMarkSweep mode)
- [ ] P4.2 报告入 design.md "Benchmark Results" 段
- [ ] P4.3 `docs/design/runtime/vm-architecture.md`：
       - "GC heap backing" 章扩 "Generational layout" 子段
       - 新章节 "Generational collection protocol"
       - Phase 表加 add-generational-gc 行
       - A3 entry 从 "future" 转 "landed"
- [ ] P4.4 archive 到 `docs/spec/archive/YYYY-MM-DD-add-generational-gc/`
- [ ] P4.5 final `test-all.sh --scope=full` 三 mode GREEN (stw, concurrent, generational)
- [ ] P4.6 commit

## 备注

实施期发现写在每个 P-commit message + 必要时回本文件 备注 节。

按 feedback_problem_first_then_defer：spec 没覆盖的设计决策必须停下汇报。

## 后续 spec 依赖关系

| 后续 spec | 何时开 |
|----------|-------|
| `add-concurrent-generational` | A3 + A4 联合优化；若 minor pause 仍是瓶颈 |
| `add-multi-generation` | 3+ 代分层；若 promotion churn 实测瓶颈 |
| `add-adaptive-promotion` | 自适应 N 阈值；若 workload 多样性大 |
| `add-per-thread-young-arena` | 若 young alloc lock 实测竞争 |
| `add-physical-promotion` | 若 cache locality 实测瓶颈，并准备好 GcRef 重写 |
| `add-card-granularity-tuning` | 若 minor 扫描成本是瓶颈 |
| `add-mmtk-binding` (D1) | A3 之后；MMTk 多 collector 选项基础就绪 |
