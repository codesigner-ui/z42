# Tasks: 调查 ConcurrentMarkSweep 残留 mark bit race

> 状态：🟡 进行中（仅探索阶段，等 design 决定根因后展开）| 创建：2026-05-26

## 进度概览
- [ ] 阶段 1: 二分定位退化 commit
- [ ] 阶段 2: 根因分析（更新 design.md）
- [ ] 阶段 3: 修复 + 回归测试

## 阶段 1: 二分定位

- [ ] 1.1 `git bisect start HEAD 9f461ebc` 起点
- [ ] 1.2 每个 candidate commit 跑 `cargo test --test cross_thread_smoke concurrent_gc_mode_stress_no_race_no_leak`（连跑 3 次确认稳定 pass/fail）
- [ ] 1.3 记录 first-bad commit，写入 design.md

## 阶段 2: 根因分析

- [ ] 2.1 读 first-bad 的 diff，对照 arc_heap.rs sweep + concurrent.rs collect_cycle 状态机
- [ ] 2.2 写 design.md 提两个候选根因 + 推荐
- [ ] 2.3 User 确认根因后进阶段 3

## 阶段 3: 修复

- [ ] 3.1 在 GC 内修正（绝不"放宽" invariant）
- [ ] 3.2 cargo test —— 全绿（含本测试稳定 10/10）
- [ ] 3.3 docs/design/runtime/vm-architecture.md 或 GC 专章追加"并发 mark bit 生命周期"说明
