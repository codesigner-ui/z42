# Design: Expand MagrGC to Full MMTk-Style Embedding Interface

## Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│  Embedding Host (C++ / Rust 应用)                                  │
│    - 注册 GcObserver  → 接收 BeforeCollect / AfterCollect / OOM    │
│    - 安装 alloc_sampler → 接收每次 AllocSample                      │
│    - pin_root / make_weak                                         │
│    - take_snapshot / iterate_live_objects                         │
│    - set_max_heap_bytes / pause / resume                          │
└──────────────────────────────────┬───────────────────────────────┘
                                   │ via VmContext.heap()
                                   ▼
┌──────────────────────────────────────────────────────────────────┐
│ trait MagrGC (gc/heap.rs) —— ~30 方法 / 10 能力组                  │
│   1. Allocation        4. Collection        7. Weak refs         │
│   2. Roots             5. Heap config       8. Observers         │
│   3. Write barriers    6. Finalization      9. Profiler          │
│                                            10. Stats             │
└──────────────────────────────────┬───────────────────────────────┘
                                   │ Box<dyn MagrGC>
                                   ▼
┌──────────────────────────────────────────────────────────────────┐
│ RcMagrGC (gc/rc_heap.rs) —— Phase 1 RC 后端                       │
│   inner: RefCell<RcHeapInner>                                    │
│     - stats: HeapStats                                           │
│     - roots: HashMap<RootHandle, Value>                          │
│     - frame_stack: Vec<FrameEntry>                               │
│     - observers: Vec<(ObserverId, Arc<dyn GcObserver>)>          │
│     - finalizers: HashMap<usize, FinalizerFn>  // *const _ as usize │
│     - alloc_sampler: Option<AllocSamplerFn>                      │
│     - pause_count: u32                                           │
│     - max_bytes: Option<u64>                                     │
│     - next_root_id / next_observer_id: u64                       │
└──────────────────────────────────────────────────────────────────┘

支持类型 (gc/types.rs):
  RootHandle / FrameMark / ObserverId / GcKind / GcEvent / GcObserver
  AllocSample / AllocKind / AllocSamplerFn / FinalizerFn
  CollectStats / WeakRef / HeapSnapshot / ObjectStats / SnapshotCoverage
