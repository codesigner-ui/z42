# Spec: Expand MagrGC to Full MMTk-Style Embedding Interface

## ADDED Requirements

### Requirement: Roots API（host-side pin + GC-side scan）

#### Scenario: pin_root 返回唯一 handle 并保留值
- **WHEN** `heap.pin_root(value)` 被调用
- **THEN** 返回的 `RootHandle` 在该 heap 实例内唯一，且 `for_each_root` 后续遍历能访问到该 value

#### Scenario: unpin_root 释放对应 root
- **WHEN** 已 pin 的 root 调用 `unpin_root(handle)`
- **THEN** 后续 `for_each_root` 不再访问该 value，`stats().roots_pinned` 减 1

#### Scenario: enter_frame / leave_frame 配对释放该帧内所有 pin
- **WHEN** `enter_frame()` 后 pin 3 个 root，再 `leave_frame(mark)`
- **THEN** 这 3 个 root 自动 unpin（`stats().roots_pinned` 回到帧前水平）

#### Scenario: for_each_root 遍历所有当前活跃 root
- **WHEN** pin 2 个 root 后调用 `for_each_root(visitor)`
- **THEN** visitor 被调用恰好 2 次，每次接收一个 `&Value`

### Requirement: Write barriers（扩展）

#### Scenario: 默认实现 no-op，不影响行为
- **WHEN** 调 `write_barrier_field` 或 `write_barrier_array_elem`
- **THEN** RcMagrGC 默认 no-op；Phase 2+ 后端可重载实现

### Requirement: Object Model helpers

#### Scenario: object_size_bytes 返回浅尺寸估计
- **WHEN** `object_size_bytes(&Value::I64(_))` 调用
- **THEN** 返回 `size_of::<Value>()`（基础类型的 enum tag + 内联数据）

#### Scenario: scan_object_refs 访问对象内嵌 Value
- **WHEN** Object 含 3 个 slot，call `scan_object_refs(&obj, visitor)`
- **THEN** visitor 被调用恰好 3 次（每个 slot 的 `&Value`）

#### Scenario: scan_object_refs 在数组上访问每个元素
- **WHEN** Array 含 5 个元素
- **THEN** visitor 被调用恰好 5 次

#### Scenario: scan_object_refs 在原子值上 no-op
- **WHEN** 在 `Value::I64(_)` / `Value::Str(_)` / `Value::Null` 上调用
- **THEN** visitor 调用次数为 0

### Requirement: Collection control（扩展）

#### Scenario: force_collect 返回 CollectStats
- **WHEN** `heap.force_collect()` 被调用
- **THEN** 返回 `CollectStats { freed_bytes, pause_us, kind }`，且 `stats().gc_cycles` 递增

#### Scenario: pause 期间触发的 collect 跳过实际工作
- **WHEN** `pause()` 后调用 `force_collect()`
- **THEN** 返回的 stats `freed_bytes == 0`，`kind == None`（被暂停跳过）

#### Scenario: resume 恢复 GC 工作
- **WHEN** `pause()` 然后 `resume()`，再次 `force_collect()`
- **THEN** GC 正常执行（RC 模式仍 stub，但 stats counter 正常更新）

### Requirement: Heap config

#### Scenario: set_max_heap_bytes 配置后 stats 反映上限
- **WHEN** `set_max_heap_bytes(Some(1_000_000))`
- **THEN** `stats().max_bytes == Some(1_000_000)`

#### Scenario: 接近上限时触发 NearHeapLimit 事件
- **WHEN** 设上限 1024 字节，注册 observer，分配累计接近上限
- **THEN** observer 收到 `GcEvent::NearHeapLimit { used, limit }`

### Requirement: Finalization

#### Scenario: register_finalizer 增加 finalizers_pending 计数
- **WHEN** 注册 finalizer
- **THEN** `stats().finalizers_pending` 加 1

#### Scenario: cancel_finalizer 减少计数
- **WHEN** cancel 已注册的 finalizer
- **THEN** `stats().finalizers_pending` 减 1

#### Scenario: Phase 1 finalizer 不会被自动触发（已知限制）
- **WHEN** 注册 finalizer 后对应对象被 drop（最后一个 Rc 释放）
- **THEN** finalizer 不会被调用 —— RC 模式无 Drop hook，Phase 3 mark-sweep 才支持

