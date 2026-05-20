# Proposal: JIT-mode GC safepoint insertion

## Why

`add-gc-safepoint` (2026-05-20) landed the cooperative safepoint protocol
in **interp** — the interp dispatch loop calls `check_safepoint(ctx)` at
function entry, backward branches, and Call return so worker threads
park promptly when GC requests a pause.

JIT mode was explicitly left out (Decision 5 of that spec). A worker
thread executing JIT-compiled native code never enters the interp
dispatch loop and therefore never sees a safepoint check. If GC fires
on another thread, `request_gc_pause` waits for `parked_count == N-1`
but the JIT-running worker never parks → **deadlock**.

This is the same class of bug as the pre-`add-gc-safepoint` race on
frame.regs, but worse — it doesn't even need pressure; any
`Std.GC.Collect()` call from a different thread while a worker is in
JIT-land hangs the process.

JIT mode is feature-flagged (`jit` cargo feature, opt-in via `--mode
jit`), so the deadlock affects only users who explicitly enable JIT
*and* spawn threads — but those users hit it immediately. Closing the
gap puts JIT on equal safety footing with interp.

## What Changes

- **New `jit_check_safepoint` helper** — `extern "C"` Rust fn called from
  JIT code, takes `(frame: *mut JitFrame, ctx: *const JitModuleCtx)`,
  reaches `VmContext` via `(*ctx).vm_ctx`, calls
  `crate::gc::safepoint::check_safepoint(vm_ctx)`
- **HelperIds.check_safepoint** registered alongside the other 50+
  helpers in `helpers/registry.rs` (3 places per existing convention:
  HelperIds struct, register_symbols, declare_imports)
- **JIT translate emits `check_safepoint` calls** at the three points
  mirroring interp:
  - Function entry — at the start of cl_blocks[0] before the first
    instruction emit
  - `Terminator::Br` — when target block index ≤ current block index
    (backward branch heuristic), emit before the `jump` ins
  - `Terminator::BrCond` — same; but here we don't know the target until
    runtime, so emit `check_safepoint` unconditionally before brif
    (cheap fast path when phase is Idle)
  - After each `Call` / `CallIndirect` return — emit
    `check_safepoint` after the helper call's return value handling but
    before the next instruction emit
- **2 cross_thread integration tests** verifying:
  - JIT-mode worker parks at the next safepoint when GC requests a pause
  - Concurrent JIT worker + main-thread `Std.GC.Collect()` completes
    without deadlock or race

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/jit/helpers/control.rs` | MODIFY | 加 `#[no_mangle] pub unsafe extern "C" fn jit_check_safepoint(frame, ctx)` |
| `src/runtime/src/jit/helpers/registry.rs` | MODIFY | `HelperIds.check_safepoint: FuncId` 字段 + `register_symbols` reg!() 行 + `declare_imports` decl!() 行（签名 `[ptr, ptr], []`） |
| `src/runtime/src/jit/translate.rs` | MODIFY | 导入 `hr_check_safepoint`；在 4 个 site 插 `builder.ins().call(hr_check_safepoint, &[frame_val, ctx_val])`：(1) function entry 在 cl_blocks[0]; (2) Terminator::Br 当 target ≤ block_idx; (3) Terminator::BrCond 无条件; (4) `Instruction::Call` / `CallIndirect` 返回后 |
| `src/runtime/tests/cross_thread_smoke.rs` | MODIFY | 加 `jit_worker_parks_on_gc_request`（构造 JIT 模式 worker + collect_cycles 验证不死锁）+ `jit_concurrent_alloc_with_collect_no_deadlock`（4 workers JIT + 1 collector loop） |
| `docs/design/runtime/concurrency.md` | MODIFY | "Runtime foundation 现状" 表中 "并发 GC" 行的 "JIT-mode safepoint 待 add-gc-safepoint-jit" 删除；后续 spec list 标 ✅ |
| `docs/design/runtime/vm-architecture.md` | MODIFY | Safepoint 协议章节 "v0 范围"表 JIT 行从 "待 add-gc-safepoint-jit" 改为 ✅ 已落地 |
| `docs/spec/changes/add-gc-safepoint-jit/` | NEW | 本 spec |

**只读引用**：

- `src/runtime/src/gc/safepoint.rs` — check_safepoint 实现
- `src/runtime/src/jit/helpers/registry.rs` 既有 helper pattern
- `src/runtime/src/jit/translate.rs:870-895` Terminator dispatch（要插点）
- `src/runtime/src/jit/frame.rs::JitModuleCtx` —— vm_ctx 字段位置

## Out of Scope

- **Counter-based throttling**：`add-gc-safepoint-counter-throttling` 后续；
  JIT 每个 backward branch 都 check 是简单正确版本；hot loop 性能优化
  独立 spec
- **不抽象 interp 与 JIT 共用 safepoint 接口**：interp 直接调 `check_safepoint(ctx)`，
  JIT 走 `extern "C"` helper trampoline；两条路径都很短，不强行合并
- **AOT mode**：AOT 还没实现（roadmap M9）；当 AOT 落地时再开 `add-gc-safepoint-aot`

## Open Questions

- [ ] **BrCond 不区分 backward/forward**：Br terminator 可以静态检查 target ≤
      block_idx；BrCond 的两个 target 编译期已知但运行时分支选哪个未知。
      简单做法：BrCond 永远 check_safepoint（forward-only 路径吃一个 lock，
      但 lock is Idle fast path ~10ns）。Design Decision 1
- [ ] **Function entry 是否多余**：interp 在 `exec_function` 入口插 check，
      因为 worker 可能在新 frame 进入时还没 check 过；JIT 函数被 caller
      间接调，caller 在 Call 后会 check —— 但 caller 可能是另一个 JIT
      函数，要回溯到第一个 interp frame。简单做法：JIT 函数入口也 check
      一次。Design Decision 2