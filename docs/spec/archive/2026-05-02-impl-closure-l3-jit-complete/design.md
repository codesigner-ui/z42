# Design: JIT 闭包指令翻译实施策略

## Architecture

```
JIT 编译模块时:
  ↓ [mod.rs::compile_module]
       JITBuilder::new()
       builder.symbol("jit_load_fn", helpers_closure::jit_load_fn as *const u8)
       builder.symbol("jit_mk_clos", helpers_closure::jit_mk_clos as *const u8)
       builder.symbol("jit_call_indirect", helpers_closure::jit_call_indirect as *const u8)
  ↓ [translate.rs::declare_helpers]
       HelperIds.load_fn        = jit.declare_function("jit_load_fn", ...)
       HelperIds.mk_clos        = jit.declare_function("jit_mk_clos", ...)
       HelperIds.call_indirect  = jit.declare_function("jit_call_indirect", ...)
  ↓ [translate.rs::translate_instr]
       Instruction::LoadFn { dst, func }       → call helpers.load_fn
       Instruction::MkClos { dst, fn_name, captures } → call helpers.mk_clos
       Instruction::CallIndirect { dst, callee, args } → call helpers.call_indirect

运行时（JIT 编译后的代码）:
  ↓ JitFn(frame*, ctx*) 内部
       ↓ 调用 helper
            helpers_closure::jit_load_fn      // 写 FuncRef 到 reg
            helpers_closure::jit_mk_clos      // 分配 env + 写 Closure 到 reg
            helpers_closure::jit_call_indirect // 解析 + 调用 + 处理 返回
  ↓ Helper 内部使用 Rust 安全代码（解 frame 指针 + heap.alloc_array + ctx.fn_entries）
```

## Decisions

### Decision 1: 单独文件 `helpers_closure.rs`
**问题**：3 个 helper 加在哪？
**决定**：新建 `src/runtime/src/jit/helpers_closure.rs`。
**理由**：closure 是独立子系统；后续 monomorphize / stack 优化也可加到此文件；helpers_object.rs 已 458 行接近软限制。

### Decision 2: 全部走 helper（不在 Cranelift IR 中内联 Value match）
**决定**：3 条指令的 Cranelift IR 端只做"打包参数 + 调用 helper + 检查返回码"。
**理由**：Value enum 在 Cranelift IR 层操作复杂；helper 用 Rust 写更安全更易复用 interp 的逻辑；性能差异微（一次调用开销 vs 复杂 IR 生成成本）。

### Decision 3: helper 签名标准化
所有 helper 遵循统一模式：
```rust
pub extern "C" fn jit_xxx(
    frame: *mut JitFrame,
    ctx: *const JitModuleCtx,
    /* instruction-specific args */
) -> u8   // 0 = ok, 1 = exception (异常已通过 ctx.set_pending_exception 投递)
```
**理由**：与 `jit_call` / `jit_obj_new` / `jit_vcall` 一致；JIT 翻译端的样板代码可复用。

### Decision 4: capture regs 编码
**问题**：`MkClos { captures: Vec<Reg> }` 中 captures 数组长度可变；如何传给 helper？
**决定**：
- Cranelift 端：把 captures 数组以"指针 + len"形式传（参考现有 `regs_val!` macro）
- captures slice 的存储位置：在 translate.rs 中分配一段 static 内存（每个 MkClos 站点独立 leak 一段）；helper 读时 `slice::from_raw_parts(ptr, len)`
- **更简单的等价方案**：把 captures 数组 box 进 `Vec<u32>` 然后 `Box::leak`，传指针 + len

**Risk**：内存泄漏。每个 MkClos 站点 leak 一次 Vec<u32>。z42 的 JIT 模块是模块级长寿；可接受。后续可改为 ahead-of-time 分配在 JitModuleCtx 的 arena。

### Decision 5: fn_name / func 字符串编码
**决定**：跟 captures 一样用静态指针 + len。`Box::leak(Box::new(name.to_string()))`。

**Risk 同上**：模块级 leak，量可控。

### Decision 6: CallIndirect 的 args 编码
**决定**：args 数组也用指针 + len 编码（同 captures）。

### Decision 7: 调用约定 — 异常处理
**问题**：helper 调用失败（如 callee not found）如何传播？
**决定**：
- helper 返回 `u8`：0 = OK，1 = exception
- 异常详情通过 `ctx.set_pending_exception(err)` 投递（pattern 已在 helpers.rs 提供）
- translate.rs 端：`brif` 检查返回码；非零 → 跳转到当前函数的 catch 块或 fall through 到 ret 1

参考实现：`helpers_object.rs::jit_call` 第 30-40 行。

## Implementation Notes

### `helpers_closure.rs` 骨架

