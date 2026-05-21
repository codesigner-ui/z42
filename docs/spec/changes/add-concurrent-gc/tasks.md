# Tasks: Concurrent Mark + Selectable GC Mode

> 状态：🟡 进行中（spec-only commit start）| 创建：2026-05-22 | 类型：vm

**总体策略**：landed STW mark-sweep + write barriers + JIT safepoint
打下的地基让 concurrent mark 成为可控加项。开关 (`GcMode` enum + env
var) 让默认行为零变化，concurrent 路径 opt-in；每个 phase 独立 GREEN
+ commit + revertible。

**总工作量估算**：~7-10 个 work session（不是 7-10 天 — 是 7-10 个聚焦
session）。spec 阶段单独 commit；P0-P7 实施 phase 各独立 commit。

## 进度概览

- [ ] 阶段 1: 探索（已完成，map 见 design.md "Architecture" + spec 摸 安全 检查）
- [ ] 阶段 2-6: spec 文档（proposal / spec / design / tasks，本文件）
- [ ] 阶段 6.5: User 确认
- [ ] 阶段 7: 实施 P0-P7
- [ ] 阶段 8: 每 phase + 最终 GREEN
- [ ] 阶段 9: 归档

## P0: GcMode enum + mode field + dispatch stub (~1 session)

- [ ] P0.1 `gc/mod.rs`（或新 `gc/mode.rs`）加 `GcMode` enum
       (`StwMarkSweep` / `ConcurrentMarkSweep`)
- [ ] P0.2 `GcMode::from_env()` 解析 `Z42_GC_MODE` env var
- [ ] P0.3 `MagrGC` trait 加 `mode() -> GcMode` 默认实现返回 `StwMarkSweep`
       (其它非 Arc heap impls 自动兼容)
- [ ] P0.4 `MagrGC` trait 加 `set_mode(GcMode)` 默认实现 panic
       (只 Arc heap 真支持)
- [ ] P0.5 `ArcMagrGC` 加 `mode: AtomicU8` 字段；`set_mode` / `mode` 实现
- [ ] P0.6 `ArcMagrGC::new()` 从 `GcMode::from_env()` 初始化
- [ ] P0.7 `run_cycle_collection` 加 `match self.mode()` dispatch；两个 arm
       都走现有 STW 路径（concurrent arm 暂时也走 STW，证明 dispatch 没
       破坏既有行为）
- [ ] P0.8 单测：`mode_default_is_stw_mark_sweep` /
       `set_mode_changes_observable_mode` /
       `env_var_z42_gc_mode_concurrent_selects_concurrent` /
       `env_var_invalid_falls_back_to_stw`
- [ ] P0.9 `test-all.sh --scope=full` GREEN (默认 mode 行为零变化)
- [ ] P0.10 commit `feat(gc): add-concurrent-gc P0 — GcMode enum + dispatch stub`

## P1: GcPhase enum extension (~0.5 session)

- [ ] P1.1 `gc/safepoint.rs` `GcPhase` 加 `ConcurrentMarking` variant
       (在 `Requested` 和 `Marking` 之间)
- [ ] P1.2 文档化 phase 状态机：
       - STW 路径：Idle → Requested → Marking → Sweeping → Idle (unchanged)
       - Concurrent 路径：Idle → Requested → ConcurrentMarking → Marking
         (handshake) → Sweeping → Idle
- [ ] P1.3 `check_safepoint` 内部 match：`ConcurrentMarking` 时 mutator
       **不** park（这是新行为；其它 phase 都 park 不变）
- [ ] P1.4 单测：`gc_phase_concurrent_marking_does_not_park_mutators`
       (使用 mock VmContext 验证 parked_count 不增长)
- [ ] P1.5 GREEN + commit

## P2: mark_queue + snapshot helpers (~1 session)

- [ ] P2.1 `arc_heap.rs` 加 `mark_queue: Mutex<Vec<Value>>` 字段
- [ ] P2.2 helper `mark_if_unmarked(v: &Value) -> bool` (mirror of
       Value variant match — 与 mark_phase 的 BFS 推送共用)
- [ ] P2.3 helper `snapshot_roots_into_mark_queue()` — STW 阶段填充 queue
       (调用 pinned_roots + external_root_scanner)
- [ ] P2.4 单测：`snapshot_roots_populates_queue_with_marked_roots` 验证
       roots 被 mark + 入 queue
- [ ] P2.5 GREEN + commit

## P3: Barrier override branches on mode (~0.5 session)

- [ ] P3.1 `arc_heap.rs` `write_barrier_field` / `write_barrier_array_elem`
       override 加 `match self.mode()` 分支：
       - `StwMarkSweep` → no-op (current behavior preserved)
       - `ConcurrentMarkSweep` → `if mark_if_unmarked(new) { mark_queue.push(new.clone()) }`
- [ ] P3.2 barrier override 加 `debug_assert!(new.is_heap_ref())`
       验证 caller 契约
