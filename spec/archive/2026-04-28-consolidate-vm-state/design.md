# Design: consolidate-vm-state

## Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│  main.rs / 嵌入宿主                                                 │
│    let mut ctx = VmContext::new();                                │
│    ctx.install_lazy_loader(libs_dir, ...);                        │
│    Vm::new(module, mode).run(&mut ctx, hint)?                     │
└────────────────────────────┬─────────────────────────────────────┘
                             ▼
                  ┌─────────────────────┐
                  │   VmContext         │
                  │   ─────────────     │
                  │   static_fields:    │
                  │     HashMap         │
                  │   pending_exception:│
                  │     Cell<Option>    │
                  │   lazy_loader:      │
                  │     Option<LL>      │
                  └──────┬──────┬───────┘
                         │      │
            ┌────────────┘      └─────────────┐
            ▼                                  ▼
  ┌───────────────────────┐       ┌─────────────────────────┐
  │  interp::run(ctx,...) │       │  jit::JitModule::run    │
  │    exec_function(ctx) │       │    (ctx, entry_name)    │
  │      exec_instr(ctx)  │       │  ┌──────────────────┐   │
  │        StaticGet/Set: │       │  │ JitModuleCtx     │   │
  │          ctx.static_* │       │  │  vm_ctx:         │   │
  │        Throw:         │       │  │   *mut VmContext │←──┘
  │          ExecOutcome  │       │  │                  │
  │            ::Thrown   │       │  └────────┬─────────┘
  └───────────────────────┘                   │
                                              ▼
                                    ┌──────────────────┐
                                    │  jit helpers     │
                                    │  (extern "C")    │
                                    │   ctx_ref =      │
                                    │   &*ctx.vm_ctx   │
                                    │   ctx_ref.       │
                                    │     static_set() │
                                    │   ctx_ref.       │
                                    │     set_excpt()  │
                                    └──────────────────┘
