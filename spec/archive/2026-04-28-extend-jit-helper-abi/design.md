# Design: extend-jit-helper-abi

## Architecture

```
                ┌─────────────────────────────────────────┐
                │  Vm::run(&mut ctx, hint)                │
                │   ↓                                      │
                │  jit::run(ctx, module, entry_name)      │
                │   ↓                                      │
                │  jit_module.run(ctx, entry_name)        │
                │   ↓                                      │
                │  self.ctx.vm_ctx = ctx as *mut          │
                │   ↓                                      │
                │  jit_fn(&mut frame, &*self.ctx)         │
                └─────────────┬───────────────────────────┘
                              │ (extern "C")
                              ▼
            ┌──────────────────────────────────────┐
            │ Cranelift-emitted machine code       │
            │   call jit_add(frame, ctx, dst, a, b)│  ← 添加 ctx 第 2 参
            │   call jit_str_concat(frame, ctx,...)│
            │   call jit_static_get(frame, ctx,...)│
            └────────────┬─────────────────────────┘
                         │
                         ▼
            ┌──────────────────────────────────────┐
            │ Rust helper (extern "C")             │
            │   pub unsafe extern "C" fn jit_X(    │
            │     frame: *mut JitFrame,            │
            │     ctx: *const JitModuleCtx,        │  ← 新参
            │     ...args                           │
            │   ) -> u8                            │
            │     {                                 │
            │       ... compute ...                 │
            │       set_exception(                  │
            │         vm_ctx_ref(ctx),              │
            │         err_val);                     │
            │       1                               │
            │     }                                 │
            └────────────┬─────────────────────────┘
                         │
                         ▼
              ┌─────────────────────────┐
              │  VmContext              │
              │  static_fields, etc.    │
              └─────────────────────────┘
```

## Decisions

### Decision 1: 单参 vs 双参承载 ctx

**问题**：每个 helper 怎么拿到 VmContext？

**选项**：
- A — 加 `*const JitModuleCtx` 第 2 参，通过 `(*jit_ctx).vm_ctx` 拿 VmContext
  指针；helpers.rs 提供 `vm_ctx_ref(jit_ctx)` 辅助
- B — 加 `*mut VmContext` 直接作为 helper 参数

**决定**：**A**。理由：
- `JitModuleCtx` 已存在并通过 entry function 自然到达；只是多数 helper 没用
- `JitModuleCtx::vm_ctx: *mut VmContext` 字段已在 consolidate-vm-state 加上
- 2 层间接（helper → JitModuleCtx → VmContext）让 unsafe 边界明确，
  `vm_ctx_ref` 工具集中收口 safety contract

### Decision 2: 是否拆 macro 简化批量改造

**问题**：helpers_arith.rs 里有 `arith_op!($name, $int_op, $float_op)` macro
批量定义 helper（jit_sub / jit_mul / jit_div / jit_rem 等都来自 macro 实例
化）。改 ABI 时一次改 macro 还是逐个手写？

**决定**：**改 macro 一次**。macro 内部 `pub unsafe extern "C" fn $name(
frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8` 改成接 ctx 即可，
所有 macro 实例自动跟进。同样 `bitwise_binop!` macro 一并改。

### Decision 3: vm_ctx_ref 的 SAFETY 契约

```rust
/// SAFETY: caller must ensure:
///   1. `jit_ctx` is non-null and points to a valid JitModuleCtx
///   2. `(*jit_ctx).vm_ctx` is non-null (true while inside JitModule::run)
///   3. The returned reference's lifetime does not outlive the helper call
pub(super) unsafe fn vm_ctx_ref<'a>(jit_ctx: *const JitModuleCtx) -> &'a VmContext {
    &*((*jit_ctx).vm_ctx)
}
```

调用方使用：

```rust
unsafe {
    let vm_ctx = vm_ctx_ref(ctx);
    set_exception(vm_ctx, value);
    // 或直接 vm_ctx.set_exception(value);
}
```

## Implementation Notes

### Phase 1: helpers.rs 顶层签名变化

```rust
// 之前（thread_local backing）
pub(super) fn set_exception(v: Value) { PENDING_EXCEPTION.with(...); }
pub(super) fn static_get(field: &str) -> Value { STATIC_FIELDS.with(...); }

// 之后（ctx 直传）
pub(super) fn set_exception(ctx: &VmContext, v: Value) {
    ctx.set_exception(v);
}
pub(super) fn static_get(ctx: &VmContext, field: &str) -> Value {
    ctx.static_get(field)
}
```

`take_exception_error(&VmContext) -> anyhow::Error` 同理。

`vm_ctx_ref` 在 helpers.rs `pub(super)` 暴露给 helper 子模块。

### Phase 2: helpers_arith.rs（无 ctx → 加 ctx）

