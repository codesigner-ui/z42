# Design: JIT-mode GC safepoint insertion

## Architecture

```
Interp side (already landed, add-gc-safepoint):
  exec_function entry              → check_safepoint(ctx)
  Terminator::Br when backward     → check_safepoint(ctx)
  Terminator::BrCond when backward → check_safepoint(ctx)
  Instruction::Call return         → check_safepoint(ctx)
  Instruction::CallIndirect return → check_safepoint(ctx)

JIT side (this spec):
  translate_function entry         → builder.ins().call(hr_check_safepoint, &[frame, ctx])
  Terminator::Br when target ≤ idx → same
  Terminator::BrCond unconditional → same (target unknown at compile time)
  Instruction::Call after handle   → same
  Instruction::CallIndirect after  → same

  jit_check_safepoint(frame, ctx) extern "C":
    let vm_ctx = unsafe { &*(*ctx).vm_ctx };
    crate::gc::safepoint::check_safepoint(vm_ctx);
```

The helper is a thin trampoline — all the protocol logic stays in
`gc/safepoint.rs`. JIT-emitted code is one `call` instruction per
insertion site.

## Decisions

### Decision 1: BrCond unconditional check

**问题**：`BrCond { true_label, false_label }` 编译期知道两个 target 索引但
运行时取决于 cond reg。可以在 BrCond 之前先 check_safepoint，再 brif；或
在每个 target block 入口分别处理（复杂）。

**决定**：**unconditional check before brif**。理由：
- safepoint check Idle fast path 是 1 Mutex lock + 1 enum compare ~10ns
- forward-branch-heavy 代码会多吃一次 check，但相对其它指令开销可忽略
- 简化 IR emission（不需要在 cl_blocks 中插入 trampoline block）
- 与 interp 的 BrCond 处理对齐（interp 也是 unconditional check 在
  backward target 时）

实际：在 Terminator::Br 优化为"仅 backward branch 时 check"（target ≤ block_idx），
但 BrCond 简化为 unconditional。Br 是字节码里更常见的循环回边载体。

### Decision 2: Function entry safepoint check

**问题**：interp 的 `exec_function` 入口 check 是为了让"新 spawn worker
进入第一个函数前就 park"。JIT 函数会被 caller 通过 Call helper 调，caller
返回后会再 check —— 但 caller 可能是另一个 JIT 函数。回溯到根 caller 总能
找到一个 interp frame（worker 入口是 `interp::run_with_static_init`）；
所以理论上 worker 在进入 JIT 之前已经 check 过。

**决定**：**仍然加 entry check**。理由：
- 防御性：若 caller-chain 全 JIT（多层 JIT-to-JIT call），中间可能漏 check
- 进入新函数的开销已经包含 frame 分配 + 参数搬运等几十 ns；多一次 ~10ns
  check 是不可见
- 一致性：与 interp 完全对应，4 个 site 一一对齐，debugging 容易

### Decision 3: jit_check_safepoint helper 签名

**问题**：所有现有 JIT helpers 都接 `(frame, ctx, ...)` —— 但 check_safepoint
只需要 ctx。要不要省略 frame 参数？

**决定**：**保留 frame 参数（unused）**。理由：
- 与 helper ABI 约定一致（`extend-jit-helper-abi` Phase 2.E 后所有 helpers
  都是 `(frame, ctx, ...)` 格式）
- ABI 一致性 > 微小代码大小优化
- 修复未来若 helper 想 inspect frame.regs 时不用改 ABI

注释里标 `_frame` 表示未用。

### Decision 4: 不通过 jit_check_safepoint 内部 panic 传播错误

**问题**：check_safepoint 是 infallible（park-and-wake，不会 throw）。但 helper
ABI 通常会用 i8 返回值传 throw 信号（0 = ok / 1 = thrown）。

**决定**：**helper 返回 unit (no return value)**。理由：
- check_safepoint 物理上不会 throw（park-and-resume 不涉及 z42 exception
  路径）
- helper signature `[ptr, ptr], []`（无返回）让调用方代码更短，emit 一行
  `builder.ins().call(hr_check_safepoint, &[frame, ctx])` 不需要 handle
  result
- 如果未来 check_safepoint 引入 throw 路径（不太可能 — safepoint 是基础
  设施），届时改 ABI 是 breaking change，记入未来 spec

### Decision 5: Call / CallIndirect 都加 post-call check

**问题**：`Instruction::Call` 和 `Instruction::CallIndirect` 都通过 JIT helper
(`hr_call` / `hr_call_indirect`) 实现，helper 内部 trap on throw（return
non-zero）。post-call site 已经有 if-thrown 跳转处理。在哪插 check_safepoint？

