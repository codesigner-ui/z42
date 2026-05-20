# Design: VmContext registry for cross-thread GC root scanning

## Architecture

```
当前（add-multithreading-foundation Phase 3 后）：
  VmCore {
    static_fields, ...,  heap: Box<dyn MagrGC>,
    // 没有 vm_contexts registry
  }

  VmContext (value-type struct) {
    core: Arc<VmCore>,
    pending_exception: Arc<Mutex<...>>,        ← scanner closure clone 捕获
    call_stack:        Arc<Mutex<...>>,        ← 同上
    func_ref_slots:    Arc<Mutex<...>>,        ← 同上
    process_next_id:   AtomicU64,
  }

  Scanner closure captures `core_weak: Weak<VmCore>` + 三个 Arc<Mutex<...>>
  clones from THE ONE VmContext. Single-VmContext invariant.

本 spec 后：
  VmCore {
    static_fields, ...,  heap: Box<dyn MagrGC>,
    vm_contexts: Mutex<Vec<VmContextPtr>>,   ← NEW
  }

  struct VmContextPtr(*const VmContext);
  unsafe impl Send for VmContextPtr {}
  unsafe impl Sync for VmContextPtr {}

  VmContext (pinned, heap-allocated) {
    core: Arc<VmCore>,
    pending_exception: Arc<Mutex<...>>,        ← unchanged
    call_stack:        Arc<Mutex<...>>,
    func_ref_slots:    Arc<Mutex<...>>,
    process_next_id:   AtomicU64,
    _pin:              PhantomPinned,         ← NEW, marks !Unpin
  }

  impl VmContext {
    pub fn new() -> Pin<Box<VmContext>> {     ← NEW signature
       let ctx = Pin::new_unchecked(Box::new(VmContext { ... }));
       let ptr = VmContextPtr(&*ctx as *const _);
       ctx.core.vm_contexts.lock().push(ptr);
       ctx
    }
  }

  impl Drop for VmContext {                   ← NEW
    fn drop(&mut self) {
       let ptr = self as *const _;
       self.core.vm_contexts.lock().retain(|p| p.0 != ptr);
    }
  }

  Scanner closure captures `Weak<VmCore>` ONLY. On each invocation:
    1. core_weak.upgrade() — skip if dropped
    2. for v in c.static_fields.lock() { visit(v) }
    3. snapshot = c.vm_contexts.lock().clone();   // copy Vec, release lock
    4. for ctx_ptr in snapshot:
         unsafe { let ctx = &*ctx_ptr.0; }
         walk ctx.{pending_exception, call_stack, func_ref_slots}
```

## Decisions

### Decision 1: `VmContextPtr` 类型 — `*const VmContext` vs `usize`

**问题**：Registry 项怎么存？

**选项**：
- **A** newtype `pub(crate) struct VmContextPtr(*const VmContext)` + `unsafe impl Send/Sync` —— 直接，deref 不需要 cast。Send/Sync via unsafe impl 表达式
- **B** `usize` storage + 手动 cast on deref —— Send/Sync 自动；deref 路径多一步

**决定**：**A**。与现有 `VmFrame` / `MethodEntry` 的 unsafe impl 模式一致（add-multithreading-foundation Phase 3 Decision-amendment 已建立此 pattern）。SAFETY 注释解释"指针在 register..deregister 区间有效"。

```rust
/// Type-erased VmContext pointer used by the GC scanner to walk all
/// thread-local VmContexts' frames.
///
/// # Safety
///
/// - The pointer is registered by `VmContext::new()` after `Pin<Box<>>`
///   wrapping ensures address stability for the entire lifetime of the
///   VmContext.
/// - It is deregistered by `Drop` BEFORE the Box is freed, so any
///   dereference performed while the entry is in `vm_contexts` is on a
///   live VmContext.
/// - Cross-thread access: per-thread VmContext fields are themselves
///   `Arc<Mutex<...>>` (Send + Sync), so reading them from another thread
///   is sound.
pub(crate) struct VmContextPtr(pub(crate) *const VmContext);

unsafe impl Send for VmContextPtr {}
unsafe impl Sync for VmContextPtr {}
```

