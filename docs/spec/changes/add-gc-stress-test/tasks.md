# Tasks: GC Random-Workload Stress Test

> 状态：🟡 进行中（spec-only commit start）| 创建：2026-05-22 | 类型：vm

**总体策略**：build on C1 validator. Hand-rolled SmallRng + seeded
deterministic op stream. 4 tests (3 modes + mode-switching) under
`cargo test`.

**总工作量估算**：~1-2 sessions / ~300 LOC. Small spec.

## 进度概览

- [ ] 阶段 1-6: spec 文档
- [ ] 阶段 6.5: User 确认
- [ ] 阶段 7: 实施 P0
- [ ] 阶段 8: GREEN
- [ ] 阶段 9: 归档

## P0: 实施 stress generator + 4 tests (~1 session)

- [ ] P0.1 新建 `gc/arc_heap_tests/stress.rs`：
       - `enum Op` (9 variants per design Op enum sketch)
       - `struct State` with bounded objects pool + pins tracking
       - `pick_op(rng, allow_mode_switch) -> Op` weighted distribution
       - `apply(op, heap, state, rng)` op interpreter
       - `gc_stress_run_seeded(seed, iters, mode)` driver
- [ ] P0.2 注册 `stress` module 在 `arc_heap_tests/mod.rs`
- [ ] P0.3 4 个测试：
       - `stress_seeded_stw_short` (seed 42)
       - `stress_seeded_concurrent_short` (seed 0x1234)
       - `stress_seeded_generational_short` (seed 0xC0DE)
       - `stress_seeded_mode_switching_short` (seed 0xBEEF, 3000 iters)
- [ ] P0.4 Env var overrides：`Z42_STRESS_ITERS` / `Z42_STRESS_SEED`
- [ ] P0.5 Coverage gate asserts (50 force_collects, 100 alloc_objects,
       100 field_sets minimum per 2000-iter run)
- [ ] P0.6 cargo test gc::arc_heap::arc_heap_tests::stress GREEN
       (all 4 stress tests under default seeds, no invariant violation)
- [ ] P0.7 commit

## P1: gc.md docs + archive (~0.5 session)

- [ ] P1.1 `docs/design/runtime/gc.md` 加新 "Stress testing"
       subsection 紧跟 "Debug invariants" 后：
       - 设计目标 (stress complements unit + integration)
       - Hand-rolled SmallRng 选择理由
       - 4 个 tests 概览
       - 怎么 reproducible (env var Z42_STRESS_SEED)
       - 怎么 expand 覆盖 (env var Z42_STRESS_ITERS)
- [ ] P1.2 Phase 表加 add-gc-stress-test 行
- [ ] P1.3 C2 backlog entry: "future" → "landed"
- [ ] P1.4 archive 到 `docs/spec/archive/YYYY-MM-DD-add-gc-stress-test/`
- [ ] P1.5 final `test-all.sh --scope=full` GREEN
- [ ] P1.6 commit + push

## 备注

实施期发现入 commit message + 备注 section.

## 后续 spec 依赖关系

| 后续 spec | 依赖本 spec 的什么 |
|----------|-------------------|
| `add-gc-coverage-guided-fuzz` | 升级到 libFuzzer + coverage feedback |
| `add-gc-stress-multi-thread` | 多线程 stress 版本（基于 cross_thread_smoke 模式）|
| `add-gc-stress-ci` | CI 长时序 stress 跑（millions of iters）|
| `add-gc-property-shrinking` | proptest 引入 + failure case shrinking |
