# Proposal: Expand MagrGC to Full MMTk-Style Embedding Interface

## Why

z42 在 README/roadmap 明确定位为**嵌入式友好**的语言（host 把 z42 嵌进 C++/Rust 应用，
类似 Lua / V8 / JavaScriptCore / SpiderMonkey 的角色）。Phase 1 落地的 `MagrGC` trait
只有 6 个方法（alloc_object / alloc_array / write_barrier / collect / collect_cycles /
stats），远不足以让宿主应用做深度集成。

宿主真正需要的 GC 接口形状（来自 Lua / V8 / .NET / JVM 的实际 API 调研）：

| 维度 | 主流引擎 API |
|------|-------------|
| **根扫描** | Lua registry / V8 `Persistent<T>` / .NET `GCHandle.Alloc(Normal)` |
| **GC 事件回调** | V8 `AddGCPrologueCallback` / .NET `RegisterForFullGCNotification` / JVMTI events |
| **分配采样** | V8 `HeapProfiler::StartTrackingHeapObjects` / .NET `EventPipe` |
| **堆快照 / 对象遍历** | V8 `TakeHeapSnapshot` / JVMTI `IterateThroughHeap` |
| **堆上限 / 暂停恢复** | V8 `Isolate::SetHeapLimits` / Lua `lua_gc(LUA_GCSETPAUSE)` |
| **终结器 / 弱引用** | Lua `__gc` / Java `Reference<T>` / .NET `IDisposable + Cleaner` |

按 [MMTk](https://www.mmtk.io/) 的 `VMBinding` porting contract 形状把 trait 扩到完整
覆盖 —— MMTk 是 OpenJDK / V8 / Julia / Ruby / RustPython 的**事实标准** GC 抽象，
若未来真要换成 mark-sweep / 分代 / MMTk 集成，接口形状无需再变。

Phase 1 的 RcMagrGC 实现继续作为默认后端，行为不变；新增能力以 trait 默认实现 + RC
模式可行子集落地。**接口完整性优先于实现深度** —— 这次把 trait 形状一次性补到位，
让嵌入用户从今天起就能基于稳定 API 编写宿主集成代码。

## What Changes

### 新增 trait 能力组（10 个）

1. **Roots API**：`pin_root` / `unpin_root` / `enter_frame` / `leave_frame` / `for_each_root`
2. **Write barriers (扩展)**：`write_barrier_field` / `write_barrier_array_elem`
3. **Object Model helpers**：`object_size_bytes` / `scan_object_refs`
4. **Collection control (扩展)**：`force_collect` / `pause` / `resume`
5. **Heap config**：`set_max_heap_bytes` / `used_bytes`
6. **Finalization**：`register_finalizer` / `cancel_finalizer`
7. **Weak references**：`make_weak` / `upgrade_weak`
8. **Event observers**：`add_observer` / `remove_observer` + `GcEvent` 枚举 + `GcObserver` trait
9. **Profiler hooks**：`set_alloc_sampler` / `take_snapshot` / `iterate_live_objects`
10. **Stats (扩展)**：`HeapStats` 增加 `used_bytes` / `roots_pinned` / `finalizers_pending` / `observers` 字段

### 新增支持类型

`RootHandle` / `FrameMark` / `ObserverId` / `GcEvent` / `GcKind` / `GcObserver` /
`AllocSample` / `AllocKind` / `AllocSamplerFn` / `FinalizerFn` / `CollectStats` /
`WeakRef` / `HeapSnapshot` / `ObjectStats` / `SnapshotCoverage`

### RcMagrGC 实现

- Roots registry：`HashMap<RootHandle, Value>` + frame stack
- Observers：`Vec<(ObserverId, Arc<dyn GcObserver>)>`，`collect_cycles` 触发 Before/After 事件
- Sampler：`alloc_*` 时回调（如安装）
- Snapshot / iterate：从 pinned roots 出发递归 `scan_object_refs`，去重 by `Rc::as_ptr`，
  Phase 1 标记 `SnapshotCoverage::ReachableFromPinnedRoots`（document 限制）
- Finalizers：`HashMap<*const _, FinalizerFn>` 仅注册不触发（RC 缺 Drop hook，Phase 3 mark-sweep 才有真实调用语义；本次 stub）
- Weak refs：`std::rc::Weak<RefCell<T>>` 直接包装；upgrade 失败返 None
- Pause / max_heap：`Cell<u32>` / `Cell<Option<u64>>`，alloc 前检查并触发 NearHeapLimit / OutOfMemory 事件

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/gc/types.rs` | NEW | 全部支持类型（RootHandle / FrameMark / GcEvent / WeakRef / ...）|
| `src/runtime/src/gc/heap.rs` | MODIFY | trait 扩展到 ~30 方法 |
| `src/runtime/src/gc/heap_tests.rs` | MODIFY | trait 默认方法契约测试扩展 |
| `src/runtime/src/gc/rc_heap.rs` | MODIFY | RcMagrGC 完整实现 |
| `src/runtime/src/gc/rc_heap_tests.rs` | MODIFY | 全部新能力的单元测试 |
| `src/runtime/src/gc/mod.rs` | MODIFY | re-export 新增类型 |
| `src/runtime/src/gc/README.md` | MODIFY | 接口形状 + Phase 1 RC 模式限制 |
| `docs/design/vm-architecture.md` | MODIFY | "GC 子系统" 段全面更新（trait 形状、RC 限制、Phase 路线对齐）|

**只读引用**：`src/runtime/src/metadata/types.rs`（Value / ScriptObject 形状）；`src/runtime/src/vm_context.rs`（heap 持有方）

## Out of Scope

- **真实 GC 算法实现**（Phase 2 cycle collector / Phase 3 mark-sweep）—— 本次只扩接口
- **Cranelift stack maps**（Phase 3 trace 真实运行时栈扫描必需，本次 enter_frame/leave_frame 只是 host-side scope）
- **Finalizer 真实触发**（RC 缺 Drop hook，Phase 3 调度）
- **`take_snapshot` / `iterate_live_objects` 全堆精确**（RC 模式只能从 pinned roots 出发，Phase 3 trace 才能全堆精确）
- **NativeFn 签名扩展**（Phase 1.5 单独 spec）
- **多线程 / Send + Sync VmContext**（A6 backlog，L3 async 时统一处理）

## Open Questions

无 —— 所有设计决策（Send + Sync 边界、Snapshot 限制语义、Finalizer stub 范围、ID 生成方式等）已在 design.md 中明确。
