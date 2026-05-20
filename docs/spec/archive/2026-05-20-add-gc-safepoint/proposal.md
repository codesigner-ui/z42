# Proposal: GC safepoint protocol (interp-only v0)

## Why

After `add-multithreading-foundation` + `add-vmcontext-registry` +
`add-threading-stdlib` + `add-sync-primitives`, the runtime supports concurrent
mutators (z42 worker threads) but the GC mark phase has a **latent data race**
on per-thread roots:

- Worker thread A is mid-instruction, e.g. writing `regs[3] = Value::I64(42)` —
  internally a `Vec<Value>::operator[]` write
- Worker thread B's allocation triggers `collect_cycles()` → the GC scanner
  walks every VmContext in `vm_contexts`, follows the raw `frame.regs` /
  `frame.env_arena` pointers, and reads `regs[3]` mid-write
- Result: **data race** at the Rust memory-model level. UB. Has not crashed
  in current tests only because (a) GC fires only on allocation pressure
  thresholds that small tests don't hit and (b) the race window is narrow

The current architecture serialises *individual* heap operations via
`heap.lock()` and per-VmContext fields via `Arc<Mutex<_>>`, but the raw
pointers to `frame.regs` / `frame.env_arena` (added for stack-walking
performance — `add-unify-frame-chain`) bypass any lock.

Without a safepoint protocol, the multi-threaded runtime is correct only
when GC happens not to collide with a mutator write. This is a ticking
time bomb that will fire under realistic workloads.

## What Changes

- **VmCore safepoint state**: `gc_phase: Mutex<GcPhase>` (Idle / Requested /
  Marking) + `gc_phase_cv: Condvar` for park/resume + `parked_count: AtomicUsize`
- **Mutator-side safepoint check** in the interp dispatch loop, inserted at:
  - Function entry (so newly spawned workers immediately respect a pending GC)
  - Backward branches (so loops park promptly)
  - `Call` return (so long-running callees don't block GC)
- **GC-side request protocol**: `collect_cycles` flow becomes:
  1. Set `gc_phase = Requested`
  2. Wait on Condvar until `parked_count == mutator_count - 1` (all *other*
     VmContexts parked; the collecting thread itself counts as the GC driver)
  3. Set `gc_phase = Marking` (so newly parked threads understand the phase)
  4. Run mark + sweep
  5. Set `gc_phase = Idle`; notify_all → mutators resume
- **JIT path: gated out for v0** — interp is the default execution mode
  (rewrite-z42-test-runner R3b), JIT covers a smaller surface, and JIT
  safepoint insertion is a separate body of work. Spec'd as
  `add-gc-safepoint-jit` follow-up.
- **Targeted tests**: 2 Rust integration tests verifying (a) mutator parks
  on GC request + resumes after collect, (b) concurrent alloc + collect across
  4 threads produces no races + no missing roots (with `cargo miri test` /
  `RUSTFLAGS=-Zsanitizer=thread` follow-up if practical on macOS)

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/runtime/src/vm_context.rs` | MODIFY | VmCore 加 `gc_phase` / `gc_phase_cv` / `parked_count` 3 字段；构造路径初始化；VmContext drop 时清理 parked_count（如果当前已 parked） |
| `src/runtime/src/gc/safepoint.rs` | NEW | safepoint 协议核心：`GcPhase` enum + `check_safepoint(&VmContext)` + `request_gc_pause(&VmContext)` + `release_gc_pause(&VmContext)` |
| `src/runtime/src/gc/mod.rs` | MODIFY | re-export `safepoint::*`；在 `mod magr;` 同级加 `pub mod safepoint;` |
| `src/runtime/src/gc/arc_heap.rs` | MODIFY | `ArcMagrGC::collect_cycles` 包一层 stop-the-world wrapper（调用 `request_gc_pause` → 内部 mark+sweep → `release_gc_pause`） |
| `src/runtime/src/interp/exec_instr.rs` | MODIFY | dispatch 循环开始 + 每个 `Br` / `BrCond` 跳回向后地址前 + `Call` return 后插 `safepoint::check_safepoint(ctx)` |
| `src/runtime/src/interp/mod.rs` | MODIFY | `exec_function` 入口加 safepoint check（worker thread 起跑即遵从未决 GC 请求） |
| `src/runtime/src/gc/safepoint_tests.rs` | NEW | 单测：`gc_phase` 初始 Idle / request 后变 Requested / release 后变 Idle / parked_count 计数正确 |
| `src/runtime/tests/cross_thread_smoke.rs` | MODIFY | 加 `gc_collect_with_concurrent_mutators_no_race`：4 worker 反复 alloc + 1 GC 反复 collect_cycles，跑 N 轮无 race，无 missing roots |
| `docs/design/runtime/concurrency.md` | MODIFY | "Runtime foundation 现状" 表 "并发 GC" 行 ❌ → 🟡（safepoint 落地但 mark/sweep 仍单线程，待 add-concurrent-gc） |
| `docs/design/runtime/vm-architecture.md` | MODIFY | safepoint 协议章节描述 phase / parked_count 状态机 + interp 插入点 |
| `docs/spec/changes/add-gc-safepoint/` | NEW | 本 spec 自身 |

**只读引用**：

- `src/runtime/src/interp/exec_call.rs` — Call 返回路径参考
- `src/runtime/src/gc/refs.rs` — 现有 lock 模式
- `src/runtime/src/gc/arc_heap.rs` — 现有 collect_cycles 入口

## Out of Scope

- **JIT safepoint** — JIT 已编译代码内的 polling 插入点比 interp 复杂得多
  （需要 cranelift / inkwell 后端协作）。独立 spec `add-gc-safepoint-jit`
  覆盖；JIT-mode 用户暂时绕路或 fallback interp
- **优化型 safepoint** — 如 GCMode (mutator local roots) / TLAB（thread-local
  allocation buffer）等 v0 不引入
- **`add-concurrent-gc`** — mark/sweep 阶段本身的多线程化（safepoint 是它的
  前置）
- **safepoint 频率调优** — 当前 v0 在每个后向 branch + Call return 检查；
  生产用 polling 间隔 / counter-based 节流是后续 perf 工作
- **跨平台 preemptive safepoint** — signal-based (Linux SIGUSR1) / suspend-thread
  (Windows) 等非 portable 方案；本 spec 走 cooperative polling

## Open Questions

- [ ] **safepoint 检查粒度**：仅后向 branch + Call return 是否覆盖足够？
      forward-branch-heavy 代码（无循环、纯线性）会跑到函数结束都不 check —
      可接受（函数最终会 return，下一次 Call 触发 check）。Design Decision 1
- [ ] **collecting thread 是否也 park？** GC 触发线程自身 holding `heap.lock()`
      跑 mark — 它本身不需要 park（它就是 GC 驱动），但需要确保它不在 mark 期间
      改 own root。Design Decision 2
- [ ] **mutator_count 来源**：直接读 `vm_contexts.lock().len()`，还是单独维护
      `AtomicUsize`？前者每次都要拿锁，后者要维护一致性。Design Decision 3