```

## Decisions

### Decision 1: 接口形状直接对齐 MMTk porting contract

MMTk `VMBinding` 把 GC 抽象拆成多个 sub-trait（`ObjectModel` / `Scanning` /
`Collection` / `ActivePlan` / `ReferenceGlue`）。z42 体量小、单 mutator，不必
拆 sub-trait —— 把对应职责合到一个 `trait MagrGC` 里，按"能力组"在文件内分段
注释。这样新接手者读一个 trait 就能看全 GC 形状，且未来真要拆 sub-trait 时
切割面清晰（每段独立成 trait 即可）。

### Decision 2: 支持类型独立成 `gc/types.rs`

`heap.rs` 只放 trait + `HeapStats`；其它 ~15 个支持类型放 `gc/types.rs`。
原因：

- `heap.rs` 限定为"接口契约"文件，可单独阅读
- 支持类型集中后，新增字段（如 `GcEvent` 加新 variant）只改一个文件
- 文件行数控制（每个 < 300 行软限）

### Decision 3: Send + Sync bounds 加在 host 注入的 trait 对象上

虽然 `VmContext` 当前 `!Send`（RefCell），但 `GcObserver` / `GcObserverFn` /
`FinalizerFn` / `AllocSamplerFn` 加 `Send + Sync` 边界 —— 嵌入 host 通常需要
跨线程使用这些 callback（如把分配采样数据 push 到后台 metrics 线程）。
代价仅一行 trait bound，收益是 future-proof L3 async 多线程模型。

### Decision 4: RootHandle / ObserverId 用 u64 ID（vs 智能指针 Drop guard）

**选项**：
- A: 用 `RootHandle(NonNull<...>)` + `Drop` 自动 unpin
- B: 用 `RootHandle(u64)`，host 显式 `unpin_root`

**决定**：B。原因：
- A 在 RC 模式下与 `Rc<RefCell>` 借用规则冲突（Drop 时尝试借 RefCell 容易死锁）
- B 与 V8 `Persistent<T>::Reset()` / .NET `GCHandle.Free()` 风格一致 —— 显式管理在嵌入式 API 中更熟悉
- B 可零代价转 A（之后包一层 Drop guard 类型即可），反向不行

### Decision 5: Frame scope 用栈 + 帧 ID（不用 Drop guard）

`FrameMark(u32)` 标记入栈深度。`leave_frame(mark)` 弹掉所有 `mark` 之后 push 进来的 root。

**为什么不用 Drop guard**：同 Decision 4 —— 显式 enter/leave 与解释器循环结构匹配
（每个 stack frame 进入时 enter，退出时 leave），用 RAII 反而要求引入额外的生命周期。

### Decision 6: Snapshot / iterate 在 RC 模式标记 ReachableFromPinnedRoots

RC 缺乏"枚举所有存活对象"能力（Rust `Rc<T>` 没有全局注册）。Phase 1 实现策略：
- **从 pinned roots 出发递归 `scan_object_refs`**，去重 by `Rc::as_ptr`
- `HeapSnapshot.coverage = SnapshotCoverage::ReachableFromPinnedRoots`
- 文档明确：host 想完整 snapshot 必须先 pin 全部 root entry points；Phase 3 trace 实现后自动升级 `Full`

替代方案"维护全局 Weak<...> registry，每次 alloc 注册"被否决：每次 alloc 多一次 Weak 创建 + Mutex 锁，性能成本不可接受。

### Decision 7: Finalizer 仅注册不触发（Phase 1 stub）

RC 模式下精确触发 finalizer 需要在 Rc Drop 时回调，而 `Rc<RefCell<T>>` 的 Drop
不可拦截（除非把 `T` 包在自定义 wrapper struct 里 —— 那是 Phase 3 `GcRef<T>` 的事）。
Phase 1 实现：
- `register_finalizer` 把 callback 存入 `HashMap<usize, FinalizerFn>`，key 是 `Rc::as_ptr` as usize
- `cancel_finalizer` 移除 key
- **不会被自动触发**：明确文档为 Phase 1 限制
- `stats().finalizers_pending` 反映注册数（让 host 知道有多少 callback 在等）

这与 V8 在 `WeakCallback` 上的早期实现策略一致 —— 先暴露 API，真实触发由后续阶段补。

### Decision 8: WeakRef 内部用 enum 区分 Object/Array

`WeakRef` 不能用 `std::rc::Weak<dyn Any>`（Rust 没有 dyn Weak）。改用 enum：

```rust
pub struct WeakRef { inner: WeakRefInner }
enum WeakRefInner {
    Object(std::rc::Weak<RefCell<ScriptObject>>),
    Array(std::rc::Weak<RefCell<Vec<Value>>>),
}
```

- `make_weak` 在 Object/Array 上 match 出对应 Rc，调 `Rc::downgrade`
- `upgrade_weak` 在 enum variant 上 match，调 `Weak::upgrade`，包成对应 Value variant
- 原子值（I64 / Str / ...）`make_weak` 返回 None —— 无意义弱引用

### Decision 9: Observer / Sampler 触发位置

- `BeforeCollect` / `AfterCollect`：在 `collect_cycles()` 与 `force_collect()` 中各发一对
- `AllocationPressure` / `NearHeapLimit`：在 alloc 前检查，超阈值时发一次（去重 by simple flag）
- `OutOfMemory`：当 `max_bytes` 设置且 alloc 会越界时发；本次仍允许分配（RC 不强制 OOM，只通知）—— 真实 OOM 拒绝由 Phase 3 实现
- `FinalizerScheduled`：Phase 1 stub 不发；Phase 3 实现真实调度时发

### Decision 10: HeapStats 字段一次扩到位

```rust
pub struct HeapStats {
    pub allocations:        u64,
    pub gc_cycles:          u64,
    pub used_bytes:         u64,           // approximation in RC: monotonic累加
    pub max_bytes:          Option<u64>,
    pub roots_pinned:       u64,
    pub finalizers_pending: u64,
    pub observers:          u64,
}
```

`used_bytes` 在 RC 模式下只增不减（dealloc 不可观察）—— 文档说明，Phase 3 trace
后变精确。这与 V8 早期 `HeapStatistics::used_heap_size` 在 incremental marking
未结束时的近似返回值语义一致。

## Implementation Notes

### gc/types.rs（新文件）

```rust
use std::cell::RefCell;
use std::sync::Arc;
use std::collections::HashMap;
use crate::metadata::{ScriptObject, Value};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct RootHandle(pub u64);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FrameMark(pub u32);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct ObserverId(pub u64);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GcKind { Minor, Full, CycleCollector }