### Decision 2: `Pin<Box<VmContext>>` 强制 vs `Box<VmContext>` 信任

**问题**：地址稳定靠什么提供？

**选项**：
- **A** `Pin<Box<VmContext>>` + `PhantomPinned` —— 类型层禁止 move-out（`*box` / `mem::swap(&mut *box, ...)`），最强保证
- **B** 裸 `Box<VmContext>` —— 堆分配地址已稳定；move-out 是 user 责任

**决定**：**A**。pre-1.0 强约束更安全，运行时无开销（Pin 是 zero-cost wrapper）。`PhantomPinned` 标记后 VmContext 不再 Unpin，编译期防止误用。

调用方影响：`Pin<Box<VmContext>>: Deref<Target=VmContext>`，所以 `ctx.foo()` / `&ctx` 风格仍 work。唯一不能写的是 `*ctx` move-out 或 `mem::swap(&mut *ctx, ...)`，这些是真正不安全的操作，正是 Pin 防的。

### Decision 3: Scanner snapshot vs hold-lock-while-iterating

**问题**：scanner 遍历 vm_contexts 时是 hold lock 还是 clone snapshot？

**选项**：
- **A** Hold registry lock for full iteration —— 防 deregister race；但如果 VmContext drop 在另一线程，drop 会 block 等 scanner 释放
- **B** Clone `Vec<VmContextPtr>` snapshot first, release lock, iterate snapshot —— scanner 不阻塞 drop；但 snapshot 可能含已 drop 的 ptr

**决定**：**A** 加 `parking_lot::Mutex`（fair lock）。理由：
1. registry 通常很小（线程数量级），clone vec 不便宜（虽然 Vec<VmContextPtr> 只 copy raw pointers，复杂度低）
2. Drop 期间等 scanner OK：GC collect 不会从 drop 内部触发（drop 在 mutator 路径终点，不分配）
3. **B 是错的**：snapshot 后某个 ptr 对应的 VmContext 在另一线程 drop，scanner 用 snapshot 上的 ptr deref → use-after-free UB

**B 修复版**：snapshot + 每个 ptr deref 前都 `find` in current registry 验证存在 —— 但这退化为 hold lock，没收益。

> **特别陷阱**：GC collect 在 alloc 路径触发 → mutator 持 alloc context → 同时进 scanner → 需要 lock vm_contexts。如果同一 mutator 之前在 VmContext::new 里持有 vm_contexts lock，就 deadlock。但 alloc 是在 VmContext 已构造完毕的 happy path，不会同 new 路径并行。**实施期需 verify**：所有 vm_contexts.lock() 区间都不嵌套 alloc。如果发现嵌套，必须改 design（候选：使用单独的 `parking_lot::RwLock` 给 scanner 读路径加速，writer 是 new/Drop）。

### Decision 4: VmContext field 完全不变 vs 整理

**问题**：本 spec 触及 VmContext 类型，要不要顺便重命名 / reorder / 加 method？

**决定**：**仅加 `_pin: PhantomPinned`，其余完全不动**。理由：scope 最小化。命名 / reorder 留给可能的 future cleanup spec（如有需要时单开）。

### Decision 5: Scanner 不再捕获 per-thread Arc 克隆

**问题**：现 scanner closure 同时捕获 `Weak<VmCore>` AND 三个 per-thread `Arc<Mutex<...>>` clone。本 spec 后 registry 走 walk-all-VmContexts 路径 —— 三个 per-thread 字段直接从遍历到的 `ctx` 取。原 closure 捕获就是冗余 / 不正确（只看见第一个 VmContext）。

**决定**：scanner closure **完全不再捕获 per-thread Arc 克隆**。只捕获 `Weak<VmCore>`。所有 root 走 walk-registry 路径。删除原 `let pe = ...; let cs = ...; let frs = ...;` 三行。

### Decision 6: VmContext::new() 失败语义

**问题**：注册 push 进 Vec 可能 OOM。new() 怎么签名？

