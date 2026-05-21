# Tasks: Mark-sweep GC

> 状态：🟢 已完成 | 创建：2026-05-21 | 归档：2026-05-21 | 类型：vm

**实施完成总结**：spec-only commit → P1 mark infrastructure → P2 sweep +
side-by-side → P3 switchover + cleanup → P4 benchmark report → P5 archive。
P4 实测 3 个 workload 全部正收益（cycle-heavy 1.39×、reachable 1.28×）。
所有 P-commit 独立 GREEN 通过 `test-all.sh --scope=full`。

## 进度概览

### Spec phase（本 commit）
- [x] 阶段 1: proposal.md
- [x] 阶段 2: specs/mark-sweep/spec.md
- [x] 阶段 3: design.md
- [x] 阶段 4: tasks.md（本文件）
- [x] 阶段 5: User 阶段 6.5 gate 通过
- [x] 阶段 6: spec 文档 commit + push

### Implementation phase（后续 sessions，记录在此供未来跟踪）

**P1: Mark infrastructure（~2 天）**
- [x] P1.1 `gc/refs.rs` GcAllocation 加 `marked: AtomicU8`
- [x] P1.2 `metadata/types.rs` Value::trace_children inherent method
- [x] P1.3 `gc/arc_heap.rs` 加 `mark_phase` 新函数（与旧 mark_reachable_set 并行）
- [x] P1.4 新单测 `mark_phase_visits_reachable_only` 验证 mark BFS
- [x] P1.5 cargo --lib gc::arc_heap GREEN
- [x] P1.6 commit

**P2: Sweep + side-by-side validation（~2 天）**
- [x] P2.1 `gc/arc_heap.rs` 加 `sweep_phase` —— 扫 registry + reset marks
- [x] P2.2 加 `collect_cycles_mark_sweep` 实验函数（feature flag 切换）
- [x] P2.3 跑 side-by-side：现有 cycle_collection 单测中加 diff 验证：
        mark-sweep 结果 == trial-deletion 结果（freed_bytes 可能差，但
        survived objects 必须一致）
- [x] P2.4 cargo --lib GREEN
- [x] P2.5 commit（实验函数仍在；旧路径仍 default）

**P3: Switchover + delete trial-deletion（~3 天）**
- [x] P3.1 `collect_cycles` 默认走 mark-sweep（`run_cycle_collection` 改为 mark+sweep 2 行）
- [x] P3.2 删除 `mark_reachable_set` + trial-deletion `tentative` 计数逻辑；
        `break_cycle_value` **保留**（被 `sweep_phase` 复用断不可达对象的内部引用），
        `GcRef::strong_count` 删除（不再需要 RC-based 外部引用计数）
- [x] P3.3 迁移 3 个语义切换敏感的单测：
        - `cycle_with_external_user_ref_is_not_broken_yet` → 显式 `pin_root`
        - `unrelated_alive_object_is_not_affected_by_collect` → 显式 `pin_root`
        - `strict_oom_off_by_default_no_rejection` → `pause()` / `resume()` 屏蔽 auto-collect
- [x] P3.4 cargo --lib gc:: 119/119 GREEN；stdlib + cross_thread_smoke GREEN
- [x] P3.5 docs/design/runtime/vm-architecture.md 同步：A2 从"未来"
        转"已落地"；Phase 表新增 mark-sweep 行；trial-deletion 注释退到 historical 段
- [x] P3.6 commit (3d7438a5)

**P4: Performance benchmark + report（~2 天）**
- [x] P4.1 `src/runtime/benches/gc_cycle_bench.rs` 新建 —— 3 类 workload
        (`cycle_heavy_100` / `shallow_tree_1k` / `large_array_10k`)，criterion
        `iter_batched` 把 heap 构造排除在计时外
- [x] P4.2 跑 baseline (commit `cbb377c4` P2 via git worktree, trial-deletion
        默认) + 新版 (mark-sweep)；3 workload × 2 算法 = 6 个 measurement
- [x] P4.3 撰写报告入 design.md "Benchmark Results (P4)" 段；结论：
        cycle-heavy 1.39×、reachable-heavy 1.28×，全部正收益
- [x] P4.4 commit（report + benchmark code）(9f8f4691)

**P5: Final archive**
- [x] P5.1 mv spec → archive（`docs/spec/archive/2026-05-21-add-mark-sweep-collector/`）
- [x] P5.2 verify CI GREEN（`test-all.sh --scope=full` 6 stages all green）
- [x] P5.3 commit

## 备注

（实施期发现写在 P1-P4 commit message + design.md 备注 section）

## 后续 spec 依赖关系

| 后续 spec | 何时开 |
|----------|-------|
| `add-write-barriers` | A3 dep；mark-sweep 落地后 |
| `add-concurrent-gc` (A4) | mark-sweep 落地 + write barriers 评估后 |
| `add-custom-allocator` (A1) | A2 落地后；独立性能轨道 |