#[derive(Debug, Clone)]
pub enum GcEvent {
    BeforeCollect      { kind: GcKind, used_bytes: u64 },
    AfterCollect       { kind: GcKind, freed_bytes: u64, pause_us: u64 },
    AllocationPressure { used_bytes: u64, limit_bytes: u64 },
    NearHeapLimit      { used_bytes: u64, limit_bytes: u64 },
    OutOfMemory        { requested_bytes: u64, limit_bytes: u64 },
}

pub trait GcObserver: std::fmt::Debug + Send + Sync {
    fn on_event(&self, event: &GcEvent);
}

#[derive(Debug, Clone)]
pub enum AllocKind {
    Object { class: String },
    Array  { elem_count: usize },
}

#[derive(Debug, Clone)]
pub struct AllocSample {
    pub kind:         AllocKind,
    pub size_bytes:   usize,
    pub timestamp_us: u64,
}

pub type AllocSamplerFn = Arc<dyn Fn(&AllocSample) + Send + Sync>;
pub type FinalizerFn    = Arc<dyn Fn() + Send + Sync>;

#[derive(Debug, Clone, Copy, Default)]
pub struct CollectStats {
    pub freed_bytes: u64,
    pub pause_us:    u64,
    pub kind:        Option<GcKind>,
}

#[derive(Debug, Clone)]
pub struct WeakRef { pub(crate) inner: WeakRefInner }

