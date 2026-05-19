# Design: multi-threading foundation

## Architecture

```
当前（单线程）：
  VmContext {                  ← Rc<RefCell> 字段 ×11，全部 !Send !Sync
    static_fields:     Rc<RefCell<Vec<Value>>>,
    lazy_loader:       Rc<RefCell<Option<LazyLoader>>>,
    call_stack:        Rc<RefCell<Vec<VmFrame>>>,
    pending_exception: Rc<RefCell<Option<Value>>>,
    native_types:      Rc<RefCell<HashMap<...>>>,
    native_libs:       Rc<RefCell<Vec<libloading::Library>>>,
    pinned_owned_buffers: Rc<RefCell<HashMap<u64, Box<[u8]>>>>,
    processes:         Rc<RefCell<HashMap<u64, ProcessSlot>>>,
    func_ref_slots:    Rc<RefCell<Vec<Value>>>,
    ...
  }

  GcRef<T> { inner: Rc<GcAllocation<T>> }   ← !Send !Sync
  GcAllocation<T> { inner: RefCell<T>, ... }

  trait MagrGC: Debug { ... }   ← 无 Send/Sync 边界

本 spec 后（多线程 ready，单线程行为不变）：
  VmCore {                     ← Send + Sync ✓
    static_fields:        Mutex<Vec<Value>>,
    static_field_index:   Mutex<HashMap<String, u32>>,
    type_registry:        RwLock<HashMap<...>>,           ← 读多写少
    lazy_loader:          Mutex<Option<LazyLoader>>,
    native_types:         RwLock<HashMap<...>>,
    native_libs:          Mutex<Vec<libloading::Library>>,
    pinned_owned_buffers: Mutex<HashMap<u64, Box<[u8]>>>,
    processes:            Mutex<HashMap<u64, ProcessSlot>>,
    gc:                   Arc<dyn MagrGC + Send + Sync>,
  }

  VmContext {                  ← 单线程视图；不要求 Send/Sync
    core:              Arc<VmCore>,                       ← 共享指针
    call_stack:        RefCell<Vec<VmFrame>>,             ← per-thread
    pending_exception: RefCell<Option<Value>>,            ← per-thread
    frame_guards:      RefCell<Vec<FrameGuard>>,          ← per-thread
    func_ref_slots:    RefCell<Vec<Value>>,               ← per-thread
  }

  GcRef<T> { inner: Arc<GcAllocation<T>> }   ← Send + Sync (when T: Send)
  GcAllocation<T> { inner: Mutex<T>, finalizer: Mutex<...> }

  trait MagrGC: Debug + Send + Sync { ... }   ← Send + Sync 边界
```

**关键观察**：
- `VmCore` 持有的字段都是**进程级别全局**的（static fields 全局唯一、type registry 全局唯一、processes / native libs 全局唯一）
- `VmContext` 字段都是**调用栈视角**的（每个执行流自己的 frame / pending exception / 等）
- 因此 VmCore 必须 Send + Sync（要跨线程共享），VmContext 不必（只在当前线程使用，跨线程时拷贝 Arc<VmCore> 给新 VmContext）

## Decisions

### Decision 1: 直接 rename `RcMagrGC` → `ArcMagrGC` vs 新增 `ArcMagrGC` 双 backend

**问题**：现有 `RcMagrGC` 是 GC 默认 backend，name 已暗示 Rc-based。Arc backing 替换后是延续 name 还是新 name？

**选项**：
- **A** rename `RcMagrGC` → `ArcMagrGC`：name 反映实际 backing；外部调用方（host embedding 用户）破坏；按 philosophy.md "不为破坏性顾虑而牺牲最佳方案" pre-1.0 允许
- **B** 新增 `ArcMagrGC`，`RcMagrGC` 留 deprecated alias：双 backend 共存一段时间；维护两套 backing 代码
- **C** 留 `RcMagrGC` name + 内部换 Arc backing：name 撒谎，未来读者疑惑

**决定**：**A** —— rename。pre-1.0 不为破坏性顾虑；host embedding 用户少（GC 不是公开稳定 API）；维护双 backend 代价远超 rename + 调用方更新。

### Decision 2: VmContext per-thread 字段是否保留 Rc/RefCell

**问题**：`call_stack` / `pending_exception` 等 per-thread 字段，每个 VmContext 实例独享，跨线程从来不需要——是否也要 Mutex 化以求形态统一？