**决定**：**在 throw 检查之后**。理由：
- 若 callee throw，控制流走 catch 路径，本帧的 check 应该走 catch 路径
  自己的 site（catch entry 不在本 spec scope，interp 也没在 catch entry
  插 check）
- 若 callee 正常返回（return code = 0），继续下个 instruction —— 此时
  check_safepoint 让"长 callee 期间产生的 GC 请求"被本帧承接
- 同 interp 顺序

## Implementation Notes

### 1. Helper definition

```rust
// src/runtime/src/jit/helpers/control.rs (append)

/// add-gc-safepoint-jit (2026-05-21): JIT-emitted code calls this at each
/// safepoint insertion site (function entry, backward branches, post-Call).
/// Trampolines into the shared `gc::safepoint::check_safepoint` so the
/// JIT path follows the same Idle/Requested/Marking protocol as interp.
#[no_mangle]
pub unsafe extern "C" fn jit_check_safepoint(
    _frame: *mut crate::jit::frame::JitFrame,
    ctx:    *const crate::jit::frame::JitModuleCtx,
) {
    let vm_ctx = unsafe { &*(*ctx).vm_ctx };
    crate::gc::safepoint::check_safepoint(vm_ctx);
}
```

### 2. registry.rs

```rust
// HelperIds field:
pub check_safepoint: FuncId,

// register_symbols (in `// control` section):
reg!("jit_check_safepoint", control::jit_check_safepoint);

// declare_imports (in `// control` block):
check_safepoint: decl!("jit_check_safepoint", [ptr, ptr], []),
```

### 3. translate.rs insertions

```rust
// Import (with other hr_* imports near top of translate_function):
let hr_check_safepoint = imp!(helper_ids.check_safepoint);

// (a) Function entry — after frame_val / ctx_val are bound, before
//     emitting the first instruction:
builder.ins().call(hr_check_safepoint, &[frame_val, ctx_val]);

// (b) Terminator::Br — when target ≤ current block:
Terminator::Br { label } => {
    let target = z42_func.blocks.iter().position(|b| &b.label == label)
        .expect("Br label not found");
    if target <= block_idx {
        builder.ins().call(hr_check_safepoint, &[frame_val, ctx_val]);
    }
    builder.ins().jump(cl_blocks[target], &[]);
}

// (c) Terminator::BrCond — unconditional check before brif:
Terminator::BrCond { cond, true_label, false_label } => {
    builder.ins().call(hr_check_safepoint, &[frame_val, ctx_val]);
    // ... existing cond/brif emission ...
}

// (d) Instruction::Call / CallIndirect — after the throw-or-no-throw
//     dispatch dispatch but before the next iteration of the instruction
//     loop. Insert at the end of the Call match arm.
```

### 4. Test pattern (cross_thread_smoke.rs)

The existing cross_thread integration tests use `make_void_action_module`
which creates a 1-block-1-Ret function. To exercise the JIT safepoint
path we need a Module compiled to JIT mode (set `exec_mode: ExecMode::Jit`
on the function + JIT-compile it). Existing tests run interp; this spec
adds JIT-specific tests.

Compile a minimal JIT module via `crate::jit::compile_module` (or
test-runner's bootstrap path). Spawn workers running the JIT function in
a loop; main thread requests gc_pause; assert no deadlock.

If compiling a real JIT module is too much fixture overhead for an
integration test, fall back to: directly call `jit_check_safepoint` from
a worker thread (treating it like the interp test pattern). This proves
the helper path, even if it doesn't exercise the full JIT-emitted code
path.

**Pragmatic v0**: do both — one test directly calls the helper (proof
of the trampoline correctness), one test compiles a real JIT function
(proof the translate.rs insertions are emitted correctly).

## Testing Strategy

- **Rust unit tests**: not needed beyond existing `gc/safepoint_tests.rs`
  (the helper is a one-line trampoline; logic lives in check_safepoint)
- **Cross-thread integration**:
  - `jit_check_safepoint_helper_invokes_protocol` — direct call from
    worker thread with manually-set `needs_auto_collect` flag, verifies
    the trampoline correctly drives check_safepoint (proves the helper
    + ABI correctness without needing JIT codegen)
  - `jit_worker_with_gc_collect_no_deadlock` — compile a minimal JIT
    function via test fixtures; 4 workers run it in a loop; collector
    requests gc_pause; assert all join + gc_cycles incremented
- **GREEN gate**: `test-all.sh` 6 stages including stdlib regression

## Deferred / Future Work

### `add-gc-safepoint-counter-throttling`
- Optimization for hot JIT loops where safepoint check at every backward
  branch adds visible overhead. Counter-based throttling reduces frequency
  while maintaining liveness bound

### `add-gc-safepoint-aot`
- AOT mode safepoint insertion when AOT lands (roadmap M9)
