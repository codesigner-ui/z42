# Tasks: Custom Region Allocator

> 状态：🟡 进行中（spec-only commit start）| 创建：2026-05-22 | 类型：vm

**总体策略**：替换 Arc<GcAllocation<T>> 为 chunked region 分配器 +
handle 索引。最大破坏点：finalizer 触发时机从 "Drop time" → "sweep
time"，配 `Std.GC.Finalize(x)` 显式 API 给资源回收路径。Pre-1.0 是
做这个改动的最佳时机（migration cost 最小）。

**总工作量估算**：~7-10 个 work session（实际推进比 concurrent-gc
稍小，因为 GC API surface 不变；最大成本在 finalizer 测试 + corelib
RAII 重写）。

## 进度概览

- [ ] 阶段 1: 探索（已完成，map 见 design.md "Architecture"）
- [ ] 阶段 2-6: spec 文档（proposal / spec / design / tasks，本文件）
- [ ] 阶段 6.5: User 确认
- [ ] 阶段 7: 实施 P0-P3
- [ ] 阶段 8: 每 phase + 最终 GREEN
- [ ] 阶段 9: 归档

## P0: Region<T> + RegionEntry<T> 基础设施 (~2 sessions)

- [ ] P0.1 新建 `gc/region.rs` —— `Region<T>` struct + chunked storage
       (Vec<Box<[MaybeUninit<RegionEntry<T>>; 256]>>)
- [ ] P0.2 `RegionEntry<T>` —— value (Mutex), marked (AtomicU8), alive
       (AtomicBool), generation (AtomicU32), finalizer slot
- [ ] P0.3 `Region::alloc(value)` —— free list pop 优先，否则 bump pointer
- [ ] P0.4 `Region::resolve(handle)` —— `&RegionEntry<T>`，地址稳定
- [ ] P0.5 `Region::tombstone(handle)` —— alive=false, generation++,
       push free_list
- [ ] P0.6 `Region::iterate_alive(visit)` —— 走 chunks，跳 alive=false
- [ ] P0.7 单测 `gc/region_tests.rs`：
       - `alloc_creates_first_chunk_on_demand`
       - `alloc_grows_chunks_when_full`
       - `alloc_pointer_stability_across_grow` (`as_ptr` 在 alloc 后多次仍指向相同地址)
       - `alloc_free_list_reuses_tombstoned_slot`
       - `tombstone_bumps_generation`
       - `iterate_alive_skips_tombstoned`
- [ ] P0.8 cargo --lib + region_tests GREEN
- [ ] P0.9 commit (Region<T> 独立 + 无 caller，纯 infrastructure)

## P1: GcRef / WeakGcRef rewrite + alloc 路由 (~3 sessions)

- [ ] P1.1 `gc/refs.rs` 大改：`GcRef<T>` 从 `Arc<GcAllocation<T>>` 变
       handle (chunk_idx: u16, entry_idx: u16, generation: u32)；
       `Clone` 是 integer copy；`Drop` 是 no-op
- [ ] P1.2 `WeakGcRef<T>` 同 handle 形态 + generation check on upgrade
- [ ] P1.3 `GcRef::as_ptr` 通过 region 解析到 `RegionEntry.value` 的
       Mutex 地址；保持 pointer stability 契约
- [ ] P1.4 `GcRef::borrow` / `borrow_mut` 走 region resolve + Mutex lock
- [ ] P1.5 `GcRef::mark` / `is_marked` / `clear_mark` 操作 RegionEntry
       的 `marked` 字段
- [ ] P1.6 `ArcMagrGC` 加 `region_object: Region<ScriptObject>` +
       `region_array: Region<Vec<Value>>` 字段
- [ ] P1.7 `alloc_object` / `alloc_array` 走 region.alloc
- [ ] P1.8 删除 `heap_registry: Vec<WeakRef>` 字段；
       `iterate_live_objects` + `sweep_phase` + `snapshot_*` 改走 region
- [ ] P1.9 删除 `GcAllocation<T>` struct（被 RegionEntry 取代）
- [ ] P1.10 cargo build green
- [ ] P1.11 现有 GC 测试 + interp + JIT + cross_thread 全 GREEN
       （行为应该等价 modulo finalizer timing，那部分留 P2）