- [ ] P3.3 单测：`barrier_no_op_in_stw_mode` (现有 6 个 write_barriers
       测试在 default mode 下仍 GREEN)
- [ ] P3.4 单测：`barrier_shades_new_value_in_concurrent_mode` 验证
       mode==Concurrent 下 mark 命中 + queue push
- [ ] P3.5 单测：`barrier_idempotent_on_already_marked_in_concurrent_mode`
       (验证 CAS 失败时不重复 push)
- [ ] P3.6 GREEN + commit

## P4: Concurrent mark loop (~2 sessions, 核心 phase)

- [ ] P4.1 实现 `run_cycle_collection_concurrent` 函数：
       - 初始 STW pause → snapshot_roots_into_mark_queue → release
       - phase = ConcurrentMarking
       - loop drain queue (单线程；当前 collector thread 跑 BFS，mutators 并发)
       - queue empty → 请求 STW handshake
       - phase = Marking → drain residual → phase = Sweeping → sweep_phase
       - release
- [ ] P4.2 `run_cycle_collection` 改为 `match self.mode()` dispatch
       (P0 已 stub，这里填入 concurrent arm 的 真实实现)
- [ ] P4.3 单测：`concurrent_collect_preserves_pinned_chain`
       (single-thread sim — collect 当前线程跑，验证 reachable 不被错误清掉)
- [ ] P4.4 单测：`concurrent_collect_frees_unreachable_cycle`
       (验证基本 cycle GC 正确性)
- [ ] P4.5 单测：`concurrent_collect_with_barrier_marks_new_objects`
       (在 collect 中间 simulated mutator write，验证 barrier shading
       让 new 对象不被错误清掉)
- [ ] P4.6 单测：`termination_handshake_drains_final_burst`
       (在 STW handshake 前 simulated burst write，验证 residual 被 drain)
- [ ] P4.7 单测：`mode_switch_during_collect_uses_original_mode`
       (start STW collect → set_mode(Concurrent) mid-flight → 当前 collect
       仍 STW 完成，next collect 才 concurrent)
- [ ] P4.8 GREEN + commit

## P5: Multi-threaded stress (~1 session)

- [ ] P5.1 现有 `cross_thread_smoke.rs` 跑一遍：`Z42_GC_MODE=concurrent
       cargo test cross_thread`，验证全 GREEN
- [ ] P5.2 新加 `arc_heap_tests/concurrent_mark_stress.rs`：
       - 多 mutator 线程持续 alloc + field_set，1 个 collector 周期性
         触发 concurrent collect；跑 N 秒后 assert 无 panic / 无内存爆涨
- [ ] P5.3 `test-all.sh --scope=full` 跑两次：
       - default mode → GREEN
       - `Z42_GC_MODE=concurrent` → GREEN
- [ ] P5.4 GREEN + commit

## P6: Bench + report (~1 session)

- [ ] P6.1 `benches/gc_cycle_bench.rs` 扩 3 个 concurrent 变种：
       - `concurrent_cycle_heavy_100`
       - `concurrent_shallow_tree_1k`
       - `concurrent_large_array_10k`
- [ ] P6.2 量化指标：
       - STW handshake 时间（短 pause 部分）
       - Concurrent mark 总耗时（背景，不阻塞 mutator）
       - 与 P4 落地的 STW 总耗时对比
- [ ] P6.3 撰写报告入 design.md "Benchmark Results (P6)" 段
- [ ] P6.4 commit

## P7: Docs + archive (~0.5 session)

- [ ] P7.1 `docs/design/runtime/vm-architecture.md`：
       - 新增 "GC mode selection" 章节：介绍 GcMode 枚举、env var、
         API、fallback 规则
       - 新增 "Concurrent mark protocol" 子章节：tricolor 不变量、
         barrier 行为、phase 状态机、终止 handshake
       - Phase 表新增 `add-concurrent-gc` 行
       - A4 entry 从 "future" 转 "landed 2026-MM-DD"
- [ ] P7.2 archive 到 `docs/spec/archive/YYYY-MM-DD-add-concurrent-gc/`
- [ ] P7.3 final `test-all.sh --scope=full` 双 mode GREEN
- [ ] P7.4 archive commit + push

## 备注

实施期发现写在每个 P-commit message + 必要时回本文件 备注 节。

按 feedback_problem_first_then_defer：遇到 spec 没覆盖的设计决策必须停下汇报。

## 后续 spec 依赖关系

| 后续 spec | 何时开 |
|----------|-------|
| `add-concurrent-sweep` | concurrent mark 落地稳定后，若 sweep STW pause 成为瓶颈 |
| `add-satb-barrier` | 若 incremental update 终止 handshake 是瓶颈 |
| `add-work-stealing-mark` | 若单 mark thread 是瓶颈 |
| `add-lockfree-mark-queue` | 若 `Mutex<Vec>` 竞争是瓶颈 |
| `add-generational-gc` (A3) | concurrent mark 落地 + write barriers 真用上 + A1 custom-allocator 落地 |
