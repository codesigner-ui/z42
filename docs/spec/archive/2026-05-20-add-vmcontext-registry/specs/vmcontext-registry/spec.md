# Spec: VmContext registry for cross-thread GC root scanning

## ADDED Requirements

### Requirement: Registry lifecycle

#### Scenario: VmContext::new registers itself
- **WHEN** caller invokes `VmContext::new()`
- **THEN** return type is `Pin<Box<VmContext>>`
- **AND** the returned VmContext's raw `*const Self` is present in `core.vm_contexts.lock()` immediately after the call returns

#### Scenario: VmContext drop deregisters
- **WHEN** the `Pin<Box<VmContext>>` last reference drops（即 `Box` 释放）
- **THEN** before memory is freed, `Drop::drop` removes `self as *const Self` from `core.vm_contexts.lock()`
- **AND** there is no stale pointer left in the registry

#### Scenario: Multiple VmContexts coexist on one VmCore
- **WHEN** two threads each call `VmContext::new()` 共享 `Arc<VmCore>`
- **THEN** `core.vm_contexts.lock().len()` is 2
- **AND** each entry points to a distinct heap allocation
- **AND** dropping one VmContext leaves the other's entry intact

### Requirement: Cross-thread GC root scanning via registry

#### Scenario: Scanner walks every registered VmContext's frames
- **GIVEN** two VmContexts A and B registered on the same VmCore
- **AND** A's `call_stack` has one frame with regs `[Value::I64(7)]`
- **AND** B's `call_stack` has one frame with regs `[Value::I64(13)]`
- **WHEN** the GC scanner closure is invoked (e.g. inside `collect_cycles`)
- **THEN** visitor is called with both `Value::I64(7)` and `Value::I64(13)` (plus other registered Values like static fields)

#### Scenario: Scanner skips dropped contexts
- **GIVEN** two VmContexts A and B
- **AND** A is dropped
- **WHEN** the GC scanner runs
- **THEN** only B's frames are walked (no UB from dereferencing dangling pointer)

#### Scenario: Scanner is safe under concurrent VmContext construction
- **WHEN** thread 1 is iterating the registry (holding lock)
- **AND** thread 2 calls `VmContext::new()` (waits on lock)
- **THEN** thread 1's iteration sees only the entries present at lock-acquire time
- **AND** thread 2's registration happens after thread 1 releases
- **AND** no deadlock occurs

### Requirement: Pin enforcement

#### Scenario: VmContext is !Unpin
- **WHEN** compile-time check `std::pin::Pin::new(&mut *ctx).get_mut()` is attempted on `Pin<Box<VmContext>>`
- **THEN** compile error (VmContext implements `!Unpin` via `PhantomPinned`)

#### Scenario: `Pin<Box<VmContext>>` derefs to `&VmContext`
- **WHEN** caller writes `ctx.heap()` on `let ctx: Pin<Box<VmContext>> = VmContext::new();`
- **THEN** auto-deref through `Pin<Box<T>>: Deref<Target=T>` works
- **AND** method dispatches to `VmContext::heap(&self)`

### Requirement: Existing single-thread paths unchanged

#### Scenario: stdlib tests still pass
- **WHEN** `./scripts/test-stdlib.sh` runs
- **THEN** 62/62 across 17 libs pass

#### Scenario: VM e2e tests still pass
- **WHEN** `./scripts/test-vm.sh` runs
- **THEN** interp 156/156 + JIT 156/156 = 312/312（数字按本 spec 实施前的 baseline，本 spec 是单线程不可回归）

#### Scenario: cargo unit + cross_thread_smoke pass
- **WHEN** `cargo test --release` runs
- **THEN** the previous 405 unit + 6 send_sync + 3 cross_thread_smoke = 414 pass
- **AND** new test `multi_vm_contexts_alloc_and_collect` (in cross_thread_smoke.rs) also passes

### Requirement: Send + Sync 不可回归

#### Scenario: VmContextPtr is Send + Sync
- **WHEN** the new test in `arc_heap_tests/send_sync.rs` runs `assert_send_sync::<VmContextPtr>()`
- **THEN** compile-time pass

#### Scenario: VmCore still Send + Sync
- **WHEN** existing `vm_core_is_send_sync` test runs
- **THEN** still passes（新 `vm_contexts: Mutex<Vec<VmContextPtr>>` 字段不破坏 bound，因为 `Mutex<T>: Sync` 要求 `T: Send`，`Vec<VmContextPtr>: Send + Sync` 由 unsafe impl 提供）

## Anti-Scope

- 不引入用户面线程 API（`Std.Threading.Thread.Start` etc.）—— `add-threading-stdlib`
- 不引入 GC safepoint —— `add-gc-safepoint`
- 不引入 spawn 语法 —— `add-spawn-syntax`（L3）
- 不让 `VmContext` 可 Clone —— 每线程独立构造
- 不优化 GC 性能（concurrent / generational）—— `add-concurrent-gc`

## IR / VM Mapping

- 无新 opcode
- VmCore 加 1 个字段：`vm_contexts: parking_lot::Mutex<Vec<VmContextPtr>>`
- 新类型：`pub(crate) struct VmContextPtr(*const VmContext);` 加 `unsafe impl Send for VmContextPtr {}` / `unsafe impl Sync for VmContextPtr {}`
- VmContext 加 1 个字段：`_pin: PhantomPinned`（标 !Unpin）
- VmContext 加 `impl Drop`（deregister）
- 修改：`VmContext::new() -> Pin<Box<Self>>`
- 修改：scanner closure 改为 walk registry

## Pipeline Steps

- [ ] Lexer
- [ ] Parser / AST
- [ ] TypeChecker
- [ ] IR Codegen
- [x] **VmContext 类型与构造路径**
- [x] **GC scanner closure**
- [x] interp 调用方更新（4 个 `&mut VmContext` → `&VmContext`）
- [x] JIT 同上
- [x] host embedding 适配 Pin<Box<VmContext>>