**选项**：
- **A** 保持 `pub fn new() -> Pin<Box<VmContext>>`，OOM 走 abort（Box::new 已经是这语义）
- **B** 改 `pub fn new() -> Result<Pin<Box<VmContext>>, anyhow::Error>` 显式 OOM

**决定**：**A**。OOM 在 VM 启动期是不可恢复 fatal，跟 Box::new 的 abort 语义一致。改 Result 会让所有 caller 加 `?`，没收益。

### Decision 7: 单元测试 vs 集成测试

**问题**：multi-VmContext 验证放哪？

**决定**：**集成测试**（`src/runtime/tests/cross_thread_smoke.rs`）。理由：需要真启动多线程；单测在 cfg(test) 跑得了但与 cross_thread_smoke 同 pattern，统一放一处。新增测试 `multi_vm_contexts_alloc_and_collect`：

```rust
#[test]
fn multi_vm_contexts_alloc_and_collect() {
    // Two threads, each its own VmContext sharing one VmCore.
    let core = ...?;  // need a way to share Arc<VmCore> across new()
    // Note: API may need adjustment — VmContext::new() creates a fresh VmCore.
    // For this test we'll instead use a shared VmCore constructor or
    // accept that the test exercises one VmCore per VmContext but
    // validates registry behaviour with multiple VmContexts.
    ...
}
```

> **实施期可能发现**：当前 `VmContext::new()` 内部 `Arc::new(VmCore { ... })` —— 每次创建一个 VmCore。要测试 multi-VmContext 共享 VmCore 需要新 constructor `VmContext::new_with_core(Arc<VmCore>)` 或者在 registry test 里只验证 same-VmCore-multi-context 的内部 invariants without actually exercising scanner with two threads. Test scope adjustment recorded in tasks.md 备注.

## Implementation Notes

### 实施顺序

1. **Phase 1** —— 类型层：加 `VmContextPtr` newtype 和 unsafe impl Send/Sync；VmCore 加 `vm_contexts` 字段；VmContext 加 `_pin` 字段
2. **Phase 2** —— 构造路径：`VmContext::new()` 改返 `Pin<Box<Self>>`；registration in new + deregistration in Drop
3. **Phase 3** —— Scanner closure 重写：上锁 walk registry；删除原 per-thread Arc clone 捕获
4. **Phase 4** —— Caller API 适配：`&mut VmContext` → `&VmContext`（4 callsite）；构造改 `let ctx = VmContext::new();`（10+ callsite）；host embedding `HostModule.ctx: Pin<Box<VmContext>>`
5. **Phase 5** —— 测试 + 文档：`multi_vm_contexts` 集成测试；vm-architecture.md 删 single-invariant 段；concurrency.md 状态表更新

### `process_next_id` 注意

per-thread 字段，但本 spec 不变（它是 AtomicU64，Send+Sync 已自动满足）。每个 VmContext 自己的 counter，仍从 1 开始。这是 *per-thread process id*，每线程互不冲突 OK 因为 ProcessSlot 在 VmCore.processes（共享）；u64 空间充裕。

### Host embedding API impact

`HostModule.ctx: VmContext` 改 `Pin<Box<VmContext>>`。host C ABI 不暴露 VmContext 直接指针（已经 hidden by HostModuleHandle），所以外部 ABI 不变。

### `let mut ctx` 残留

构造改后 `let ctx = ...` 即可（Pin<Box<>> 不可变绑定足够；所有 ctx 方法 `&self`）。grep 修一遍。

## Testing Strategy

- **单元测试**：vm_context_tests.rs 加 `vm_context_registers_self_on_new` / `vm_context_drop_removes_from_registry` / `two_vm_contexts_both_registered`
- **集成测试**：cross_thread_smoke.rs 加 `multi_vm_contexts_alloc_and_collect`
- **Send+Sync 编译期**：send_sync.rs 加 `assert_send_sync::<VmContextPtr>()` + `assert_send_sync::<VmCore>()` re-verify（regression guard）
- **不回归**：stdlib 62 / test-vm 312 / dotnet test 1288
- **Memory safety**：手测 `cargo run --release` 多次构造 + drop VmContext 看 valgrind / heaptrack 是否报 leak（informally）