```rust
//! L3 closure JIT helpers. See impl-closure-l3-jit-complete spec.
use crate::interp;
use crate::metadata::Value;
use crate::vm_context::VmContext;
use crate::jit::frame::{JitFrame, JitModuleCtx};
use crate::jit::helpers::{set_pending_exception, JitFn};
use std::slice;

#[no_mangle]
pub extern "C" fn jit_load_fn(
    frame: *mut JitFrame,
    _ctx: *const JitModuleCtx,
    dst: u32,
    name_ptr: *const u8, name_len: usize,
) -> u8 {
    let name = unsafe { std::str::from_utf8_unchecked(slice::from_raw_parts(name_ptr, name_len)) };
    let frame = unsafe { &mut *frame };
    frame.set(dst, Value::FuncRef(name.to_string()));
    0
}

#[no_mangle]
pub extern "C" fn jit_mk_clos(
    frame: *mut JitFrame,
    ctx: *const JitModuleCtx,
    dst: u32,
    name_ptr: *const u8, name_len: usize,
    caps_ptr: *const u32, caps_len: usize,
) -> u8 {
    let name = unsafe { std::str::from_utf8_unchecked(slice::from_raw_parts(name_ptr, name_len)) }.to_string();
    let frame = unsafe { &mut *frame };
    let ctx_ref = unsafe { &*ctx };
    let vm_ctx = unsafe { &mut *ctx_ref.vm_ctx };
    let caps = unsafe { slice::from_raw_parts(caps_ptr, caps_len) };
    let mut env_vec = Vec::with_capacity(caps_len);
    for &r in caps {
        env_vec.push(frame.regs[r as usize].clone());
    }
    let env_val = vm_ctx.heap().alloc_array(env_vec);
    let env = match env_val {
        Value::Array(rc) => rc,
        _ => unreachable!("alloc_array must return Value::Array"),
    };
    frame.set(dst, Value::Closure { env, fn_name: name });
    0
}

#[no_mangle]
pub extern "C" fn jit_call_indirect(
    frame: *mut JitFrame,
    ctx: *const JitModuleCtx,
    dst: u32,
    callee: u32,
    args_ptr: *const u32, args_len: usize,
) -> u8 {
    let frame = unsafe { &mut *frame };
    let ctx_ref = unsafe { &*ctx };
    let vm_ctx = unsafe { &mut *ctx_ref.vm_ctx };

    let (fname, env_opt) = match &frame.regs[callee as usize] {
        Value::FuncRef(n) => (n.clone(), None),
        Value::Closure { env, fn_name } => (fn_name.clone(), Some(env.clone())),
        other => {
            set_pending_exception(vm_ctx,
                anyhow::anyhow!("CallIndirect: expected FuncRef or Closure, got {:?}", other));
            return 1;
        }
    };

    let args = unsafe { slice::from_raw_parts(args_ptr, args_len) };
    let mut arg_vals = Vec::with_capacity(args_len + 1);
    if let Some(env) = env_opt {
        arg_vals.push(Value::Array(env));
    }
    for &r in args {
        arg_vals.push(frame.regs[r as usize].clone());
    }

    // Lookup callee + invoke.
    match ctx_ref.fn_entries.get(fname.as_str()) {
        Some(entry) => {
            // Build callee frame, register for GC, invoke, unregister.
            let mut callee_frame = JitFrame::new(entry.max_reg, &arg_vals);
            vm_ctx.push_frame_regs(&callee_frame.regs as *const _);
            let f: JitFn = unsafe { std::mem::transmute(entry.ptr) };
            let r = unsafe { f(&mut callee_frame, ctx) };
            vm_ctx.pop_frame_regs();

            if r != 0 { return 1; }   // exception bubbles up

            // Set dst to the callee's return value (reg 0 by convention)
            frame.set(dst, callee_frame.regs.get(0).cloned().unwrap_or(Value::Null));
            0
        }
        None => {
            set_pending_exception(vm_ctx,
                anyhow::anyhow!("CallIndirect: undefined function `{fname}`"));
            1
        }
    }
}
```

> **注**：实施期间需确认 `JitFrame::new` / `JitFn` / `set_pending_exception` /
> `push_frame_regs` 等 helper 的精确签名。上面是按调研结论写的预期模式。

### `translate.rs` 端三条指令

