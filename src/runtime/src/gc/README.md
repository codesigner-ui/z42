# gc/

## 职责

z42 VM 的 GC 子系统：堆对象（`ScriptObject` / `Array` / `Map`）的分配与回收抽象。

不包含：根扫描、栈映射、写屏障真实实现 —— 这些留给 Phase 2/3。

## 核心文件

| 文件 | 职责 |
|------|------|
| `heap.rs` | `trait MagrGC` —— GC 抽象接口（对齐 MMTk porting contract，10 个能力组 ~30 方法）|
| `rc_heap.rs` | `RcMagrGC` —— Phase 1 默认后端，`Rc<RefCell<...>>` 引用计数 + 全 host-side 嵌入接口 |
| `types.rs` | 支持类型 —— `RootHandle` / `FrameMark` / `GcEvent` / `GcObserver` / `WeakRef` / `HeapSnapshot` / ... |
| `heap_tests.rs` | trait 默认方法契约测试 |
| `rc_heap_tests.rs` | RcMagrGC 行为单元测试（覆盖全 11 个能力组）|

## 入口点

- `crate::gc::MagrGC` —— GC 接口 trait（10 个能力组）
- `crate::gc::RcMagrGC` —— Phase 3a 默认实现（GcRef backing 是 Rc<RefCell<T>>）
- `crate::gc::GcRef<T>` / `crate::gc::WeakGcRef<T>` —— 堆引用不透明句柄（Phase 3a 引入，Phase 3b 切换 backing 时 callsite 零修改）
- 嵌入相关类型：`RootHandle` / `FrameMark` / `GcEvent` / `GcObserver` / `AllocSample` / `WeakRef` / `HeapSnapshot` / `HeapStats` / ...

### 能力组（按 trait 内分组）

| # | 能力组 | 主要方法 |
|---|--------|---------|
| 1 | Allocation | `alloc_object` / `alloc_array` |
| 2 | Roots | `pin_root` / `unpin_root` / `enter_frame` / `leave_frame` / `for_each_root` |
| 3 | Write barriers | `write_barrier_field` / `write_barrier_array_elem` |
| 4 | Object Model | `object_size_bytes` / `scan_object_refs` |
| 5 | Collection | `collect` / `collect_cycles` / `force_collect` / `pause` / `resume` |
| 6 | Heap config | `set_max_heap_bytes` / `used_bytes` |
| 7 | Finalization | `register_finalizer` / `cancel_finalizer` |
| 8 | Weak refs | `make_weak` / `upgrade_weak` |
| 9 | Observers | `add_observer` / `remove_observer` |
| 10 | Profiler | `set_alloc_sampler` / `take_snapshot` / `iterate_live_objects` |
| 11 | Stats | `stats` |

### 典型使用

```rust
// 脚本驱动分配（VM 内部）
let v = ctx.heap().alloc_array(vec![Value::Null; n]);

// Host-side 嵌入集成
let h = ctx.heap().pin_root(value.clone());
let id = ctx.heap().add_observer(Arc::new(MyTelemetry {}));
ctx.heap().set_max_heap_bytes(Some(64 * 1024 * 1024));
let snap = ctx.heap().take_snapshot();
```

## 依赖关系

- 上游：`metadata::{Value, ScriptObject, TypeDesc, NativeData}`
- 下游：`vm_context::VmContext` 持有 `Box<dyn MagrGC>`；`interp/exec_instr.rs` 与 `jit/helpers_object.rs` 通过 `ctx.heap()` 调用

## Phase 路线

详见 [`docs/design/vm-architecture.md`](../../../../docs/design/vm-architecture.md) "GC 子系统" 段。

**已知限制（Phase 1 RC 模式）**：

1. **环引用泄漏**：`a.next = b; b.next = a` 仍泄漏 → Phase 2 修复
2. **Finalizer 不会被自动触发**：RC 缺 Drop hook，注册仅记录到 `finalizers_pending` 计数 → Phase 3 mark-sweep 调度真实触发
3. **`take_snapshot` / `iterate_live_objects` 仅覆盖 reachable from pinned roots**：RC 无全堆枚举能力 → Phase 3 trace 后 `coverage` 自动升级 `Full`
4. **`used_bytes` 单调递增**：RC drop 不可观察 → Phase 3 trace 精确化
5. **`OutOfMemory` 仅通知不拒绝**：RC 模式 alloc 仍然成功 → Phase 3 可拒绝

> **2026-04-29 extend-native-fn-signature**：原限制"corelib 内 Rc::new 直构未迁移"已解决 ——
> `NativeFn` 签名扩展为 `fn(&VmContext, &[Value]) -> Result<Value>`，全部 ~55 个 builtin
> 走 ctx 传参；`__obj_get_type` / `__env_args` 走 `ctx.heap().alloc_*(...)`。
> 全代码库无任何 `Rc::new(RefCell::new(...))` 直构，仅 `gc/rc_heap.rs` 内部权威实现保留。

## 命名

**MagrGC** 取自《银河系漫游指南》中的 **Magrathea** —— 那颗专门建造定制行星的传奇世界。
