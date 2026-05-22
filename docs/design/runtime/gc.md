# z42 GC 子系统 —— MagrGC

> 抽取自 `vm-architecture.md` "GC 子系统"章（2026-05-22 split-gc-doc）。
> 文档体量已超过 vm-architecture.md 一半，独立成章方便阅读 + 后续 GC
> 专项迭代直接更新此文。vm-architecture.md 保留一段简介 + 跳转链接。


## 接口形状（嵌入式宿主友好版）

z42 VM 的 GC 抽象由 `crate::gc::MagrGC` trait 定义，全面对齐
[MMTk](https://www.mmtk.io/) `VMBinding` porting contract（OpenJDK / V8 / Julia /
Ruby / RustPython 的事实标准 GC 抽象）。trait 在单文件内按"能力组"组织
~30 个方法，未来如需切割成 sub-trait（参考 MMTk 的 `ObjectModel` /
`Scanning` / `Collection` / `ReferenceGlue` 拆分）切割面清晰。

| 能力组 | 主要方法 | 用途 |
|-------|---------|------|
| 1. Allocation | `alloc_object` / `alloc_array` | 脚本驱动堆分配 |
| 2. Roots | `pin_root` / `unpin_root` / `enter_frame` / `leave_frame` / `for_each_root` | host pin + frame scope + GC scan |
| 3. Write barriers | `write_barrier_field` / `write_barrier_array_elem` | Phase 2+ 用，默认 no-op |
| 4. Object Model | `object_size_bytes` / `scan_object_refs` | trace / snapshot 基础设施 |
| 5. Collection | `collect` / `collect_cycles` / `force_collect` / `pause` / `resume` | GC 控制 |
| 6. Heap config | `set_max_heap_bytes` / `used_bytes` | 堆上限 / 用量 |
| 7. Finalization | `register_finalizer` / `cancel_finalizer` | 析构回调（Phase 1 仅注册不触发）|
| 8. Weak refs | `make_weak` / `upgrade_weak` | 弱引用 |
| 9. Observers | `add_observer` / `remove_observer` | GcEvent 订阅（Before/After Collect / NearHeapLimit / OOM）|
| 10. Profiler | `set_alloc_sampler` / `take_snapshot` / `iterate_live_objects` | 分配采样 + 堆快照 + 存活遍历 |
| 11. Stats | `stats` | HeapStats 快照（7 字段）|

`VmContext` 持有 `Box<dyn MagrGC>`（与 `static_fields` / `lazy_loader` 同 ownership
模型）；所有脚本驱动分配走 `ctx.heap().alloc_*(...)`；JIT helper 通过
`vm_ctx_ref(ctx).heap().alloc_*(...)` 调用同一接口；嵌入 host 通过同一
`heap()` 入口访问 root / observer / profiler 等所有能力。

命名 **MagrGC** 取自《银河系漫游指南》中的 **Magrathea** —— 那颗专门建造
定制行星的传奇世界，与"管理对象生命周期"主题契合。

## Phase 1：RcMagrGC（已落地）

`crate::gc::RcMagrGC` 是 Phase 1 默认后端，**底层引用计数行为等价迁移前的直接
`Rc::new(RefCell::new(...))` 构造**，但 host-side 嵌入接口完整：

- `Value::Object` / `Value::Array` 形状不变
- `Rc::ptr_eq` 引用相等语义保留
- `RefCell` 运行时借用检查保留
- 内部状态由 `RefCell<RcHeapInner>` 持有：roots HashMap、frame_pins 栈、
  observer 列表、finalizer 表、alloc_sampler、pause counter、ID 生成器等
- `alloc_*` 通用通路 `record_alloc`：bump stats → 压力检查 → sampler 触发
- 事件分发：先 snapshot observer 列表再调用，避免回调重入引发 borrow 冲突

**已知限制（Phase 3a/3b/3c/3d/3d.1/3f/3e/3f-2/3-OOM 后）**：（无）

GC 子系统主功能至此完整。所有原始限制已解决，可投产。

## Safepoint 协议（add-gc-safepoint, 2026-05-20）

多线程引入（add-threading-stdlib）后，GC scanner 通过 `vm_contexts` 注册表
对每个 VmContext 走 raw `frame.regs` / `frame.env_arena` 指针扫 root —— 但 worker
线程同时在跑 interp 指令、改写 regs，构成 Rust 内存模型层 data race。Safepoint
协议引入 stop-the-world 屏障：

```
VmCore {
    gc_phase:     Mutex<GcPhase>,     // Idle / Requested / Marking
    gc_phase_cv:  Condvar,
    parked_count: AtomicUsize,        // mutator parked 数（不含 collector）
}

enum GcPhase { Idle, Requested, Marking }
```

**Mutator 侧**：interp dispatch loop 在三类位置调 `crate::gc::safepoint::check_safepoint(ctx)`：

| 位置 | 理由 |
|------|------|
| `exec_function` 入口 | 新 spawn 的 worker 在执行任何指令前先 yield 给未决 GC |
| 后向 branch（`Br` / `BrCond` target ≤ 当前 block_idx）| 循环回边 = 长流程的天然 yield 点 |
| `Call` / `CallIndirect` 返回后 | callee 跑完返回本帧时检查；长 callee 的 GC 请求被父帧捕获 |

fast path（Idle）：一次 Mutex lock + 一次 enum compare。slow path（Requested/Marking）：
`parked_count.fetch_add(1)` + `gc_phase_cv.notify_all()`（唤醒等阈值的 collector）+
`gc_phase_cv.wait()`（等回 Idle）+ `parked_count.fetch_sub(1)`。

**Collector 侧**：`crate::gc::safepoint::request_gc_pause(ctx) -> GcPauseGuard`
RAII guard：

1. 写 `gc_phase = Requested`
2. 循环等 `parked_count >= vm_contexts.len() - 1`（不含 collector 自身），重新读
   `vm_contexts.lock().len()` 每轮以容忍 mid-pause 注册的新 VmContext
3. 写 `gc_phase = Marking`
4. caller 跑 mark + sweep
5. Drop 时写 `gc_phase = Idle` + `notify_all()` 释放所有 mutator

**接入点**：`corelib/gc.rs` 的 `builtin_gc_collect` / `builtin_gc_force_collect`
（即 z42 `Std.GC.Collect()` / `ForceCollect()`）持 RAII guard 调
`heap.collect_cycles()` / `force_collect()`。

**v0 范围**（已记入 design.md Decisions）：

| 维度 | v0 行为 | 后续 spec |
|------|---------|----------|
| JIT-mode safepoint | ✅ 已落地（add-gc-safepoint-jit 2026-05-21）：JIT translate 在 function entry / backward Br / BrCond / `Call` / `CallIndirect` 返回后共 4 类 site emit `jit_check_safepoint` helper call。trampoline 调用 `gc::safepoint::check_safepoint`，与 interp 协议完全对齐；JIT-mode multi-thread workloads 不再死锁 | — |
| Auto-threshold (`maybe_auto_collect`) | ✅ Safepoint-aware（add-gc-safepoint-auto-threshold 2026-05-20）：trip 时 set `VmCore.needs_auto_collect: Arc<AtomicBool>` flag 而非 inline `collect_cycles()`；下个 `check_safepoint(ctx)` 用 `swap(false, AcqRel)` claim 后 safepoint-wrapped collect。trait 加默认 no-op `set_external_needs_collect_flag`；ArcMagrGC 内部 `Mutex<Option<Arc<AtomicBool>>>`，VmCore 构造后 wire。flag 未装时 fallback inline（GC 单测路径不变） | — |
| 检查频率节流 | ✅ Counter-throttled fast path（add-gc-safepoint-counter-throttling 2026-05-21）：`VmContext.safepoint_skip: AtomicU32`，`check_safepoint` fast path 单 `fetch_sub` (~3-5ns)；每 N=1024 次（默认）才走 slow path（Mutex lock + 真正的 phase + auto-collect drain）。`Z42_SAFEPOINT_THROTTLE` env override（设 1 = disable throttling）。`VmContext::force_safepoint()` 公共 API 供 test / embedder 强制下次走 slow path。trampoline `jit_check_safepoint` 自动透明受益（同 fn）。GC pause latency 上限 = N × 单 iter (~50ns) ≈ 50us，远小于实际 collect 时间 | — |
| 多 collector 仲裁 | ✅ 已落地（add-multi-collector-arbitration 2026-05-21）：`VmCore.collector_active: AtomicBool` + `request_gc_pause` 返 `Option<GcPauseGuard>`，前置 CAS claim；失败 collector 自动 park-as-mutator 返 `None`。`GcPauseGuard::drop` 释放 collector_active 让下一个 claim 通过。`Std.GC.Collect()` / `ForceCollect()` 在另一 collector active 时静默 no-op（best-effort 语义同 C# / Java）。4-worker auto_collect 测试 + 2-thread 显式 collect 仲裁测试都通过 | — |
| 并发 mark/sweep | 仍单线程；safepoint 是前置条件 | `add-concurrent-gc`（Phase A 性能轨道）|

多线程 workloads 推荐用显式 `Std.GC.Collect()` 触发；或将 `max_bytes` 配
足够大以避免 auto-collect 在 contended 路径触发。

> **2026-04-29 add-heap-registry（Phase 3b 完成）**：snapshot/iterate `Full` 覆盖。
>
> **2026-04-29 add-cycle-breaking-collector（Phase 3c 完成）**：环引用泄漏
> + `used_bytes` 单调递增两项限制解决 —— 初始实现走 Bacon-Rajan trial-deletion
> 算法：mark from pinned roots → `tentative[v] = strong_count - 1` 扣减集合
> 内部引用 → `tentative == 0` 清空内部 slots → alive_vec drop 时 Rc 链完成释放。
>
> **2026-05-21 add-mark-sweep-collector（A2 完成）**：trial-deletion 升级为
> 标准 mark-sweep（见下文 ["GC 后续迭代规划" A2 段](#a2-纯-mark-sweep已落地2026-05-21)）。
> O(N²) → O(reachable)；语义切到纯 tracing：Rust-local `Value` 强引用**不再
> 隐式作为 root**，embedder 必须 `pin_root` 显式标记保留对象。算法路径
> 仅 2 行（`mark_phase` + `sweep_phase`），断环逻辑沿用 `break_cycle_value`。
>
> **2026-04-29 add-finalizer-and-auto-collect（Phase 3d 完成）**：
> - **Finalizer 真触发**：`run_cycle_collection` 断环前从 finalizers map remove +
>   one-shot 调用回调（在 break_cycle_value 之后、alive_vec drop 之前 dispatch）
> - **内存压力自动 collect**：`alloc_object` / `alloc_array` 后调
>   `maybe_auto_collect` —— `used >= 90% max_bytes` 且距上次 auto-collect
>   增长 >= 10% limit 时自动触发 `collect_cycles`
> - **`near_limit_warned` 自动 reset**：collect 后若 `used` 已降到阈值以下
>   reset，让下次跨阈值能再发 `NearHeapLimit` 事件

### GC mode selection (add-concurrent-gc, 2026-05-22)

`ArcMagrGC` 现在支持运行时选择 GC 算法。Default 仍是 STW mark-sweep
（与 add-mark-sweep-collector 落地后行为完全一致）；ConcurrentMarkSweep
模式作为可选 opt-in 提供。

**切换方式：**

- **Env var**：进程启动前设 `Z42_GC_MODE=concurrent`（或 `=stw`，与
  不设等价）。无法识别的值回退到 stw 并 stderr 警告。
- **API**：运行期调 `heap.set_mode(GcMode::ConcurrentMarkSweep)`。下次
  `collect_cycles_with_context` 起生效；进行中的 collect 完成时仍按原
  模式（per spec scenario）。
- **生产入口**：`safepoint::check_safepoint_slow` 的 auto-collect 路径
  + `Std.GC.Collect()` builtin 都改走 `collect_cycles_with_context`，
  由 heap 内部按 mode 分发。

**当前可选模式：**

| Mode | 描述 | 何时用 |
|------|------|--------|
| `StwMarkSweep` (default) | 一次性停世界跑 mark + sweep | 单线程、对 throughput 敏感、所有 workload |
| `ConcurrentMarkSweep` | STW root 快照 → mutator 继续跑 + barrier shade → 终止 handshake STW → STW sweep | 多线程 + 对 pause time 敏感 |

**遇到 bug 的回退路径**：`Z42_GC_MODE=stw` 强制走默认稳定路径。生产报
错优先 fallback STW 看是否复现，把 bug 定位到 concurrent 路径还是更
底层（trait / barrier / safepoint）。

### Concurrent mark protocol (add-concurrent-gc P4, 2026-05-22)

ConcurrentMarkSweep 模式下 `collect_cycles_with_context` 跑 6 个阶段：

```text
1. request_gc_pause                    [STW]   collector_active=true, phase=Marking
   ↓
2. snapshot_roots_into_mark_queue       [STW]   pinned_roots + external_scanner → mark + enqueue
   ↓
3. yield_to_concurrent_marking          [STW]   phase=ConcurrentMarking, mutators wake
   ↓
4. drain_mark_queue (collector thread)  [concurrent]   BFS through gray queue;
                                                       mutators run; barriers push gray
   ↓
5. request_handshake_pause              [STW]   phase=Marking, wait for re-park
   ↓
6. drain_mark_queue (residual)          [STW]   catch barrier pushes during handshake race window
   ↓
7. sweep_phase                          [STW]   walk registry, free unmarked
   ↓
8. GcPauseGuard::drop                   [STW]   phase=Idle, collector_active=false, mutators resume
```

**Tricolor 不变量**：incremental update（Dijkstra）。Barrier override
（add-write-barriers 落地的 call site）只在 ConcurrentMarkSweep 模式下
shade：写入 heap-ref 时 `mark_if_unmarked(new)`，CAS 成功则 push 到
`mark_queue`。保证 "no black-to-white edge" —— 任何 mutator 写入的
new 都至少是 gray，最终被 collector traced。

**Termination invariant**：drain 当 queue 空。但 barrier 可能在 STW
handshake 触发**前的瞬间**push 新 gray —— 阶段 5/6 的 handshake →
residual drain 安全捕获。`request_handshake_pause` 等待 mutators
park（同 `request_gc_pause` 模式），park 后所有 mutator 已观察到 phase
= Marking，不会再写 → 此时 queue 空 = 真终止。

**为什么 Relaxed atomic 对 marked bit 仍然安全**：

- 每个 mark 操作是 CAS（atomic + idempotent）—— 多 thread race 时只有
  一个 transitions 0→1，其它返回 false 跳过 enqueue（不会重复 trace）
- BFS 是单调的（一个 cycle 内 mark bit 只从 0 → 1，永远不反向）
- 跨 thread 可见性通过 (a) `parking_lot::Mutex` on mark_queue 的
  Acquire/Release，(b) STW handshake 转换时 `gc_phase` Mutex 的
  Acquire/Release。这两个 sync point 足以建立 happens-before

未来 audit 若移除其中任一 sync point（比如改用 lock-free queue），
必须重新评估 Acquire/Release ordering。

**Phase 状态机**：

```text
STW path (mode = StwMarkSweep):
  Idle → Requested → Marking → Idle

Concurrent path (mode = ConcurrentMarkSweep):
  Idle → Requested → Marking (snapshot) → ConcurrentMarking (drain) →
  Marking (handshake + sweep) → Idle
```

`park_until_idle` 的等待条件从 `!Idle` 改为 `Requested | Marking`
—— ConcurrentMarking 不让 mutator park。

### Finalizer contract (add-custom-allocator, 2026-05-22)

Finalizer 触发时机随 backing 切换变化：

**当前契约（post-add-custom-allocator）**: finalizer **不**在 `GcRef`
出 scope 时触发（`GcRef::drop` 是 no-op，无 refcount）。两条触发路径：

1. **自动**：GC `sweep_phase` 找到 unreachable 对象 → take finalizer →
   触发 → tombstone slot。时机不可控（依赖下次 collect）。
2. **手动**：用户显式调 `Std.GC.Finalize(target)` → 立即 take + 触发
   finalizer → tombstone slot。匹配 .NET `IDisposable.Dispose()` /
   Java `AutoCloseable.close()` 语义。

**RAII 模式建议**：

- 资源类型（文件 handle / socket / FFI handle）暴露显式 `Close()` /
  `Dispose()` 方法；用户主动调用
- 不依赖 finalizer 做"scope exit 立即释放" —— 该模式在 z42 不成立
- finalizer 是 **safety net**（防泄漏），不是即时释放机制

**Strong reference 检测**（design D5）: 显式 `Std.GC.Finalize` 后，
其他 strong reference 之后 `borrow` 会 `debug_assert!` panic（generation
mismatch detection — debug build 立即报错；release build 由后续
generation 检查 enforce）。

**Pre-spec 历史契约**（archive reference）: Arc backing 下 `GcRef::drop`
触发 refcount 减 1；最后一个 ref drop 时 `GcAllocation::Drop` 自动触发
finalizer。这条契约已废弃；现有 stdlib `ProcessHandle.Drop(slotId)`
本来就是显式 dispose，已经匹配新契约。

### GC heap backing (add-custom-allocator, 2026-05-22)

`ArcMagrGC` 现在两个 region 持有所有堆引用对象：

- `region_object: Mutex<Region<ScriptObject>>` —— `Value::Object` 后端
- `region_array: Mutex<Region<Vec<Value>>>` —— `Value::Array` 后端

`Region<T>` 内部 `Vec<Box<[MaybeUninit<RegionEntry<T>>; 256]>>` chunked
storage —— chunks 是 `Box` 单位故 entry 地址在 chunk 生命周期内**绝对稳定**
（`GcRef::as_ptr` 身份哈希契约的物理基础）。Alloc 走 free-list pop 优先
（reuse tombstoned slot，preserve bumped generation）+ bump pointer。

`GcRef<T>` 是 12B 的 `NonNull<RegionEntry<T>>` + `u32 generation` handle：
- Clone = memcpy 12 字节，**零原子 op**（vs 之前 `Arc::clone` 每次
  `fetch_add` 2-4 ns）
- Drop = no-op（无 refcount）
- borrow / borrow_mut 走 `RegionEntry.value: Mutex<T>` 阻塞 lock
  （同 add-multithreading-foundation 并发模型）
- `ptr_eq` 比 NonNull + generation（stale-vs-fresh 区分）
- `as_ptr` 返回 entry 内 `Mutex<T>` 稳定地址

**WeakGcRef** 同 handle shape + generation snapshot。`upgrade` 检查
`alive && generation matches` —— ABA-safe：被 tombstone 后即使槽位
重用，generation 也 mismatch 不会假阳性 upgrade。

**Sweep 路径**：`sweep_phase` 直接 walk `region_object` + `region_array`
的 `iterate_alive`；找 unmarked `is_marked() == false` → 取 finalizer
（mutex take）→ 触发 finalizer → tombstone（alive=false, generation++,
push free_list）。`heap_registry: Vec<WeakRef>` 字段已删除 —— region
就是 authoritative liveness store。

**生命周期契约**：`GcRef` 不能 outlive 它所指 `Region` 所属的
`ArcMagrGC`。z42 现有架构所有 GcRef 都活在 VmContext 范围内，契约
天然满足。Embedder 需注意 drop order。

### Write barrier contract (add-write-barriers, 2026-05-21)

`MagrGC` trait 包含两个 write-barrier 钩子：

```rust
fn write_barrier_field(&self, owner: &Value, slot: usize, new: &Value);
fn write_barrier_array_elem(&self, arr: &Value, idx: usize, new: &Value);
```

默认实现（含 `ArcMagrGC` STW mark-sweep）是 no-op；后续
`add-generational-gc` / `add-concurrent-gc` 落地时 override 为 card-marking /
SATB 真实逻辑。Phase 1 落地的是**call-site wiring + 调用契约**，运行时
行为零变化（trait override 为 no-op，仅 `#[cfg(test)]` 时 dispatch 到
test-only `BarrierObserver`）。

**Caller 契约**（interp `field_set` / `array_set`、JIT `jit_field_set` /
`jit_array_set`）:

1. **Filter at call site**: 只在 `new.is_heap_ref()` 时 invoke barrier。
   Primitive (`I64 / F64 / Bool / Char / Str / Null / FuncRef / PinnedView /
   StackClosure / Ref::Stack`) 写入 skip — 这些既不参与 cross-region 引用
   也不参与 cross-generation 引用，barrier dispatch 是纯浪费。`is_heap_ref()`
   是 `Value` 上 inherent 方法，与 `trace_children` 平行：一个判定，一个遍历。
2. **Post-write order**: barrier 在 slot/elem 写之后调用。card-marking
   自然 fit；若未来 A4 用 SATB 需要 pre-barrier（看 *旧* 值），扩 trait
   加 `write_barrier_field_pre(&owner, slot, old: &Value, new: &Value)`，
   不强迫所有 backend 都付 pre-barrier 代价。
3. **Lock released before call**: 调用前 `drop(borrowed)` 释放
   `owner.slots` / `arr` 的 inner-`Mutex` lock，让未来 override 可以
   re-borrow `owner` 而不死锁。
4. **IC fast path 也 dispatch**: FieldSet 的 inline cache 命中路径也必须
   走 barrier，否则未来 generational/concurrent 在 hot code 漏写 → mark
   queue 不完整 → UAF。这条规则在 `interp::field_set` /
   `jit_field_set` 内 inline 多个写入点都加了 dispatch；六个写入点对应六个
   `write_barrier_field` call（fast + slow + 无-IC，interp 和 JIT 各一套）。
5. **StaticSet 不 dispatch**: static fields 是 GC root，"old → new"
   写永远在 root，不存在 cross-region/cross-generation 关心的场景。

**Override 契约**:

- 实现可以 `debug_assert!(new.is_heap_ref())` 检测 caller 是否漏 filter
- 不能假设 `owner` 持有 inner-`Mutex` lock；可以自由 `owner.borrow()` /
  `owner.borrow_mut()`（caller 已 drop）
- Phase 1 default no-op 不修改 `HeapStats`；这条契约保留下来意味着
  pure-tracing / generational override 也不应改 `HeapStats`（stats 反映
  alloc/free，不反映 barrier dispatch 次数）

> **2026-04-29 extend-native-fn-signature（Phase 1.5 完成）**：原限制"corelib 直构未迁移"
> 已解决 —— `NativeFn` 签名扩展为 `fn(&VmContext, &[Value]) -> Result<Value>`，全部 ~55
> 个 builtin 走 ctx 传参；`__obj_get_type` / `__env_args` 走 `ctx.heap().alloc_*(...)`。
> 全代码库无任何 `Rc::new(RefCell::new(...))` 直构，仅 `gc/rc_heap.rs` 内部权威实现保留
> （即所有分配都通过 GC 接口的唯一物理收口点）。

> **2026-04-29 remove-dead-value-map**：原 Phase 1 限制 #3（`alloc_map()` 占位）
> 与 `Value::Map` variant 一并删除 —— 自从 2026-04-26 extern-audit-wave0 把
> `Std.Collections.Dictionary` 改为纯脚本类后，`Value::Map` 已无创建路径。`value_to_str`
> 同步改为 exhaustive match，编译期强制覆盖所有 Value variant。

## Phase 路线（持续迭代）

| Phase | 内容 | 状态 |
|-------|------|------|
| **Phase 1** | trait MagrGC 接口 + RcMagrGC 实现 + 6 个脚本驱动 callsite 收口 | ✅ 2026-04-29 add-magrgc-heap-interface |
| **Phase 1 (扩展)** | trait 全面对齐 MMTk porting contract（10 能力组 ~30 方法）+ host-side 嵌入接口完整实现 | ✅ 2026-04-29 expand-magrgc-mmtk-interface |
| **Phase 1.5** | corelib `NativeFn` 签名扩展带 `&VmContext` + corelib 内剩余 Rc::new 迁移 | ✅ 2026-04-29 extend-native-fn-signature |
| **Phase 2** | （**跳过**）—— 直接进 Phase 3 mark-sweep，避免双重智能指针 churn | ⏭ 跳过 |
| **Phase 3a** | `GcRef<T>` / `WeakGcRef<T>` 不透明句柄抽象（backing 仍 `Rc<RefCell<T>>`，行为零变化）| ✅ 2026-04-29 introduce-gcref-handle |
| **Phase 2** | 环检测真实实现（dumpster 2.0 集成 / 自研 Bacon-Rajan 二选一） | 📋 待立项 |
| **Phase 3b** | Heap registry（`Vec<WeakRef>` 让 GC 枚举所有存活对象）+ snapshot/iterate Full coverage | ✅ 2026-04-29 add-heap-registry |
| **Phase 3c** | Trial-deletion 环回收器（保留 RC backing，断环让 Rc 链 Drop） | ✅ 2026-04-29 add-cycle-breaking-collector |
| **Phase 3d** | Finalizer 真触发（cycle collect 时调度）+ 内存压力自动 collect + near_limit_warned 自动 reset | ✅ 2026-04-29 add-finalizer-and-auto-collect |
| **Phase 3d.1** | External root scanner（VmContext static_fields / pending_exception 暴露给 cycle collector，修复漏扫 bug）| ✅ 2026-04-29 add-external-root-scanning |
| **Phase 3d.2** | 暴露 `Std.GC.Collect()` / `UsedBytes()` / `ForceCollect()` 给 z42 脚本 + 端到端 golden test 验证环回收 | ✅ 2026-04-29 expose-gc-to-scripts |
| **Phase 3f** | interp 栈扫描（`exec_function` FrameGuard RAII 把 frame.regs 暴露给 scanner，修复"outer 在 reg + outer.slot → inner" 间接可达对象被误清的 bug） | ✅ 2026-04-29 add-interp-stack-scanning |
| **Phase 3e** | `GcRef<T>` backing 升级 `Rc<GcAllocation<T>>`，wrapper Drop 自动触发已注册 finalizer（含纯 Rc Drop 路径，不仅限 cycle collect） | ✅ 2026-04-29 add-drop-time-finalizer |
| **Phase 3f-2** | JIT 栈扫描（6 个 JitFrame::new 站点 push/pop frame.regs 到 exec_stack，修复 JIT 路径 transitive bug）| ✅ 2026-04-29 add-jit-stack-scanning |
| **unify-frame-chain** | `exec_stack` / `env_arena_stack` / `call_stack` 三栈合并为单一 `Vec<VmFrame>`；任一 invoke 站点 push 全套 (regs/env_arena/name/file)，GC scanner 改单循环；附带修复 `jit_obj_new` ctor + `jit_to_str` ToString 缺 call_frame push 的 stack-trace 漏帧 bug | ✅ 2026-05-10 unify-frame-chain |
| **Phase 3-OOM** | strict OOM 模式（trait `set_strict_oom`；启用后 alloc 越过 `max_heap_bytes` 返回 `Value::Null` 不入 registry / 不 bump stats）| ✅ 2026-04-29 add-strict-oom-rejection |
| **A2 mark-sweep** | 移除 trial-deletion，切到标准 mark-sweep（`marked: AtomicU8` 字段 + `mark_phase` BFS + `sweep_phase`）；纯 tracing 语义，Rust-local 强引用不再隐式为 root | ✅ 2026-05-21 add-mark-sweep-collector |
| **Write barriers** | `MagrGC::write_barrier_field` / `write_barrier_array_elem` call-site wiring 完成；`Value::is_heap_ref()` 在 call site 过滤 primitive 写入；trait override 可由后续 generational / concurrent backend 接入；default no-op | ✅ 2026-05-22 add-write-barriers |
| **A4 concurrent mark** | 可选 `Z42_GC_MODE=concurrent` 切换 tricolor incremental update：STW root snapshot → ConcurrentMarking 阶段 mutator 不 park → barrier shade gray → STW 终止 handshake → STW sweep；STW 仍为默认 | ✅ 2026-05-22 add-concurrent-gc |
| **A1 custom allocator** | `GcRef<T>` 从 `Arc<GcAllocation<T>>` 切到 chunked region + NonNull handle。Clone 零原子 op；`heap_registry` 删除；finalizer at sweep + `Std.GC.Finalize(x)` 显式 API。Sweep survivors 2.49× faster；alloc 1.03-1.06× faster | ✅ 2026-05-22 add-custom-allocator |
| **A3 generational** | `GcMode::GenerationalMarkSweep` opt-in：RegionEntry gen_age + young_list + per-chunk card_dirty bitmap；write barrier 标记 old→young 跨代写；minor GC 扫 young + dirty cards (O(young) pause)；major GC 扫全堆 + 清 cards；survival rate >= 0.75 时 minor → major escalation。Bench: 1.40 ms minor vs 5.55 ms full STW on equivalent heap (**~4× minor speedup**) | ✅ 2026-05-22 add-generational-gc |

至此 GC 主功能完整，可投产。后续可选迭代见下文 ["GC 后续迭代规划"](#gc-后续迭代规划) 段。

## 字符串脚本化的未来动机

当 Phase 2/3 GC 成熟（环检测 + 追踪），字符串可以从 `Value::Str(String)`
primitive 迁移成 `Value::Object(...)` 包装的脚本类（z42 源码实现 BCL `String`），
届时 z42 源码可承担更多 string 方法实现，进一步减少 Rust 端硬编码 builtin。
这与 2026-04-24 起的 simplify-string-stdlib / wave1-string-script 系列重构方向一致。

## 设计权衡：为什么不一次到位

- **Phase 1 范围严格限定为"接口收口、行为零变化"** —— commit 范围干净（纯重构），
  失败回滚成本低；环检测算法选型应在专门的 Phase 2 spec 中讨论（dumpster crate 依赖、
  STW vs 并发等独立决策点）
- **trait 形状一次设计完，让后续 phase 切换实现无需改 callsite** —— 6 处分配点
  已统一走 `ctx.heap()`，未来即使从 RcMagrGC 切到 MarkSweepHeap，调用方代码不变
- **`Value` enum 不动** —— Phase 3 引入 `GcRef<T>` 时再统一修改 PartialEq /
  JIT helper / 测试构造；Phase 1 不要把这些副带成本算进来

## GC 后续迭代规划

> 至 Phase 3-OOM 完成（2026-04-29）GC 主功能完整。下表是**可选优化轨道**，
> 按需启动独立 spec。每条提供 **What / Why / Deps / Size / Risk** 四元组，
> 让接手者不用读源码就能判断 ROI。

### A. 性能优化轨道

#### A1. 自定义堆 allocator（已落地，2026-05-22）
- **状态**：✅ 完成，spec `add-custom-allocator`
- **What**：`GcRef<T>` 从 `Arc<GcAllocation<T>>` 切到 `NonNull<RegionEntry<T>>` +
  generation handle。Backing 是 chunked region allocator（`Vec<Box<[E;256]>>`，
  chunks 永不 relocate 保 pointer stability）。
- **实现要点**：
  - `Region<T>` chunked allocator + bump pointer + free list（gc/region.rs，
    `add-custom-allocator P0`）
  - `GcRef<T>` 重写：NonNull + generation 12B handle；Clone 是 memcpy 零原子 op；
    Drop 是 no-op（add-custom-allocator P1）
  - `heap_registry: Vec<WeakRef>` 删除；region 是 authoritative liveness store；
    sweep / iterate / snapshot 都直接走 `Region::iterate_alive`
  - Finalizer 语义切换：Drop 不再触发，sweep 时触发 + `Std.GC.Finalize(x)`
    显式 API 给 RAII 场景立即释放（add-custom-allocator P2）
  - WeakRef 用 generation tombstone 替代 `std::Weak<Arc<...>>` —— ABA-safe
- **生命周期契约**：`GcRef` 不能 outlive 它所指 `Region` 所属的 `ArcMagrGC`。
  z42 现有架构所有 GcRef 都活在 VmContext 范围内，契约天然满足；embedder 需注意
- **Benchmark 实测**（macOS arm64 release，10k objects）:
  - alloc throughput: 1.03-1.06× faster（Arc init 节省，但 record_alloc bookkeeping 仍占大头）
  - sweep survivors: **2.49× faster**（去除 per-entry Weak::upgrade）
  - sweep garbage: 3.50× slower（**work 时序重分布**：Arc 路径在 Drop 时分散付费，
    region 路径集中在 sweep 付费；total CPU 大致相同）
  - GcRef::clone: 架构上去除原子 op（hot-path bench 待 follow-up perf spec）
- **A3 / D1 解锁**：Region<T> 是 generational promotion 物理前提；API shape 已对齐
  MMTk `Mutator::alloc` binding 契约

#### A2. 纯 Mark-Sweep（已落地，2026-05-21）
- **状态**：✅ 完成，spec `add-mark-sweep-collector`
- **What**：从 trial-deletion（mark + tentative count + 断环）升到标准 mark-sweep
  （BFS 标记 reachable → 扫描 registry 反向断不可达对象）
- **实现要点**：
  - `GcAllocation<T>` 新增 `marked: AtomicU8`（Relaxed ordering 足够，STW
    safepoint 已建立 happens-before）
  - `mark_phase()`：BFS from `pinned_roots` + `external_root_scanner`，
    `Value::trace_children` 提供子引用枚举
  - `sweep_phase()`：snapshot live → `is_marked = true` reset mark，
    `is_marked = false` 调 `break_cycle_value` 清内部引用；Vec drop 时 Arc
    链式 Drop 触发 finalizer
  - 算法路径仅 `run_cycle_collection` 2 行（mark + sweep）
- **语义切换**：trial-deletion 之前默认按 `Arc::strong_count` 把"外部用户强引用"
  当作隐式 root；mark-sweep 走**纯 tracing**——Rust-local `Value` 不再保护
  对象，embedder 必须 `pin_root` 显式表达保留意图。VmContext 已通过
  `external_root_scanner` 暴露所有 mutator-visible 根，stdlib/JIT 路径无回归
- **未实现**：Trace trait 一次性 derive（沿用 `Value::trace_children` 内联
  match），自定义 allocator 仍未做（A1 仍 backlog）；后续 benchmark 报告独立 spec

#### A3. Generational GC（已落地，2026-05-22）
- **状态**：✅ 完成，spec `add-generational-gc`
- **What**：`GcMode::GenerationalMarkSweep` opt-in mode；RegionEntry gen_age
  + Region young_list + per-chunk card_dirty bitmap；minor GC 扫 young +
  dirty cards (O(young) pause)；major GC 扫全堆 + 清 cards；survival rate
  >= 0.75 (Z42_GC_MINOR_THRESHOLD) 时 minor → major escalation。
- **实现要点**：
  - Logical promotion (gen_age 字段，不物理移动)：保 GcRef NonNull 契约
  - Promotion threshold N=2 (industry default Java tenure)，配
    `Z42_GC_TENURE` 调
  - Card marking 粒度 per-chunk (256 entries / card)；u32 bitmap per chunk
  - Write barrier override 检查 `owner.gen_age >= threshold && new.gen_age == 0`
    时 `mark_card_dirty(owner.chunk)`
  - Minor mark phase：BFS from pinned + external + dirty cards；trace
    children but enqueue young only (老 children 通过 dirty card 重 root)
  - Card_dirty 不在 minor 清；只在 major 清（preserve stable old→young refs）
- **Bench**: 1.40 ms minor vs 5.55 ms full STW on 10k pinned old + 1k young
  workload — **~4× minor speedup**
- **Mutually exclusive with concurrent mark v1**: combine in future
  `add-concurrent-generational` spec

#### A4. Concurrent / incremental collector
- **What**：collect 工作分摊到多个 alloc 时间片（incremental）或后台线程（concurrent）
- **Why**：当前 STW collect 在大堆下停顿明显；分摊后单次 pause 时间有界
- **Deps**：A2（mark-sweep，✅ 已就位）+ 多线程模型（roadmap A6 backlog）
- **Size**：3000+ LOC，20-30 天
- **Risk**：数据竞争 + write barrier 复杂度高；需要 GC safepoint 协议

### B. 嵌入式 / 可观察性

#### B1. OOM 异常抛出（替代 strict 模式返 Null）
- **What**：alloc 失败抛 `Std.OutOfMemoryException`（脚本 `try/catch` 可捕获），替代 strict 模式返 `Value::Null`
- **Why**：脚本端 OOM 处理更自然；当前返 Null 后续访问产生 NRE，丢失"why null"信息
- **Deps**：预分配 exception 实例（破启动期 alloc 循环依赖）+ ctx.set_exception 与 alloc callsite 集成
- **Size**：~150 LOC，1-2 天
- **Risk**：低；exception 实例的"启动期 alloc"循环依赖是设计点

#### B2. 软引用（SoftGcRef）
- **What**：内存压力下 GC 可主动回收的引用，介于 strong 与 weak 之间
- **Why**：缓存场景（"内存够则保留，紧张则丢弃"）现在只能手动 weak + 重建
- **Deps**：A2 mark phase 决策接口（区分软引用与强引用，A2 已就位可扩展）
- **Size**：~200 LOC，2 天

#### B3. Heap snapshot 导出（V8 / Chrome DevTools 格式）
- **What**：序列化 `HeapSnapshot` 为 `.heapsnapshot` 兼容 Chrome DevTools / [perfetto](https://perfetto.dev/) 等工具
- **Why**：嵌入用户用熟悉的工具分析堆，无需重写 z42 专用 GUI
- **Deps**：无（纯序列化层）
- **Size**：~300 LOC，2-3 天

#### B4. 分配站点追踪（per-callsite alloc count + total bytes）
- **What**：默认 alloc_sampler 实现按 IR 站点 ID 聚合分配数据
- **Why**：定位"哪行代码在 hot allocate"，性能调优刚需
- **Deps**：编译期注入 site ID（IR 加 `alloc_site_id` 字段 + Codegen 配合）
- **Size**：~400 LOC，3-4 天
- **Risk**：site ID 在 IR 持久化、跨 zpkg 唯一性

#### B5. GC pause 直方图
- **What**：内置 metrics 直方图（Prometheus 风格 buckets：< 1ms / 1-10 / 10-100 / ...）
- **Why**：观察长尾 pause 分布，比单次 pause_us 更有信息量
- **Deps**：无
- **Size**：~200 LOC，2 天

### C. 测试 / 调试质量

#### C1. Debug-only invariant 检查
- **What**：debug build 下每次 collect 后 assert（无 dangling Rc / registry 一致性 / finalizers_pending == has_finalizer 总数 / 等）
- **Why**：早暴露内部一致性 bug，防止生产环境疑难杂症
- **Deps**：无
- **Size**：~150 LOC，1-2 天

#### C2. Stress test（property-based）
- **What**：proptest / quickcheck 生成随机 alloc / drop / pin / collect 序列，长跑下 verify 不 crash + 内存不 leak
- **Why**：手工 unit test 难覆盖 alloc 与 collect 的交错状态空间
- **Deps**：proptest crate
- **Size**：~300 LOC，2-3 天

#### C3. Multi-VmContext 隔离压测
- **What**：多个 VmContext 实例并行运行（线程间隔离），verify GC 状态不互相污染
- **Why**：嵌入用户可能创建多 VM；验证 Phase 3 多实例隔离设计真的成立
- **Deps**：无
- **Size**：~200 LOC，2 天

### D. 终极方向：MMTk 集成

#### D1. MMTk 后端实现
- **What**：实现 [MMTk](https://www.mmtk.io/) `VMBinding` trait，把 RcMagrGC 替换为 MMTk-backed GC（多 collector 可选：SemiSpace / GenImmix / MarkSweep / Immix / GenCopy）
- **Why**：MMTk 是工业级研究项目，被 OpenJDK / V8 / Julia / Ruby / RustPython 采用；享受 ~30 年 GC 算法成果
- **Deps**：A1（自定义 allocator）；A2 mark-sweep 已落地（提供 tracing 基础）；trait 形状已对齐 MMTk porting contract
- **Size**：4-8 周（包括稳定 + benchmark）
- **Risk**：高 —— 引入大型 crate 依赖；ABI 边界 careful；但 trait 抽象层可以 ABI-shield

### 优先级建议

如以工程成熟度优先：

1. **C1 + C2 + C3**（debug invariants + stress + multi-VM）—— 巩固现有质量
2. **B1**（OOM exception）—— 嵌入用户最常请求的 ergonomics
3. **B5**（pause 直方图）—— 低成本可观察性
4. **B3**（heap snapshot 导出）—— 让现有工具链可用

如以性能为优先：

1. **A1**（自定义 allocator）—— 单刀直入的常数因子优化
2. ~~**A2**（mark-sweep）~~ ✅ 已落地（2026-05-21）
3. **B4**（alloc site tracking）—— 找出热点
4. **A3 / A4**（generational / concurrent）—— 长期投资（A2 已就位，可基于
   mark-sweep 进阶）

如以"赶上工业级"为优先：

直接跳到 **D1**（MMTk 集成），跳过 A1-A4 自研路径。trait 形状已对齐
MMTk porting contract，集成成本主要在 ABI shim + 调优。

---
