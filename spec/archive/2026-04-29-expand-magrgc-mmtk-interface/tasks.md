# Tasks: Expand MagrGC to Full MMTk-Style Embedding Interface

> 状态：🟢 已完成 | 创建：2026-04-29 | 完成：2026-04-29

## 进度概览
- [x] 阶段 1: 支持类型新文件 gc/types.rs
- [x] 阶段 2: trait 扩展 + 默认方法测试
- [x] 阶段 3: RcMagrGC 完整实现
- [x] 阶段 4: RcMagrGC 单元测试扩展
- [x] 阶段 5: gc 模块对外导出 + README
- [x] 阶段 6: 文档同步
- [x] 阶段 7: 验证

## 阶段 1: gc/types.rs（NEW）
- [x] 全部 ~15 个支持类型（RootHandle / FrameMark / ObserverId / GcKind / GcEvent / GcObserver / AllocKind / AllocSample / AllocSamplerFn / FinalizerFn / CollectStats / WeakRef / SnapshotCoverage / ObjectStats / HeapSnapshot / HeapStats）

## 阶段 2: trait 扩展（gc/heap.rs）
- [x] trait 增至 ~30 方法，10 个能力组分段（Allocation / Roots / Write barriers / Object Model / Collection / Heap config / Finalization / Weak refs / Observers / Profiler / Stats）
- [x] Roots / Write barriers / Object Model / Collection / Heap config / Finalization / Weak refs / Observers / Profiler 全部方法签名落地
- [x] heap_tests.rs 默认方法 no-op 测试扩展（write_barrier_field / write_barrier_array_elem / collect / HeapStats default 7 字段）

## 阶段 3: RcMagrGC 实现（gc/rc_heap.rs）
- [x] 3.1 RcHeapInner 内部状态结构（含 roots / frame_pins / observers / finalizers / alloc_sampler / pause_count / ID 生成器 / near_limit_warned）
- [x] 3.2 alloc_object / alloc_array 走 record_alloc 通路（含 sampler / pressure 检查）
- [x] 3.3 Roots：pin / unpin / enter_frame / leave_frame / for_each_root
- [x] 3.4 Object Model：object_size_bytes / scan_object_refs（exhaustive 匹配，无 Map）
- [x] 3.5 Collection：force_collect（emit Before/After + return CollectStats）/ pause / resume / collect_cycles
- [x] 3.6 Heap config：set_max_heap_bytes（含重置 near_limit_warned）/ used_bytes
- [x] 3.7 Finalizer：register / cancel（基于 `Rc::as_ptr` as usize key；仅注册不触发）
- [x] 3.8 Weak refs：make_weak / upgrade_weak（Object/Array 走 enum；原子值返 None）
- [x] 3.9 Observers：add / remove / fire_event helper（snapshot-then-dispatch 避免重入冲突）
- [x] 3.10 Profiler：set_alloc_sampler / take_snapshot / iterate_live_objects（HashSet 去重 by `Rc::as_ptr`）

## 阶段 4: 单元测试（gc/rc_heap_tests.rs）

40 个新测试分布：
- [x] Allocation（4）
- [x] Roots（5：pin / unpin / for_each / enter-leave / nested / outside-frame）
- [x] Object Model（5）
- [x] Collection（6：force / pause / resume / nested-pause / collect_cycles / collect-noop）
- [x] Heap config（2）
- [x] Finalization（3）
- [x] Weak refs（5）
- [x] Observers（5：add / before-after-on-cycles / before-after-on-force / remove / OOM）
- [x] Profiler（7：sampler / set-none / coverage / pinned-root / nested / iterate-reachable / iterate-cycle-dedupe）
- [x] Stats（3）

## 阶段 5: 模块导出 + README
- [x] 5.1 `gc/mod.rs` re-export 全部新类型（HeapStats / HeapSnapshot / ObjectStats / SnapshotCoverage / RootHandle / FrameMark / ObserverId / GcKind / GcEvent / GcObserver / AllocKind / AllocSample / AllocSamplerFn / FinalizerFn / CollectStats / WeakRef）
- [x] 5.2 `gc/README.md` 更新：核心文件 4→5、入口点扩到全部嵌入类型、能力组表、典型使用代码、Phase 1 RC 已知限制 6 项

## 阶段 6: 文档同步
- [x] 6.1 `docs/design/vm-architecture.md` "GC 子系统" 段：trait 形状代码块替换为能力组表、Phase 1 RcMagrGC 描述细化、已知限制扩到 6 项、Phase 路线表新增 "Phase 1 (扩展)" 行

## 阶段 7: 验证

### 编译状态
- ✅ `cargo build` 0 错误 0 警告
- ✅ `dotnet build` 0 错误（已建好）

### 测试结果
- ✅ Rust unit tests: **120/120 通过**（lib），含新增 40 个 GC tests
- ✅ Rust integration tests (`zbc_compat`): **4/4**
- ✅ `dotnet test`: **735/735**
- ✅ `./scripts/test-vm.sh`: **interp 101 + jit 101 = 202/202**

### Spec scenarios 覆盖

| 能力组 | 场景数 | 全部覆盖 |
|--------|-------|---------|
| Roots API | 4 | ✅ |
| Write barriers | 1 | ✅ |
| Object Model | 4 | ✅ |
| Collection control | 3 | ✅ |
| Heap config | 2 | ✅ |
| Finalization | 3 | ✅ |
| Weak references | 4 | ✅ |
| Event observers | 3 | ✅ |
| Profiler hooks | 5 | ✅ |
| HeapStats | 1 | ✅ |

### Tasks 完成度：7 阶段全部 ✅

### 结论：✅ 全绿，可以归档