**选项**：
- **A** 保留 `Rc<RefCell<T>>`（实际可简化为 `RefCell<T>` 因为 VmContext 自己持有，不需要 shared 引用计数）：单线程便宜；多线程时每个线程构造新 VmContext 自带这些字段
- **B** 升级到 `Mutex<T>`：单一形态，但单线程跑也付原子 cost

**决定**：**A** —— 保留单线程便宜形态。理由：per-thread 字段定义上不跨线程，付 atomic cost 是浪费。**额外简化**：既然 VmContext 自己持有，可以从 `Rc<RefCell<T>>` 降级到 `RefCell<T>`（去掉 Rc 引用计数层）—— 减少一次 atomic-free 的 Rc::clone。

> 进一步：`RefCell<T>` 也可以再降为 `UnsafeCell<T>` + 手动断言（编译器内部 VM 实践），但这是后续 perf 优化，本 spec 不做。

### Decision 3: Mutex 选型 — `std::sync::Mutex` vs `parking_lot::Mutex`

**问题**：标准库 Mutex 有 poisoning 机制 + 系统调用 fast path；parking_lot 更快、无 poisoning + 更精简 API。

**选项**：
- **A** `std::sync::Mutex`：标准库，零依赖；有 poisoning（panic 后毒化锁）
- **B** `parking_lot::Mutex`：依赖加 1 crate；无 poisoning；microbench ~2-5x faster on uncontended path
- **C** 抽象一层 `type GcMutex<T> = ...` 让选型可在 Cargo feature 切换

**决定**：**B** —— `parking_lot::Mutex`。理由：
1. 性能：GC 热路径 lock/unlock 是高频操作（每次 GcRef::borrow 都触发），原子 op 数量直接影响 baseline
2. Poisoning 在 VM 内部场景没价值（panic 后 ctx 已废，重启进程即可；不需要"读到锁残留状态"的语义）
3. 已在依赖图中（rust 生态 stable 多年）

verify `parking_lot` 是否已在 `Cargo.toml`：实施期检查；不在则新增依赖（条目落 implementations notes）。

### Decision 4: `MagrGC` trait 加 Send + Sync 是否需要 sub-trait

**问题**：直接 `pub trait MagrGC: Debug + Send + Sync` 强制所有实现 Send + Sync；还是引入 `MagrGCThreadSafe: MagrGC + Send + Sync` 区分？

**选项**：
- **A** 直接给主 trait 加边界：单一 trait，简单
- **B** sub-trait：保留单线程 GC backend 可能性

**决定**：**A** —— 直接加边界。z42 已决定走多线程路线（Send + Sync 是底座），保留单线程-only backend 没价值。

### Decision 5: `Ref<'a, T>` / `RefMut<'a, T>` 类型 alias

**问题**：`GcRef::borrow()` 现在返回 `std::cell::Ref<'_, T>`。切到 Mutex 后该返回 `MutexGuard<'_, T>`。直接换返回类型会破坏调用方签名。

**选项**：
- **A** 在 `gc::refs` 内定义 `pub type Ref<'a, T> = parking_lot::MutexGuard<'a, T>`（同样为 RefMut），调用方 type-alias 不变
- **B** 调用方全改类型签名

**决定**：**A** —— 用 type alias 把破坏面藏起来。调用方 `let x = gc_ref.borrow();` 仍然能用，编译期不需要改。这是把 Phase 3a "callsite 走 GcRef::* API 不需任何修改" 契约延续到 Phase 4a。

### Decision 6: Per-call lock 还是 read-write lock 区分

**问题**：`VmCore.type_registry` / `native_types` 是**读多写少**（运行时频繁查询类型，注册只在 module load 时）。要不要用 `RwLock` 取代 `Mutex`？

**选项**：
- **A** 全 Mutex：一致，简单
- **B** 读多写少字段用 RwLock：性能更好，但 lock 类型不一致增加心智负担

**决定**：**B** 部分采纳 —— type_registry / native_types 用 RwLock；其余 Mutex。理由：这俩是 hot read 路径，多线程下 Mutex 互斥读会序列化所有类型查询，对 GC marking / vcall dispatch 杀伤大。RwLock 写 lock 罕见，读 lock 廉价。

### Decision 7: GcRef `borrow_mut` 重入语义

**问题**：RefCell 的 `borrow_mut` 在递归调用时 panic。`Mutex` 在递归 lock 时 deadlock（非 reentrant）。语义不一致。