#[derive(Debug, Clone)]
pub(crate) enum WeakRefInner {
    Object(std::rc::Weak<RefCell<ScriptObject>>),
    Array (std::rc::Weak<RefCell<Vec<Value>>>),
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct HeapStats {
    pub allocations:        u64,
    pub gc_cycles:          u64,
    pub used_bytes:         u64,
    pub max_bytes:          Option<u64>,
    pub roots_pinned:       u64,
    pub finalizers_pending: u64,
    pub observers:          u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SnapshotCoverage {
    Full,
    ReachableFromPinnedRoots,
}

impl Default for SnapshotCoverage {
    fn default() -> Self { Self::ReachableFromPinnedRoots }
}

#[derive(Debug, Clone, Copy, Default)]
pub struct ObjectStats { pub count: u64, pub bytes: u64 }

#[derive(Debug, Clone, Default)]
pub struct HeapSnapshot {
    pub objects_by_type: HashMap<String, ObjectStats>,
    pub total_objects:   u64,
    pub total_bytes:     u64,
    pub timestamp_us:    u64,
    pub coverage:        SnapshotCoverage,
}
```

### gc/heap.rs trait 扩展形状

```rust
pub trait MagrGC: std::fmt::Debug {
    // 1. Allocation
    fn alloc_object(&self, td: Arc<TypeDesc>, slots: Vec<Value>, native: NativeData) -> Value;
    fn alloc_array(&self, elems: Vec<Value>) -> Value;

    // 2. Roots
    fn pin_root(&self, value: Value) -> RootHandle;
    fn unpin_root(&self, handle: RootHandle);
    fn enter_frame(&self) -> FrameMark;
    fn leave_frame(&self, mark: FrameMark);
    fn for_each_root(&self, visitor: &mut dyn FnMut(&Value));

    // 3. Write barriers (default no-op)
    fn write_barrier_field      (&self, _o: &Value, _slot: usize, _new: &Value) {}
    fn write_barrier_array_elem (&self, _a: &Value, _idx:  usize, _new: &Value) {}

    // 4. Object Model
    fn object_size_bytes (&self, value: &Value) -> usize;
    fn scan_object_refs  (&self, value: &Value, visitor: &mut dyn FnMut(&Value));

    // 5. Collection control
    fn collect        (&self) {}
    fn collect_cycles (&self) {}
    fn force_collect  (&self) -> CollectStats;
    fn pause          (&self);
    fn resume         (&self);

    // 6. Heap config
    fn set_max_heap_bytes (&self, max: Option<u64>);
    fn used_bytes         (&self) -> u64;

    // 7. Finalization
    fn register_finalizer (&self, value: &Value, fin: FinalizerFn);
    fn cancel_finalizer   (&self, value: &Value);

    // 8. Weak references
    fn make_weak    (&self, value: &Value) -> Option<WeakRef>;
    fn upgrade_weak (&self, weak:  &WeakRef) -> Option<Value>;

    // 9. Event observers
    fn add_observer    (&self, observer: Arc<dyn GcObserver>) -> ObserverId;
    fn remove_observer (&self, id: ObserverId);

    // 10. Profiler
    fn set_alloc_sampler   (&self, sampler: Option<AllocSamplerFn>);
    fn take_snapshot       (&self) -> HeapSnapshot;
    fn iterate_live_objects(&self, visitor: &mut dyn FnMut(&Value));

    // 11. Stats
    fn stats(&self) -> HeapStats;
}
```

### RcMagrGC 内部状态（重构）

```rust
#[derive(Default)]
struct RcHeapInner {
    stats:              HeapStats,
    roots:              HashMap<RootHandle, Value>,
    frame_stack:        Vec<u32>,             // depth at each frame entry
    frame_pins:         Vec<Vec<RootHandle>>, // pins added in each frame
    observers:          Vec<(ObserverId, Arc<dyn GcObserver>)>,
    finalizers:         HashMap<usize, FinalizerFn>,
    alloc_sampler:      Option<AllocSamplerFn>,
    pause_count:        u32,
    next_root_id:       u64,
    next_observer_id:   u64,
    near_limit_warned:  bool,
}

#[derive(Debug, Default)]
pub struct RcMagrGC {
    inner: RefCell<RcHeapInner>,
}
```

`alloc_object` / `alloc_array` 实现要点：
1. check pause；如未暂停继续
2. 计算 size，update `used_bytes`
3. 检查是否超 `max_bytes` → 发 `OutOfMemory` / `NearHeapLimit` 事件
4. 实际构造 `Rc<RefCell<...>>`
5. 触发 `alloc_sampler`（如安装）
6. bump `allocations`

`force_collect` / `collect_cycles` 触发观察者：
- emit `BeforeCollect { kind, used_bytes }`
- 实际工作（Phase 1 RC: no-op，仅 bump gc_cycles）
- emit `AfterCollect { kind, freed_bytes: 0, pause_us: 0 }`

`take_snapshot` / `iterate_live_objects`：
- 从 `roots` HashMap 出发遍历
- 用 `HashSet<usize>` 记 `Rc::as_ptr` as usize 防止环遍历死循环
- `take_snapshot` 按 `type_desc.name` 累加 `ObjectStats`
- `iterate_live_objects` 直接对每个唯一对象调 visitor

## Testing Strategy

- **rc_heap_tests.rs** 扩展（约 40+ 测试）：
  - Roots：pin/unpin、enter/leave_frame 配对、for_each_root
  - Object model：size_bytes、scan_object_refs 数量正确
  - Collection：force_collect 返回 stats、pause/resume 行为
  - Heap config：set_max_heap_bytes、NearHeapLimit 事件
  - Finalization：register/cancel 计数
  - Weak refs：make_weak (Object/Array/原子三类)、upgrade 成功 + 失败
  - Observers：add/remove、collect_cycles 触发 Before/After
  - Profiler：sampler 触发次数、snapshot 反映 reachability、iterate 不漏 root 可达
- **heap_tests.rs**：trait 默认方法 no-op 验证扩展
- **集成验证**：现有 dotnet test + golden test 全绿（行为零变化 —— 新接口都是 host-side，不影响 script 执行）