8 个 helper（jit_add, jit_sub, jit_mul, jit_div, jit_rem, jit_eq, jit_ne,
arith_op! macro 系列, jit_lt, jit_le, jit_gt, jit_ge, jit_and, jit_or, jit_not,
jit_neg, bitwise_binop! macro 系列, jit_bit_not）：

```rust
// Before
pub unsafe extern "C" fn jit_add(frame: *mut JitFrame, dst: u32, a: u32, b: u32) -> u8

// After
pub unsafe extern "C" fn jit_add(
    frame: *mut JitFrame,
    ctx:   *const JitModuleCtx,
    dst:   u32, a: u32, b: u32,
) -> u8
```

内部 `set_exception(value)` 调用改：

```rust
// Before
Err(e) => { set_exception(Value::Str(e.to_string())); return 1; }

// After
Err(e) => {
    set_exception(unsafe { vm_ctx_ref(ctx) }, Value::Str(e.to_string()));
    return 1;
}
```

### Phase 3: helpers_mem.rs（13 helper，1 已带 ctx）

带 ctx 的：jit_to_str（保持）

不带 ctx 的：jit_const_i32 / jit_const_i64 / jit_const_f64 / jit_const_bool /
jit_const_char / jit_const_null / jit_const_str（实际已带 ctx，需确认）/
jit_copy / jit_str_concat / jit_get_bool / jit_set_ret / jit_throw /
jit_install_catch

`jit_throw` 现在用 `set_exception` 存值；要 ctx。
`jit_str_concat` 失败时 `set_exception`；要 ctx。

### Phase 4: helpers_object.rs（15 helper，5 已带 ctx）

带 ctx 的：jit_call / jit_obj_new / jit_vcall / jit_is_instance / jit_as_cast

不带 ctx 的需加：jit_builtin / jit_array_new / jit_array_new_lit /
jit_array_get / jit_array_set / jit_array_len / jit_field_get /
jit_field_set / jit_static_get / jit_static_set

`jit_static_get` / `jit_static_set` 改为通过 ctx 访问而非 thread_local：

```rust
// Before
(*frame).regs[dst as usize] = super::helpers::static_get(field);

// After
(*frame).regs[dst as usize] = unsafe {
    super::helpers::vm_ctx_ref(ctx).static_get(field)
};
```

### Phase 5: translate.rs

`declare_helpers` 内每个 `decl!` 在第 1 个 ptr 后插入第 2 个 ptr：

```rust
// Before
add: decl!("jit_add", [ptr, i32t, i32t, i32t], [i8t]),

// After
add: decl!("jit_add", [ptr, ptr, i32t, i32t, i32t], [i8t]),
```

helper 调用点（`builder.ins().call(...)`）插入 `ctx_val`：

```rust
// Before
let inst = builder.ins().call(hr_add, &[frame_val, d, av, bv]);

// After
let inst = builder.ins().call(hr_add, &[frame_val, ctx_val, d, av, bv]);
```

`ctx_val` 已经是 entry block 的第 2 参（`builder.block_params(cl_blocks[0])[1]`），
直接复用。

### Phase 6: jit/mod.rs 清理

```rust
// Before
sync_in_from_ctx(ctx);
self.ctx.vm_ctx = ctx as *mut VmContext;
let r = unsafe { f(&mut frame, &*self.ctx) };
self.ctx.vm_ctx = std::ptr::null_mut();
sync_out_to_ctx(ctx);

// After
self.ctx.vm_ctx = ctx as *mut VmContext;
let r = unsafe { f(&mut frame, &*self.ctx) };
self.ctx.vm_ctx = std::ptr::null_mut();
```

`use helpers::{take_exception_error, sync_in_from_ctx, sync_out_to_ctx, JitFn}`
里的 sync 项删除；`take_exception_error` 改为 `take_exception_error(ctx)`
（因为它现在接 &VmContext）。

## Testing Strategy

- **单元测试**：cargo test —— vm_context_tests / lazy_loader_tests / corelib
  tests / metadata tests 应保持全过（59+9 = 68）
- **Integration**：`zbc_compat.rs` 4 个测试不变（不依赖 JIT 内部）
- **Golden**：`scripts/test-vm.sh` 200 个 interp + jit 测试 ——
  必须 100% 通过（JIT 模式是本变更主要受影响面）
- **Cross-zpkg**：`scripts/test-cross-zpkg.sh` 1 个 e2e 测试
- **C# 端**：dotnet build / dotnet test 完备性 verify（应零变化）

## 关键不变量

- VmContext 在 `JitModule::run` 期间地址稳定（栈上引用安全）
- helper 不持久化 ctx 引用（每次调用重新 vm_ctx_ref）
- `(*jit_ctx).vm_ctx` 在 entry / exit 时严格 set / null（防御性，避免悬空指针被
  误用）
