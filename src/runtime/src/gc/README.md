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

**已知限制（Phase 3a/3b/3c/3d/3d.1 后）**：

1. **Finalizer 仅在 collect_cycles 时触发**：纯 Rc Drop（无环）路径不触发 →
   Phase 3e 替换 backing 时一并解决
2. **`OutOfMemory` 仅通知不拒绝**：RC 模式 alloc 仍然成功 → Phase 3e+
3. **interp / JIT 栈帧 regs 暂未对接为 GC roots** → Phase 3f Cranelift stack maps

> **2026-04-29 add-heap-registry（Phase 3b 完成）**：snapshot/iterate Full coverage。
>
> **2026-04-29 add-cycle-breaking-collector（Phase 3c 完成）**：环引用真实回收
> （Bacon-Rajan trial-deletion）+ `used_bytes` 准确反映释放量。
>
> **2026-04-29 add-finalizer-and-auto-collect（Phase 3d 完成）**：
> - **Finalizer 真触发**：`collect_cycles` 断环时调用注册的 finalizer（one-shot）
> - **内存压力自动 collect**：alloc 后检查 `used >= 90% max_bytes`，throttle by
>   10% growth → 自动 `collect_cycles`
> - `near_limit_warned` collect 后自动 reset，让下次跨阈值能再发 `NearHeapLimit`
>
> **2026-04-29 add-external-root-scanning（Phase 3d.1 完成）**：修复 cycle
> collector 漏扫 VmContext 级 roots 的 bug —— `RcMagrGC` 加 `external_root_scanner`
> 字段，`mark_reachable_set` 在扫完 pinned roots 后调用 scanner 把额外 roots
> 也喂入 BFS。`VmContext::new` 注册一个扫描自身 `static_fields` /
> `pending_exception` 的闭包（通过 Rc<RefCell<...>> 共享 ownership）。这样
> static 字段持有的 cyclic 对象不会被误判为 unreachable + 内部 slots 不会被误清。

> **2026-04-29 extend-native-fn-signature**：原限制"corelib 内 Rc::new 直构未迁移"已解决 ——
> `NativeFn` 签名扩展为 `fn(&VmContext, &[Value]) -> Result<Value>`，全部 ~55 个 builtin
> 走 ctx 传参；`__obj_get_type` / `__env_args` 走 `ctx.heap().alloc_*(...)`。
> 全代码库无任何 `Rc::new(RefCell::new(...))` 直构，仅 `gc/rc_heap.rs` 内部权威实现保留。

## 命名

**MagrGC** 取自《银河系漫游指南》中的 **Magrathea** —— 那颗专门建造定制行星的传奇世界。