- [ ] P1.12 commit

## P2: Finalizer 语义迁移 + `Std.GC.Finalize(x)` API (~2 sessions)

- [ ] P2.1 删除 `GcAllocation::Drop` 触发 finalizer 路径（GcAllocation
       已被删，但确认 RegionEntry 没继承 Drop-finalizer）
- [ ] P2.2 `sweep_phase` 内部 finalize unmarked entries —— 已有从
       mark-sweep spec 落下的代码骨架，确认 region 路径正确触发
- [ ] P2.3 新 builtin `builtin_gc_finalize` 在 corelib/gc.rs
- [ ] P2.4 注册 `Std.GC.Finalize(x): void` 到 builtin map
- [ ] P2.5 `src/libraries/z42.core/src/Std/GC.z42` 加 `Finalize(x)`
       script signature
- [ ] P2.6 迁移 `arc_heap_tests/finalization.rs` ~10 个测试：
       - "drop fires finalizer" → "force_collect fires finalizer"
       - 至少 2 个加 `Std.GC.Finalize(x)` 覆盖
- [ ] P2.7 审 corelib RAII 模式：
       - `corelib/process.rs::builtin_process_handle_drop` →
         决定 (a) 引入 `Std.IO.Process.Close()` 显式 API + 文档
         "process exit at sweep without Close" 或 (b) 接受 sweep 关 fd
       - 同步更新 `process_tests.rs` 期待
- [ ] P2.8 单测 (regions tests 加章节)：
       - `finalizer_does_not_fire_on_scope_exit`
       - `finalizer_fires_at_sweep_when_unreachable`
       - `std_gc_finalize_fires_finalizer_immediately`
       - `use_after_finalize_panics_in_release` (Decision 5)
- [ ] P2.9 cargo + `test-all.sh --scope=full` GREEN
- [ ] P2.10 commit

## P3: Bench + docs + archive (~1-2 sessions)

- [ ] P3.1 `gc_cycle_bench.rs` 扩 `alloc_throughput_*` workloads：
       - tight loop alloc 10k objects, measure obj/sec
       - tight loop alloc + drop, measure alloc+drop cycle/sec
       - tight loop alloc + collect, measure full cycle
- [ ] P3.2 `sweep_overhead_*`：
       - 10k objects survive sweep, measure per-object cost
       - 10k objects swept (with + without finalizer)
- [ ] P3.3 跑 baseline (worktree at HEAD~spec-start) vs post-spec
- [ ] P3.4 报告入 design.md "Benchmark Results" 段
- [ ] P3.5 `docs/design/runtime/vm-architecture.md`：
       - "GC heap backing" 章重写: Arc → Region
       - 新章节"Finalizer contract"：sweep-only + Std.GC.Finalize API
       - Phase 表新增 add-custom-allocator 行
       - A1 entry 从 "future" 转 "landed"
- [ ] P3.6 archive 到 `docs/spec/archive/YYYY-MM-DD-add-custom-allocator/`
- [ ] P3.7 final `test-all.sh --scope=full` GREEN（双 GcMode）
- [ ] P3.8 commit

## 备注

实施期发现写在每个 P-commit message + 必要时回本文件 备注 节。

按 feedback_problem_first_then_defer：spec 没覆盖的设计决策必须停下汇报。

## 后续 spec 依赖关系

| 后续 spec | 依赖本 spec 的什么 |
|----------|-------------------|
| `add-generational-gc` (A3) | Region<T> 基础 + Promotion 跨 region；本 spec landed 后可开 |
| `add-size-class-regions` | 若 fragmentation 实测瓶颈，按对象大小分桶 |
| `add-per-thread-arena` | 若 alloc lock contention 实测瓶颈，per-VmContext local arena |
| `add-handle-compression` | 若 GcRef size (12B) 实测瓶颈，压缩到 u64 |
| `add-mmtk-binding` (D1) | MMTk integration；本 spec 的 Region API 已对齐 MMTk Mutator::alloc shape |