```

## Decisions

### Decision 1: VmContext 是否持有 module 引用？

**问题**：`Vm::run` 现在持有 module 并 pass 给 interp/jit。VmContext 是否
也持有？

**选项**：
- A — VmContext 不持 module。`run(&mut ctx, &module, ...)` 双参数
- B — VmContext 持 module 引用。`run(&mut ctx, ...)` 单参数，ctx.module 访问

**决定**：**选 A**。理由：
- 一个 ctx 可以串行复用跑多个 module（hot-reload 场景）
- Module 是只读 IR 数据，不属于"VM 状态"
- 与 LazyLoader（持有 module reference 的部分由 ctx.lazy_loader 内部管理）
  正交

### Decision 2: JIT helpers 如何拿到 ctx？

**问题**：JIT helpers 是 `extern "C"`；增加参数会改 ABI。

**选项**：
- A — 加参数 `*mut VmContext` 到所有 helper 签名（每个 helper 多 1 个 reg）
- B — 把 ctx 嵌入 `JitModuleCtx::vm_ctx: *mut VmContext`，helper 通过现有
      `*const JitModuleCtx` 拿 ctx
- C — Cell-based shared state on JitModuleCtx（不需要 unsafe，但 RefCell
      runtime cost）

**决定**：**选 B**。理由：
- 不改 helper ABI，对现有 helper 调用点（translate.rs）改动最少
- 已经有 `JitModuleCtx` 通过 `*const JitModuleCtx` 传到所有 helper
- `vm_ctx: *mut VmContext` 字段；helper 内 `(*ctx).vm_ctx` 拿原始指针
- unsafe 是必需的（extern "C" + 跨 helper 共享可变状态），但收口在 JIT 模块内部

**安全约束**：
- `VmContext` 在 `Vm::run` 期间不能搬动（但不强制 Pin —— stack-allocated
  ctx 在 run 内部不会被搬）
- ctx 的生命周期必须 outlive `JitModule::run` 的整个调用栈
- 单线程使用：本变更不解锁多线程 VM；多线程同 ctx 是 future work

### Decision 3: pending_exception 是 Cell 还是 RefCell？

**问题**：JIT helper 是 `&JitModuleCtx`（`*const`），不能直接 `&mut ctx`。

**选项**：
- A — `Cell<Option<Value>>` —— Value 不是 Copy，Cell 不行
- B — `RefCell<Option<Value>>` —— 标准做法
- C — `UnsafeCell<Option<Value>>` —— JIT 单线程，绝对不会同时多个 borrow
- D — 直接 `*mut Option<Value>` 字段 —— 最 unsafe，但 hot path 性能最好

**决定**：**选 B（RefCell）**。理由：
- pending_exception 不是热点（每抛一次最多一次读写）
- RefCell 提供借用检查，多余的 unsafe 不必要
- 与 static_fields（也是 RefCell<HashMap>）一致

### Decision 4: lazy_loader.rs 重写策略

**问题**：lazy_loader 当前有 `install` / `uninstall` / `try_lookup_*` 等
自由函数，全部基于 thread_local STATE。如何重写？

**选项**：
- A — 把所有自由函数改 `VmContext` impl 方法
- B — 把 `LazyLoader` struct 暴露为 `pub`，VmContext 持有 `Option<LazyLoader>`，
      调用方改用 `ctx.lazy_loader.as_mut().and_then(|ll| ll.try_lookup_*())`
- C — 在 `metadata/lazy_loader.rs` 里保留 `LazyLoader` struct + 关联 impl，
      VmContext 提供 thin wrapper：`ctx.try_lookup_function(name)` 委托给
      `ctx.lazy_loader.as_ref()?.try_lookup_function(name)`

**决定**：**选 C**。理由：
- 保留 `LazyLoader` struct 内部不变（最小化重构面）
- VmContext 提供调用方习惯的 API
- 与 review2 §3 的 "包进 VmContext" 提法一致

### Decision 5: JIT helper ABI 不动；用 sync_in / sync_out 桥接（实施期发现）

**问题**：实施时发现 JIT 端 ~30 个 extern "C" arith/bool/mem helpers 签名为
`fn(frame, dst, a, b) -> u8` 等，**不带 `*const JitModuleCtx`**。它们调用
`set_exception(value)` 报错时无法从 ctx 拿状态。

**选项**：
- A — 改全部 helper 签名加 `ctx: *const JitModuleCtx`，translate.rs 同步更新
  helper 调用点（~30 处）；侵入式 ABI 改造
- B — 保留 jit/helpers.rs 内的 PENDING_EXCEPTION + STATIC_FIELDS thread_local，
  在 `JitModule::run` 边界与 VmContext **双向同步**
  （`sync_in_from_ctx` 进入前 → `sync_out_to_ctx` 退出后）

**决定**：**选 B**。理由：
- A 触及 ABI 工作量与本 spec scope 不成比例（C# 编译器编入的 Cranelift IR
  call sites 也要重新生成）
- B 在外部观察上等价：每次 `JitModule::run` 的边界 ctx 是规范来源；JIT 内部
  thread_local 是实施细节
- 多 ctx 顺序串行运行：每次 sync_in 都先用 ctx 状态 reset thread_local →
  跨 ctx 不污染（spec 验证场景 3 满足）
- 同线程并发 JIT 运行：本来 `JitModule::run` 是同步的，结构上不可能
- 留下后续 spec `extend-jit-helper-abi` 的清理空间

**代价**：runtime 内还剩 2 个 thread_local（jit::helpers.rs 的 PENDING_EXCEPTION
+ STATIC_FIELDS），加上 jit::frame.rs 的 FRAME_POOL（pure cache，原本 OOS）
= 3 个。VmContext 接管的核心状态（lazy_loader / interp 端 static_fields / 异常
统一传播）已收口。review2 §3 主要价值（多 VmContext 实例独立、interp 端 thread_local
归零）已实现。

## Implementation Notes

### Phase 1: 创建 VmContext + 删除死代码（最小改动版）

```rust
// src/runtime/src/vm_context.rs
pub struct VmContext {
    pub(crate) static_fields: RefCell<HashMap<String, Value>>,
    pub(crate) pending_exception: RefCell<Option<Value>>,
    pub(crate) lazy_loader: RefCell<Option<LazyLoader>>,
}

impl VmContext {
    pub fn new() -> Self { /* default fields */ }

    // ── Static fields ─────────────────────────────────────
    pub fn static_get(&self, field: &str) -> Value { /* ... */ }
    pub fn static_set(&self, field: &str, val: Value) { /* ... */ }
    pub fn static_fields_clear(&self) { /* ... */ }

    // ── Exception (used by JIT bridge) ───────────────────
    pub fn set_exception(&self, val: Value) { /* ... */ }
    pub fn take_exception(&self) -> Option<Value> { /* ... */ }

