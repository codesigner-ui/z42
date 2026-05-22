# Tasks: GC Random-Workload Stress Test

> 状态：🟢 已完成 | 创建：2026-05-22 | 归档：2026-05-22 | 类型：vm

**总体策略**：build on C1 validator. Hand-rolled SmallRng + seeded
deterministic op stream. 4 tests (3 modes + mode-switching) under
`cargo test`.

**总工作量估算**：~1-2 sessions / ~300 LOC. Small spec.

## 进度概览

- [x] 阶段 1-6: spec 文档
- [x] 阶段 6.5: User 确认
- [x] 阶段 7: 实施 P0
- [x] 阶段 8: GREEN
- [x] 阶段 9: 归档

## P0: 实施 stress generator + 4 tests (~1 session)

- [x] P0.1 新建 `gc/arc_heap_tests/stress.rs`：
       - `enum Op` (9 variants per design Op enum sketch)
       - `struct State` with bounded objects pool + pins tracking
       - `pick_op(rng, allow_mode_switch) -> Op` weighted distribution
       - `apply(op, heap, state, rng)` op interpreter
       - `gc_stress_run_seeded(seed, iters, mode)` driver
- [x] P0.2 注册 `stress` module 在 `arc_heap_tests/mod.rs`
- [x] P0.3 4 个测试：
       - `stress_seeded_stw_short` (seed 42)
       - `stress_seeded_concurrent_short` (seed 0x1234)
       - `stress_seeded_generational_short` (seed 0xC0DE)
       - `stress_seeded_mode_switching_short` (seed 0xBEEF, 3000 iters)
- [x] P0.4 Env var overrides：`Z42_STRESS_ITERS` / `Z42_STRESS_SEED`
- [x] P0.5 Coverage gate asserts (50 force_collects, 100 alloc_objects,
       100 field_sets minimum per 2000-iter run)
- [x] P0.6 cargo test gc::arc_heap::arc_heap_tests::stress GREEN
       (all 4 stress tests under default seeds, no invariant violation)
- [x] P0.7 commit

## P1: gc.md docs + archive (~0.5 session)

- [x] P1.1 `docs/design/runtime/gc.md` 加新 "Stress testing"
       subsection 紧跟 "Debug invariants" 后：
       - 设计目标 (stress complements unit + integration)
       - Hand-rolled SmallRng 选择理由
       - 4 个 tests 概览
       - 怎么 reproducible (env var Z42_STRESS_SEED)
       - 怎么 expand 覆盖 (env var Z42_STRESS_ITERS)
- [x] P1.2 Phase 表加 add-gc-stress-test 行
- [x] P1.3 C2 backlog entry: "future" → "landed"
- [x] P1.4 archive 到 `docs/spec/archive/YYYY-MM-DD-add-gc-stress-test/`
- [x] P1.5 final `test-all.sh --scope=full` GREEN
- [x] P1.6 commit + push

## 备注

**P0 实施期发现 latent bug** (2026-05-22)：第一次跑 4 个 stress tests
中 2 个失败（concurrent + mode-switching），panic 在 `entry_ref:
generation/alive mismatch — use-after-finalize`。Root cause:
add-concurrent-gc 的 `force_collect` 无 VmContext 路径走
`run_cycle_collection_stw`，但 ConcurrentMarkSweep 模式下 barrier
已经把 mark bits 设了 + mark_queue 累积。STW path 假设 clean slate
→ mark_phase 见到 already-marked entries 时 CAS 失败 → 不 trace
children → sweep 保留 marked 但 unreachable 的 entry → 其 slot 子
对象被 swept → 留下 stale Value in slot → 下次 collect mark_phase 经
slot 触发 use-after-finalize panic。

**Fix**：`run_cycle_collection_stw` 入口加 defensive
`reset_all_marks_in_regions()` + `mark_queue.lock().clear()`。
确保 STW path 不依赖 caller state，clean slate 保证。

**验证 stress 价值**：第一次跑就 catch 到 bug。Stress 是值得的。

## 后续 spec 依赖关系

| 后续 spec | 依赖本 spec 的什么 |
|----------|-------------------|
| `add-gc-coverage-guided-fuzz` | 升级到 libFuzzer + coverage feedback |
| `add-gc-stress-multi-thread` | 多线程 stress 版本（基于 cross_thread_smoke 模式）|
| `add-gc-stress-ci` | CI 长时序 stress 跑（millions of iters）|
| `add-gc-property-shrinking` | proptest 引入 + failure case shrinking |