### Requirement: Weak references

#### Scenario: make_weak 在堆引用上成功
- **WHEN** 在 `Value::Object(_)` / `Value::Array(_)` 上 `make_weak(&v)`
- **THEN** 返回 `Some(WeakRef)`

#### Scenario: make_weak 在原子值上返回 None
- **WHEN** 在 `Value::I64(_)` / `Value::Str(_)` / `Value::Null` 上 `make_weak(&v)`
- **THEN** 返回 `None`

#### Scenario: upgrade_weak 在仍存活时返回原值
- **WHEN** 创建 weak ref 后强引用仍存活
- **THEN** `upgrade_weak(&w)` 返回 `Some(Value::...)`，且 `Rc::ptr_eq` 与原值相等

#### Scenario: upgrade_weak 在已回收时返回 None
- **WHEN** 强引用全部 drop 后调用 `upgrade_weak(&w)`
- **THEN** 返回 `None`

### Requirement: Event observers

#### Scenario: add_observer 返回唯一 ObserverId
- **WHEN** 添加 observer
- **THEN** 返回的 ObserverId 在 heap 实例内唯一，`stats().observers` 加 1

#### Scenario: collect_cycles 触发 Before/After 事件
- **WHEN** 安装 observer 后调用 `collect_cycles()`
- **THEN** observer 至少收到 `GcEvent::BeforeCollect { kind: CycleCollector, ... }` 与 `GcEvent::AfterCollect { kind: CycleCollector, ... }` 各一次（顺序固定 Before → After）

#### Scenario: remove_observer 后不再收到事件
- **WHEN** remove_observer(id) 后再 collect_cycles
- **THEN** 该 observer 不再被调用，`stats().observers` 减 1

### Requirement: Profiler hooks

#### Scenario: set_alloc_sampler 安装后每次 alloc 触发回调
- **WHEN** `set_alloc_sampler(Some(fn))` 后 alloc 3 次
- **THEN** sampler 被调用 3 次，每次接收 `&AllocSample { kind, size_bytes, timestamp_us }`

#### Scenario: set_alloc_sampler(None) 清除 sampler
- **WHEN** `set_alloc_sampler(None)` 后再 alloc
- **THEN** 不触发任何回调

#### Scenario: take_snapshot 在 RC 模式标记 reachable-only
- **WHEN** Phase 1 RcMagrGC 调用 `take_snapshot()`
- **THEN** 返回的 `HeapSnapshot.coverage == SnapshotCoverage::ReachableFromPinnedRoots`

#### Scenario: take_snapshot 反映 pinned roots 可达对象
- **WHEN** pin 一个含 2 个 slot（值都是 Object）的 Object 后 take_snapshot
- **THEN** snapshot.total_objects 至少 3（外层 + 2 内层）

#### Scenario: iterate_live_objects 不漏 root 可达对象
- **WHEN** pin 一个 Array 含 5 个 Object 元素，调 `iterate_live_objects(visitor)`
- **THEN** visitor 至少访问 6 次（Array + 5 Objects）

### Requirement: HeapStats（扩展字段）

#### Scenario: HeapStats 包含全部 7 个字段
- **WHEN** 调 `heap.stats()`
- **THEN** 返回 struct 包含：`allocations` / `gc_cycles` / `used_bytes` / `max_bytes` / `roots_pinned` / `finalizers_pending` / `observers`

## MODIFIED Requirements

### Requirement: trait MagrGC 形状（扩展）

**Before**：6 个方法（alloc_object / alloc_array / write_barrier / collect / collect_cycles / stats）。

**After**：~30 个方法分 10 个能力组，覆盖 MMTk porting contract 全部主要维度（roots / barriers / object model / collection / heap config / finalization / weak refs / observers / profiler / stats）。

## IR Mapping

无 IR 变化。

## Pipeline Steps

- [ ] Lexer
- [ ] Parser / AST
- [ ] TypeChecker
- [ ] IR Codegen
- [x] VM interp (consumes trait via `ctx.heap()`)
- [x] VM JIT helpers (consumes trait via `vm_ctx_ref(ctx).heap()`)
