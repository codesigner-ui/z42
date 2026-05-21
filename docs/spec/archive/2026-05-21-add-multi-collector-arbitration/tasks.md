# Tasks: Multi-collector arbitration

> 状态：🟢 已完成 | 创建：2026-05-21 | 完成：2026-05-21 | 类型：vm

## 进度概览
- [x] 阶段 1: VmCore.collector_active + request_gc_pause Option API
- [x] 阶段 2: GcPauseGuard::drop 释放 collector_active
- [x] 阶段 3: 既有 callers 适配 Option
- [x] 阶段 4: Rust 单测 + cross_thread 4-worker auto_collect 恢复
- [x] 阶段 5: 文档同步
- [x] 阶段 6: 归档 + commit + push

## 阶段 1: VmCore field + request_gc_pause CAS

- [x] 1.1 `vm_context.rs` VmCore 加 `collector_active: AtomicBool`，构造路径初始化 false
- [x] 1.2 `gc/safepoint.rs::request_gc_pause` 改返 `Option<GcPauseGuard>`；前置 CAS claim；失败 → park_until_idle + None
- [x] 1.3 cargo build GREEN

## 阶段 2: GcPauseGuard release

- [x] 2.1 `GcPauseGuard::drop` 加 `ctx.core.collector_active.store(false, Release)`
- [x] 2.2 cargo build GREEN

## 阶段 3: 既有 callers 适配 Option

- [x] 3.1 `gc/safepoint.rs::check_safepoint_slow` 用 `if let Some(_pause) = request_gc_pause(ctx) { collect_cycles }`
- [x] 3.2 `corelib/gc.rs::builtin_gc_collect` 同 + docstring 注 best-effort
- [x] 3.3 `corelib/gc.rs::builtin_gc_force_collect` 同
- [x] 3.4 既有 `gc/safepoint_tests.rs` 中 `request_gc_pause_with_only_self_proceeds_immediately` / `pause_guard_drop_notifies_waiters` / `request_pause_waits_for_other_mutators_to_park` 改用 `.expect("CAS should succeed")` 拆 Option
- [x] 3.5 既有 `cross_thread_smoke.rs::gc_collect_with_concurrent_mutators_no_race` 用 `let _pause = request_gc_pause(&collector).expect(...)`
- [x] 3.6 既有 `cross_thread_smoke.rs::gc_collect_with_concurrent_mutators_no_race` 4-worker auto_collect 测试中 `request_gc_pause` 同适配
- [x] 3.7 cargo build GREEN

## 阶段 4: 单测 + 4-worker 集成测试

- [x] 4.1 `gc/safepoint_tests.rs` 加 `request_gc_pause_returns_some_when_uncontested`
- [x] 4.2 加 `second_collector_falls_back_to_mutator_park_returns_none` —— 主线程占住 collector_active，另起线程调 request_gc_pause 验证返 None + parked_count 增过
- [x] 4.3 加 `release_re_enables_next_collector` —— 主线程 collector active 然后 drop guard，第二个 request 成功
- [x] 4.4 `cross_thread_smoke.rs::auto_collect_triggers_via_safepoint_no_race` 恢复到 4 workers
- [x] 4.5 `cross_thread_smoke.rs` 加 `concurrent_gc_collect_callers_arbitrate` —— 2 thread 同时 request_gc_pause + collect_cycles，验证一个 Some 一个 None
- [x] 4.6 cargo test 全过

## 阶段 5: 文档同步

- [x] 5.1 `docs/design/runtime/vm-architecture.md` Safepoint v0 范围表 "多 collector 仲裁" 行 ⚠️ → ✅，描述 collector_active CAS + GcPauseGuard release

## 阶段 6: 归档 + commit

- [x] 6.1 mv → `docs/spec/archive/2026-05-21-add-multi-collector-arbitration/`
- [x] 6.2 targeted GREEN：cargo --lib + cross_thread_smoke + test-stdlib z42.threading（runtime-only 改动按前一 spec 同款简化策略）
- [x] 6.3 commit + push

## 备注

### 实施期发现 1 —— GcPauseGuard 跨线程返回的生命周期问题

`request_gc_pause(&w)` 返 `Option<GcPauseGuard<'_>>`，guard 借 `&w`。集成测试初版尝试 `thread::spawn(move || { request_gc_pause(&w) })`：guard 借 w，w 在闭包结束时 drop，但 Option 的 Drop 会跑 guard.drop — borrow checker 拒绝跨线程返回。

修复：在闭包内 `is_some()` 拆 Option 拿 bool；`drop(pause)` 释放 guard；返 bool。同样 pattern 用 in `second_collector_falls_back_to_mutator_park_returns_none` 单测。

### 实施期发现 2 —— 集成测试 main thread 必须参与 safepoint 协议

`concurrent_gc_collect_callers_arbitrate` 测试初版让 main 直接 `for h in handles { h.join() }`。但 main 持 VmContext 进 vm_contexts。active collector worker 等 `parked_count >= n_workers + main - 1`；main 在 kernel join() 永不 park → 死锁。

修复：用 `Arc<AtomicUsize> active` 计数活跃 worker。main 在 `while active > 0 { check_safepoint }` 循环里参与 safepoint 直到所有 worker 完成。这是 add-gc-safepoint-auto-threshold 已经记录的 pattern；现在巩固为 "main thread always loops check_safepoint when holding VmContext during multi-thread test"。

### 实施期发现 3 —— `main.core.collector_active` 集成测试不可见

vm_context 字段 `pub(crate)` —— 集成测试是独立 crate。替换 assertion 为 functional check：`request_gc_pause(&main).is_some()` 等价证明 collector_active 已释放。

### 测试验证

- gc/safepoint_tests 14/14（11 既有 + 3 新仲裁单测）
- cross_thread_smoke 9/9（包括恢复的 4-worker auto_collect + 新 concurrent_gc_collect_callers_arbitrate）