```rust
// Helper to leak a String → (ptr, len)
fn leak_str(s: &str) -> (i64, i64) {
    let boxed = s.to_string().into_boxed_str();
    let bytes: &'static [u8] = Box::leak(boxed).as_bytes();
    (bytes.as_ptr() as i64, bytes.len() as i64)
}

// Helper to leak a &[u32] → (ptr, len)
fn leak_regs(rs: &[u32]) -> (i64, i64) {
    let v: Box<[u32]> = rs.to_vec().into_boxed_slice();
    let leaked: &'static [u32] = Box::leak(v);
    (leaked.as_ptr() as i64, leaked.len() as i64)
}

// LoadFn case
Instruction::LoadFn { dst, func } => {
    let (np, nl) = leak_str(func);
    let dst_v   = builder.ins().iconst(types::I32, *dst as i64);
    let np_v    = builder.ins().iconst(ptr, np);
    let nl_v    = builder.ins().iconst(ptr, nl);
    let r = builder.ins().call(helper_ids.load_fn,
        &[frame_ptr, ctx_ptr, dst_v, np_v, nl_v]);
    let r0 = builder.inst_results(r)[0];
    install_exception_check(&mut builder, r0);
}

// MkClos case
Instruction::MkClos { dst, fn_name, captures } => {
    let (np, nl) = leak_str(fn_name);
    let (cp, cl) = leak_regs(captures);
    let dst_v   = builder.ins().iconst(types::I32, *dst as i64);
    let np_v    = builder.ins().iconst(ptr, np);
    let nl_v    = builder.ins().iconst(ptr, nl);
    let cp_v    = builder.ins().iconst(ptr, cp);
    let cl_v    = builder.ins().iconst(ptr, cl);
    let r = builder.ins().call(helper_ids.mk_clos,
        &[frame_ptr, ctx_ptr, dst_v, np_v, nl_v, cp_v, cl_v]);
    let r0 = builder.inst_results(r)[0];
    install_exception_check(&mut builder, r0);
}

// CallIndirect case
Instruction::CallIndirect { dst, callee, args } => {
    let (ap, al) = leak_regs(args);
    let dst_v    = builder.ins().iconst(types::I32, *dst as i64);
    let callee_v = builder.ins().iconst(types::I32, *callee as i64);
    let ap_v     = builder.ins().iconst(ptr, ap);
    let al_v     = builder.ins().iconst(ptr, al);
    let r = builder.ins().call(helper_ids.call_indirect,
        &[frame_ptr, ctx_ptr, dst_v, callee_v, ap_v, al_v]);
    let r0 = builder.inst_results(r)[0];
    install_exception_check(&mut builder, r0);
}
```

### `mod.rs` 端 helper 符号注册

```rust
// 在 compile_module 的 jit_builder 配置部分添加：
jit_builder.symbol("jit_load_fn",       crate::jit::helpers_closure::jit_load_fn       as *const u8);
jit_builder.symbol("jit_mk_clos",       crate::jit::helpers_closure::jit_mk_clos       as *const u8);
jit_builder.symbol("jit_call_indirect", crate::jit::helpers_closure::jit_call_indirect as *const u8);
```

### `helpers.rs` 端 HelperIds 扩展

```rust
pub struct HelperIds {
    // ... existing ...
    pub load_fn: FuncId,
    pub mk_clos: FuncId,
    pub call_indirect: FuncId,
}
```

## Testing Strategy

| 验证目标 | 测试 |
|---|---|
| LoadFn JIT 行为 | `golden/run/lambda_l2_basic` 移除 interp_only 后 JIT 模式通过 |
| MkClos JIT 行为 | `closure_l3_capture` + `closure_l3_loops` 移除 interp_only |
| CallIndirect FuncRef 路径 | lambda_l2_basic 的 Apply((int)x => x*4, 5) 调用 |
| CallIndirect Closure 路径 | closure_l3_capture 的 inc() / pred() / Apply 调用 |
| Local fn 闭包 | local_fn_l2_basic 移除 interp_only（无 capture）+ closure_l3_capture（含 capture） |

### GREEN 标准

- `dotnet build` / `cargo build`：0 错误
- `dotnet test`：100% 通过
- `./scripts/test-vm.sh`：100% 通过 **on both interp + jit modes**（关键：jit 模式 4 个 closure golden 现在必须通过）

## Risk & Open Items

| 风险 | 缓解 |
|------|------|
| `Box::leak` 累积内存泄漏 | 模块级长寿；闭包 IR 站点数量受用户代码控制；后续可优化为 ModuleCtx arena |
| Helper signature 与 `jit_call` 风格不一致 | 严格遵循 helper 命名 + 返回 u8 + frame/ctx 首参；按 helpers_object.rs 模式抄 |
| `jit_call_indirect` 返回值约定（dst 设置）| 与 `jit_call` 一致：callee frame.regs[0] = ret，复制到 caller frame.regs[dst] |
| String / Vec ABI 边界 | 全用 ptr + len 模式，static lifetime；helper 内 `from_raw_parts` 安全（前提：translate.rs leak 的指针不被释放）|
| GC root 注册 | callee frame 通过 `vm_ctx.push_frame_regs(...)` / `pop_frame_regs()` 配对，与 `jit_call` 模式一致 |
