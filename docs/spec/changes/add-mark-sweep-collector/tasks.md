# Tasks: Mark-sweep GC

> 状态：🟡 进行中（spec-only）| 创建：2026-05-21 | 类型：vm

**这是 spec-only commit**：proposal + design + tasks 文档 land 之后，
实施 phase 拆 P1-P4 单独 commit，每个独立 GREEN。预估总工作量 ~9 天
（实施 phase 不属于本 spec 验收）。

## 进度概览

### Spec phase（本 commit）
- [ ] 阶段 1: proposal.md
- [ ] 阶段 2: specs/mark-sweep/spec.md
- [ ] 阶段 3: design.md
- [ ] 阶段 4: tasks.md（本文件）
- [ ] 阶段 5: User 阶段 6.5 gate 通过
- [ ] 阶段 6: spec 文档 commit + push

### Implementation phase（后续 sessions，记录在此供未来跟踪）

**P1: Mark infrastructure（~2 天）**
- [ ] P1.1 `gc/refs.rs` GcAllocation 加 `marked: AtomicU8`
- [ ] P1.2 `metadata/types.rs` Value::trace_children inherent method
- [ ] P1.3 `gc/arc_heap.rs` 加 `mark_phase` 新函数（与旧 mark_reachable_set 并行）
- [ ] P1.4 新单测 `mark_phase_visits_reachable_only` 验证 mark BFS
- [ ] P1.5 cargo --lib gc::arc_heap GREEN
- [ ] P1.6 commit

**P2: Sweep + side-by-side validation（~2 天）**
- [ ] P2.1 `gc/arc_heap.rs` 加 `sweep_phase` —— 扫 registry + reset marks
- [ ] P2.2 加 `collect_cycles_mark_sweep` 实验函数（feature flag 切换）
- [ ] P2.3 跑 side-by-side：现有 cycle_collection 单测中加 diff 验证：
        mark-sweep 结果 == trial-deletion 结果（freed_bytes 可能差，但
        survived objects 必须一致）
- [ ] P2.4 cargo --lib GREEN
- [ ] P2.5 commit（实验函数仍在；旧路径仍 default）

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
- [ ] P3.6 commit

**P4: Performance benchmark + report（~2 天）**
- [ ] P4.1 `bench/microbench/gc_cycle.rs`（如不存在则新建）—— 3 类
        workload (cycle-heavy / shallow-tree / large-array)
- [ ] P4.2 跑 baseline (trial-deletion checkout) + 新版 (mark-sweep)
- [ ] P4.3 撰写报告入 design.md "Benchmark results" 段
- [ ] P4.4 commit（report + benchmark code）

**P5: Final archive**
- [ ] P5.1 mv spec → archive
- [ ] P5.2 verify CI GREEN
- [ ] P5.3 commit

## 备注

（实施期发现写在 P1-P4 commit message + design.md 备注 section）

## 后续 spec 依赖关系

| 后续 spec | 何时开 |
|----------|-------|
| `add-write-barriers` | A3 dep；mark-sweep 落地后 |
| `add-concurrent-gc` (A4) | mark-sweep 落地 + write barriers 评估后 |
| `add-custom-allocator` (A1) | A2 落地后；独立性能轨道 |