**选项**：
- **A** 接受 deadlock 行为：lock 是真 lock；recursive 调用方 bug 暴露
- **B** 用 `try_lock` + panic on already-locked：复刻 RefCell panic 语义
- **C** 用 reentrant lock（parking_lot 有 ReentrantMutex）：性能差

**决定**：**B** —— `GcRef::borrow_mut()` 内部用 `try_lock`，失败 panic（错误信息提示"recursive borrow_mut"）。理由：保留现有 panic 调试体验；避免 deadlock 难调试。

> 调用方影响：本 spec 实施期需要 verify 没有调用方依赖了"成功锁住第二次"的不健康行为。grep `borrow_mut.*borrow_mut` 应无结果（嵌套调用本身 RefCell 也会 panic，所以理论上没此 bug）。

## Implementation Notes

### Phase 划分（实施次序）

1. **Phase 1**：加 `VmCore`，把 6 个共享字段先搬过去（先 static_fields + type_registry + lazy_loader + native_types + native_libs + pinned_owned_buffers）；VmContext 通过 `&self.core.static_fields.lock()` 访问。`MagrGC` 边界**不动**这一 phase。所有测试需绿。
2. **Phase 2**：剩余共享字段（processes + gc_backend）搬入 VmCore。
3. **Phase 3**：`GcRef<T>` 切 Arc backing；`GcAllocation.inner` 切 Mutex；`MagrGC` trait 加 Send + Sync；rename `RcMagrGC` → `ArcMagrGC`。这是最大单步；compile 错误最多。
4. **Phase 4**：清理：删 unused `use Rc;` import；调用方 `.lock()` 形态规范化；docs 更新。
5. **Phase 5**：跑全套 GREEN；记录 baseline 性能（test-vm.sh wall-time）+ 与本 spec 前对比，验证 < 2x 退化。

> 每 phase 独立 commit + 全部测试绿；不囤积。

### type registry 的 RwLock 切换最微妙

[`Module.type_registry`](../../../../src/runtime/src/metadata/loader.rs#L499) 在 `build_type_registry` 时一次性构造完成；其他时间纯 read。本 spec 应该让它从 `HashMap` 变 `RwLock<HashMap>`，但实施期可能发现某些 callsite 把它当成 mutable 访问（即使语义上不该）—— 这种情况是历史 hack，应该改正而非加锁。

### 关于"VmContext clone 给新线程"

虽然本 spec 不引入 spawn API，但内部测试 `vm_core_is_send_sync` 之外，最好加一个集成测试：从 Rust test 直接 `std::thread::spawn(move || { ... })` 模拟一个 VmContext 跨线程使用场景——这给后续 stdlib threading spec 一个可验证的 baseline。

测试形态（伪代码）：

```rust
#[test]
fn vm_context_can_cross_thread_boundary() {
    let core = Arc::new(VmCore::new(...));
    let core_clone = Arc::clone(&core);
    
    let handle = std::thread::spawn(move || {
        let ctx = VmContext::new_with_core(core_clone);
        // 简单 read：访问 static_fields[0]
        let _ = ctx.core.static_fields.lock().get(0).cloned();
    });
    
    handle.join().unwrap();
}
```

### `GcRef::clone()` 频率

`Value::Array(GcRef<...>)` 每次 clone 都会 Arc::clone 一次。Value clone 在 VM 内是相对常见操作（赋值、传参、push 到 vec 等）。Rc::clone 是 `*count += 1`；Arc::clone 是原子 fetch_add。在 x86_64 上 uncontended atomic 大约 1-3ns，加上 cache line 影响。

预期影响：interp 整体吞吐 < 10% 退化。如果测出超 20% 退化，回滚到 spec 重新评估（design 的 baseline assertion 是 < 2x，但实际期望 < 1.1x）。

## Testing Strategy

- **GREEN 不回归**：stdlib 17 lib / VM e2e 312/312 / 编译器 1288 全部
- **新增 Send/Sync 编译期 assert**（`gc::heap_tests.rs`）：
  - `assert_send_sync::<VmCore>()`
  - `assert_send_sync::<GcRef<Vec<Value>>>()`
  - `assert_send_sync::<Arc<dyn MagrGC>>()`
- **新增 cross-thread integration test**（`runtime/tests/cross_thread_smoke.rs`）：构造 VmCore，分配 GcRef，跨线程 read。**不调用 z42 函数**（那是下一份 spec）。
- **Baseline benchmark**：手动测试 test-vm.sh wall-time 前 / 后对比，记入 tasks.md "实施期发现" 段