    // ── Lazy loader (delegate to LazyLoader struct) ──────
    pub fn install_lazy_loader(&self, libs_dir: Option<PathBuf>, main_pool_len: usize) { /* ... */ }
    pub fn install_lazy_loader_with_deps(&self, ...) { /* ... */ }
    pub fn try_lookup_function(&self, name: &str) -> Option<Arc<Function>> { /* ... */ }
    pub fn try_lookup_type(&self, name: &str) -> Option<Arc<TypeDesc>> { /* ... */ }
    pub fn try_lookup_string(&self, abs_idx: usize) -> Option<String> { /* ... */ }
    pub fn declared_namespaces(&self) -> Vec<String> { /* ... */ }
}
```

注意：方法签名都用 `&self`（不是 `&mut self`），因为内部用 `RefCell`
（runtime borrow check）。这与 thread_local 的现状一致 —— 调用方代码风格
基本不变，只是 receiver 从全局变成 ctx 引用。

### Phase 2: interp 改造

`exec_function(module, func, args)` → `exec_function(ctx, module, func, args)`：
所有递归 / 互调点同步加 ctx。

`StaticGet { dst, field }` → `frame.set(*dst, ctx.static_get(field))`
（替换 `dispatch::static_get(field)`）

异常路径：interp 全部走 `ExecOutcome::Thrown`，不再触碰 `pending_exception`。
`UserException` sentinel 删除。从 JIT 边界返回的异常通过
`ctx.take_exception()` 取出后立刻 wrap 成 `ExecOutcome::Thrown`。

### Phase 3: JIT 改造

`JitModuleCtx` 加字段：
```rust
pub struct JitModuleCtx {
    pub string_pool: Vec<String>,
    pub fn_entries: HashMap<String, FnEntry>,
    pub module: *const Module,
    pub vm_ctx: *mut VmContext,   // ← NEW
}
```

helper 改写示例：
```rust
// 之前
pub(super) fn set_exception(v: Value) {
    PENDING_EXCEPTION.with(|p| *p.borrow_mut() = Some(v));
}

// 之后
pub(super) unsafe fn set_exception(ctx_ptr: *mut VmContext, v: Value) {
    (*ctx_ptr).set_exception(v);
}
```

helper 调用方（`jit_call` / `jit_vcall` 等）：
```rust
let ctx_ref = &*ctx;            // *const JitModuleCtx
let vm_ctx = ctx_ref.vm_ctx;    // *mut VmContext
// ... use vm_ctx for set_exception / static_set / etc.
```

`JitModule::run(&mut self, vm_ctx: &mut VmContext, entry_name: &str)`：
- 把 `&mut *vm_ctx as *mut VmContext` 写入 `self.ctx.vm_ctx`
- 调用 entry function（已通过 jit_module_ctx 拿到 vm_ctx）
- run 结束清空 `self.ctx.vm_ctx = std::ptr::null_mut()` 防御性

### Phase 4: lazy_loader 改造

把 `lazy_loader::STATE` thread_local + 自由函数全部删除；`LazyLoader` struct
+ impl 保留。所有调用点改用 `ctx.try_lookup_function(name)` 等委托方法。

`metadata/loader.rs` 里使用 `lazy_loader::xxx` 的两处改为接受 ctx 参数。

### 调用点路径变化

```rust
// 之前
crate::metadata::lazy_loader::try_lookup_function(name)

// 之后
ctx.try_lookup_function(name)
```

约 10-15 处调用点。

## Testing Strategy

- **单元测试**：
  - `vm_context.rs` 末尾加 `#[cfg(test)] mod vm_context_tests;` →
    `vm_context_tests.rs` 测 static_get/set/clear、set_exception/take、
    install_lazy_loader 隔离
  - 既有 `metadata/lazy_loader_tests.rs`：重写以 ctx-based API
- **Integration**：
  - `runtime/tests/zbc_compat.rs` 已有，保证现有 IR 解码不受影响
  - 新增 `runtime/tests/vm_context.rs`：构造 2 个 VmContext，验证 static
    fields 隔离场景
- **VM golden**：`./scripts/test-vm.sh`（200 个 interp + jit 测试）—— 必须 100% 通过
- **Cross-zpkg**：`./scripts/test-cross-zpkg.sh` —— stdlib 跨包加载场景，
  lazy_loader 重构的回归保护

## 关键不变量

- VmContext 在 `Vm::run` 期间地址稳定（栈分配也 OK，run 内部不搬）
- 同一 ctx 不能跨线程共享（本变更不引入 Send/Sync）—— 保留 single-thread-per-ctx
  约束，多线程 VM 是后续 spec
- JIT 编译过的函数与同一 ctx 关联：JitModule 内 `vm_ctx` 指针仅在 `run`
  调用栈期间有效，run 返回后置空
