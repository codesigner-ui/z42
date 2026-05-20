# Tasks: GC safepoint protocol (interp-only v0)

> 状态：🟢 已完成 | 创建：2026-05-20 | 完成：2026-05-20 | 类型：vm

## 进度概览
- [x] 阶段 1: VmCore 新字段 (gc_phase / gc_phase_cv / parked_count)
- [x] 阶段 2: gc/safepoint.rs 模块
- [x] 阶段 3: ArcMagrGC::collect_cycles 接入 stop-the-world wrapper
- [x] 阶段 4: interp dispatch loop 插 safepoint check 三处
- [x] 阶段 5: Rust 单测 + cross-thread 集成测试
- [x] 阶段 6: 文档同步
- [x] 阶段 7: 归档 + commit + push

## 阶段 1: VmCore 字段

- [x] 1.1 `src/runtime/src/vm_context.rs` VmCore 加 3 字段：
        `gc_phase: parking_lot::Mutex<GcPhase>` (定义在 gc/safepoint.rs)
        `gc_phase_cv: parking_lot::Condvar`
        `parked_count: AtomicUsize`
- [x] 1.2 构造路径初始化 gc_phase = Idle / parked_count = 0
- [x] 1.3 cargo build GREEN

## 阶段 2: safepoint 模块

- [x] 2.1 `src/runtime/src/gc/safepoint.rs` NEW —— `GcPhase` enum + `check_safepoint` + `park_until_idle` + `request_gc_pause` + `GcPauseGuard`
- [x] 2.2 `src/runtime/src/gc/mod.rs` 加 `pub mod safepoint;` + re-export
- [x] 2.3 `src/runtime/src/gc/safepoint_tests.rs` NEW —— 4+ 单测：
        `gc_phase_idle_by_default` / `request_pause_sets_phase` /
        `release_pause_resets_phase` / `parked_count_decrement_after_release`
- [x] 2.4 cargo test 全过

## 阶段 3: collect_cycles 接入

- [x] 3.1 `gc/arc_heap.rs::collect_cycles` 包一层 `request_gc_pause` —— 取
        `_pause` RAII guard，调用现有 mark+sweep，guard drop 时 release
- [x] 3.2 注意：需要 `&VmContext` 参数 — 目前 `MagrGC::collect_cycles(&self)`
        签名无 ctx 入参，需扩展为 `collect_cycles(&self, ctx: &VmContext)`
- [x] 3.3 grep 所有 collect_cycles callsite 调整签名
- [x] 3.4 cargo test 全过

## 阶段 4: interp safepoint 插入

- [x] 4.1 `src/runtime/src/interp/mod.rs::exec_function` 入口加 `check_safepoint(ctx)`
- [x] 4.2 `src/runtime/src/interp/exec_instr.rs` Br terminator 在 backward
        branch 加 check_safepoint（block_index target ≤ current）
- [x] 4.3 同上 BrCond terminator
- [x] 4.4 `interp/exec_call.rs::call` 返回后加 check_safepoint
- [x] 4.5 cargo test 全过 (existing tests do not regress)

## 阶段 5: 测试

- [x] 5.1 `runtime/tests/cross_thread_smoke.rs` 加
        `gc_collect_with_concurrent_mutators_no_race` —— 4 workers alloc loop
        + 1 collector loop 100 轮 + final consistency
- [x] 5.2 ./scripts/test-stdlib.sh 全量 69/69 不回归
- [x] 5.3 ./scripts/test-all.sh 全绿

## 阶段 6: 文档同步

- [x] 6.1 `docs/design/runtime/concurrency.md` "并发 GC" 行 ❌ → 🟡（safepoint 落地，mark/sweep 仍单线程）；next-step list `add-gc-safepoint` ✅
- [x] 6.2 `docs/design/runtime/vm-architecture.md` 加 safepoint 协议章节

## 阶段 7: 归档 + commit

- [x] 7.1 mv → `docs/spec/archive/2026-05-20-add-gc-safepoint/`
- [x] 7.2 commit + push
- [x] 7.3 verify CI GREEN

## 备注

### 实施期发现 1 —— 不改 MagrGC trait 签名（Decision 6 amendment）

阶段 3 原计划把 `MagrGC::collect_cycles(&self)` 改为 `(&self, ctx: &VmContext)` 让所有 callsite 知道 ctx。实施期发现：
- `arc_heap.rs::maybe_auto_collect` 在内部 alloc 路径触发（无 ctx 可传）
- 扩展签名要级联改 `alloc_object` / `alloc_array` 等 10+ 个 trait 方法 + ~50 处 GC 单测 fixture
- 工作量大且与本 spec 主线（interp safepoint 协议）正交

改用 **Option A**：只在 corelib/gc.rs 的 `builtin_gc_collect` / `builtin_gc_force_collect`（即 z42 `Std.GC.Collect()` / `ForceCollect()`）包 `request_gc_pause` RAII guard。auto-threshold 路径 (`maybe_auto_collect`) 仍 unguarded —— v0 限制：高分配压力 + 跨线程并发 alloc + 跨线程 reads/writes 同时发生时仍有 race window。

`add-gc-safepoint-auto-threshold` 作为 deferred follow-up 处理。

### 实施期发现 2 —— `HeapStats` 字段名

cross_thread_smoke 集成测试初稿用 `stats.collections` 字段名，实际 HeapStats 用 `gc_cycles`。修正一行。
