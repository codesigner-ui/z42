# Tasks: Safepoint-aware auto-threshold GC trigger

> 状态：🟢 已完成 | 创建：2026-05-20 | 完成：2026-05-20 | 类型：vm

## 进度概览
- [x] 阶段 1: MagrGC trait + ArcMagrGC `set_external_needs_collect_flag` 实现
- [x] 阶段 2: ArcMagrGC `maybe_auto_collect` 改为 flag-based
- [x] 阶段 3: VmCore.needs_auto_collect 字段 + 构造路径 wire
- [x] 阶段 4: `check_safepoint` 扩展 drain 逻辑
- [x] 阶段 5: 单测 + 集成测试
- [x] 阶段 6: 文档同步
- [x] 阶段 7: 归档 + commit + push

## 阶段 1: trait + ArcMagrGC method

- [x] 1.1 `src/runtime/src/gc/heap.rs` MagrGC trait 加 `fn set_external_needs_collect_flag(&self, _flag: Arc<AtomicBool>) {}` 默认 no-op
- [x] 1.2 `src/runtime/src/gc/arc_heap.rs` ArcMagrGC 加字段 `external_needs_collect: parking_lot::Mutex<Option<Arc<AtomicBool>>>`，构造初始化为 None
- [x] 1.3 ArcMagrGC `impl MagrGC` 加 `set_external_needs_collect_flag` 实现
- [x] 1.4 cargo build GREEN

## 阶段 2: maybe_auto_collect 改写

- [x] 2.1 `arc_heap.rs::maybe_auto_collect` 改为：pressure trip 时若 `external_needs_collect` 装有 flag → `flag.store(true, Release)`；否则 fallback 到原 `self.collect_cycles()` inline 路径
- [x] 2.2 cargo test 全过（现有 GC 单测走 fallback，无回归）

## 阶段 3: VmCore wiring

- [x] 3.1 `vm_context.rs` VmCore 加字段 `needs_auto_collect: Arc<AtomicBool>`
- [x] 3.2 构造时初始化为 `Arc::new(AtomicBool::new(false))`
- [x] 3.3 在 `Arc::new(VmCore {...})` 之后立即调 `core.heap.set_external_needs_collect_flag(Arc::clone(&core.needs_auto_collect))`
- [x] 3.4 `new_with_core` 不需重新 wire（共享 core 已有 flag）
- [x] 3.5 cargo build GREEN

## 阶段 4: check_safepoint drain

- [x] 4.1 `gc/safepoint.rs::check_safepoint` Idle phase 时检查 `ctx.core.needs_auto_collect.swap(false, AcqRel)`
- [x] 4.2 若为 true → `let _g = request_gc_pause(ctx); ctx.heap().collect_cycles();`（guard drop 释放）
- [x] 4.3 cargo test 全过

## 阶段 5: 测试

- [x] 5.1 `gc/safepoint_tests.rs` 加 `auto_collect_flag_drained_at_next_safepoint`：手工 set flag true → check_safepoint → 验证 flag false + gc_cycles 增加
- [x] 5.2 `runtime/tests/cross_thread_smoke.rs` 加 `auto_collect_triggers_via_safepoint_no_race`：4 workers + 小 max_bytes（如 4096），跑 200 iters × 4，验证 gc_cycles > 0 + 无 deadlock / 无 panic
- [x] 5.3 ./scripts/test-stdlib.sh 全量 69/69 不回归
- [x] 5.4 ./scripts/test-all.sh 全绿

## 阶段 6: 文档同步

- [x] 6.1 `docs/design/runtime/concurrency.md` "并发 GC" 行 🟡 → ✅（safepoint complete）
- [x] 6.2 `docs/design/runtime/vm-architecture.md` Safepoint 协议章节 "v0 范围"表 auto-threshold 行更新为 deferred-flag-based

## 阶段 7: 归档 + commit

- [x] 7.1 mv → `docs/spec/archive/2026-05-20-add-gc-safepoint-auto-threshold/`
- [x] 7.2 commit + push
- [x] 7.3 verify CI GREEN

## 备注

### 实施期发现 1 —— 集成测试初版死锁

阶段 5.2 `auto_collect_triggers_via_safepoint_no_race` 初稿在 main 完成 50 次 check_safepoint 后立即 `for h in handles { h.join() }`，期间 main 在 join 等 worker，不再 check_safepoint —— 不会 park。但 main 仍持 VmContext，所以 `vm_contexts.len() = 5`，collector（某 worker 抢到 collect 权）等 `parked_count >= 4` 但只有其它 3 个 worker 能 park（主以外的 3 个）。

修复：用 `Arc<AtomicUsize> active` 计数活跃 worker，main 用 `while active.load(Acquire) > 0 { check_safepoint(&main); thread::yield_now(); }` 持续参与 safepoint 协议直到所有 worker 完成。

教训记录：多线程 + safepoint 协议下，所有持 VmContext 的线程都必须**持续**调用 check_safepoint（直接 join 或别的 blocking 操作 = 协议外）。z42 用户层不会遇到这个（用户线程都在跑 interp dispatch loop，每个 backward branch 自动 check），但 Rust 集成测试需要显式处理。

### 实施期发现 2 —— `Std.GC.Collect()` 与 auto-threshold 协议互动

显式 `__gc_collect` 调用走 `let _g = request_gc_pause(ctx); ctx.heap().collect_cycles();` 路径；它不读 `needs_auto_collect` flag。同时，flag 可能在 `collect_cycles` 跑完之前已被某 worker 的 alloc 设过 true。Collect 结束 + last_auto_collect_used 更新 → 短期内不再 trip pressure，flag 残留 true 没事 —— 下次 check_safepoint 时 swap claim，再跑一次 collect（基本是 no-op，gc_cycles+1 但 freed_bytes=0）。可接受，没浪费功，没正确性问题。

### test-all.sh 临时失败

提交前一次 test-all 报 dotnet test failed at stage 2/6，但直接跑 `dotnet test src/compiler/z42.Tests/z42.Tests.csproj` 1288/1288 全过。第二次完整 test-all 也 ALL GREEN —— 推测是 dotnet build-cache 在 cargo build 完后的瞬时不一致状态，重跑即恢复。不影响代码正确性。
