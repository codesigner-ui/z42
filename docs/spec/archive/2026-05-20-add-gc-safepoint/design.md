# Design: GC safepoint protocol (interp-only v0)

## Architecture

```
当前（after add-sync-primitives）：
  collect_cycles 由当前 mutator thread inline 调用，
  其他 mutator 同时在跑 → 跨 thread 读 frame.regs 是 data race。

本 spec 后（cooperative polling）：
  VmCore {
    ..., heap, vm_contexts,
    gc_phase:        Mutex<GcPhase>,     ← NEW Idle / Requested / Marking
    gc_phase_cv:     Condvar,            ← NEW 通知 phase 变更
    parked_count:    AtomicUsize,        ← NEW 当前 parked 的 mutator 数（不含 collector）
  }

  enum GcPhase { Idle, Requested, Marking }

  // Mutator 侧：interp dispatch loop 散点插
  fn check_safepoint(ctx: &VmContext) {
      let phase = *ctx.core.gc_phase.lock();
      if matches!(phase, GcPhase::Requested | GcPhase::Marking) {
          park_until_idle(ctx);
      }
  }

  fn park_until_idle(ctx: &VmContext) {
      ctx.core.parked_count.fetch_add(1, AcqRel);
      ctx.core.gc_phase_cv.notify_all();  // 唤醒等 parked_count 达标的 collector
      let mut phase = ctx.core.gc_phase.lock();
      while !matches!(*phase, GcPhase::Idle) {
          ctx.core.gc_phase_cv.wait(&mut phase);
      }
      ctx.core.parked_count.fetch_sub(1, AcqRel);
  }

  // Collector 侧：包 mark + sweep
  fn collect_cycles_safepointed(ctx: &VmContext, ...) {
      // 1. Request pause
      *ctx.core.gc_phase.lock() = GcPhase::Requested;
      // 2. Wait for everyone else to park
      let mutator_count = ctx.core.vm_contexts.lock().len();
      let need_parked = mutator_count.saturating_sub(1);  // 不算 collector 自己
      let mut phase = ctx.core.gc_phase.lock();
      while ctx.core.parked_count.load(Acquire) < need_parked {
          ctx.core.gc_phase_cv.wait(&mut phase);
      }
      // 3. Marking
      *phase = GcPhase::Marking;
      drop(phase);
      // ... actual mark + sweep ...
      // 4. Release
      *ctx.core.gc_phase.lock() = GcPhase::Idle;
      ctx.core.gc_phase_cv.notify_all();
  }
```

## Decisions

### Decision 1: safepoint 检查粒度

**问题**：interp dispatch loop 在哪些点 check safepoint？

**选项**：
- **A** 每条指令前 check —— 最严密，但 ~5-10% interp overhead
- **B** 仅 backward branch + Call return + function entry —— 标准 HotSpot 风格；
  forward-only 线性代码段不 check，但函数最终 return → 下个 Call 触发
- **C** Counter-based throttling（每 N 条指令 check 一次）—— 引入额外计数器

**决定**：**B**。理由：
- z42 实际代码绝大多数循环都有 backward branch
- 真正纯线性长函数极少（Phase 1 stdlib 无超 100 行的 hot function）
- v0 不追求极致 latency；GC 触发到完成几十 ms 量级用户可接受
- C 引入额外热路径开销（计数器递增 + 比较），不如 B 简单

Insertion points 详见 §Implementation Notes。

### Decision 2: collecting thread 是否 park 自己？

**问题**：触发 `collect_cycles` 的 thread 本身在跑 GC 代码 —— 它需要 park
吗？

**决定**：**不 park**。理由：
- collector thread 本身就是 GC 驱动，要执行 mark + sweep 代码
- 它在调 collect 前 hold `heap.lock()`（即 `Box<dyn MagrGC>` 内部的锁路径），
  其他 mutator 想 alloc 会卡在那
- 它的 own roots（pending_exception / call_stack / func_ref_slots）由 collector
  自己读取 + 自己负责不在 marking 期间修改
- `parked_count` 目标是 `mutator_count - 1` 而不是 `mutator_count`

边界 case：collect 时若 `vm_contexts` 只有 collector 自己 → `need_parked = 0` →
立即进入 Marking 阶段。

### Decision 3: `mutator_count` 来源

**问题**：collector 等待的 `need_parked` 阈值怎么算？

**选项**：
- **A** 每次 collect 一开始 `vm_contexts.lock().len()` snapshot
- **B** 单独维护 `mutator_count: AtomicUsize`，在 VmContext::new / drop 同步增减
- **C** 直接读 vm_contexts.len()（无锁、UB）

**决定**：**A**。理由：
- VmContext 注册 / drop 不在 hot path（每个 thread 各一次 ctx 生命周期）
- 拿锁成本一次 < 1 µs，相比 mark+sweep 几 ms 微不足道
- 简单，无需双数据源一致性维护
- 选 A 时还要注意：新 VmContext 在 collector 等待期间注册了怎么办？
  若新 ctx 立即 check_safepoint 看到 Requested 则会 park（贡献 parked_count）
  + 触发 notify_all 唤醒 collector 重新评估 — 可能死锁等条件未达成。
  解决：collector 等待循环每次重新读 `vm_contexts.lock().len()`。详 Implementation Notes。

### Decision 4: GcPhase / Mutex / Condvar 选用 std vs parking_lot

**问题**：用 `std::sync::Mutex<GcPhase> + std::sync::Condvar`，还是
`parking_lot::Mutex<GcPhase> + parking_lot::Condvar`？

**决定**：**parking_lot**。与项目其它跨线程同步一致（add-multithreading-foundation
Phase 3 标准），且 parking_lot Condvar API 更简洁（无 poison handling 噪音）。

### Decision 5: JIT 暂不实施 + 退路策略

**问题**：JIT mode 下 mutator 已编译为 native 代码，无法在 Rust 层插 check。

**决定**：v0 不实施。具体走向：
- JIT mode 当前作为 opt-in，默认 interp（rewrite-z42-test-runner R3b）
- 多线程 + JIT 同时使用时，scanner 仍读 JIT frame 的 raw ptrs → race。
  本 spec 不解决，仅 interp 路径安全
- 文档明记：multi-thread workloads 推荐用 interp；JIT + threading 是
  `add-gc-safepoint-jit` 未来 spec 范围
- collect_cycles 入口加 debug_assert / 日志：若发现有 JIT-mode VmContext 在
  vm_contexts 注册表中且未 park，警告（v0 不强制 abort）

### Decision 6 (amended at implementation time): MagrGC trait signature unchanged

**问题**：原 Decision 3.2 提议 `MagrGC::collect_cycles(&self, ctx: &VmContext)` 改签名，让所有 callsite 知道 ctx；但 `arc_heap.rs::maybe_auto_collect` 在内部 alloc 路径触发（无 ctx），扩展签名要级联改 alloc_* 全部 ~10 个方法 + 数十处 GC 单测 fixture。

**决定**：**不改 trait 签名**。改用 **Option A**：
- script-explicit `__gc_collect` / `__gc_force_collect`（已有 `&VmContext` 入参）在 corelib/gc.rs 包 `request_gc_pause` RAII guard
- auto-threshold 内部路径（`maybe_auto_collect`）**仍走 unguarded** `self.collect_cycles()` —— v0 限制：高分配压力 + 跨线程并发写 regs 同时发生时仍有 race window
- 文档明记：多线程 workloads 推荐用 `Std.GC.Collect()` 显式触发；或将 `max_bytes` 配足够大避免 auto-trip

未来 spec `add-gc-safepoint-auto-threshold` 可通过给 `ArcMagrGC` 加 `Weak<VmCore>` backref + 在 safepoint check 内 drain pending-collect 标志的方式补全；当前 v0 不阻塞主线推进。

### Decision 7: 是否引入 `gc_disabled` flag for ffi callback re-entrant 路径？

**问题**：z42 调用 native fn（CallNative）期间，native fn 可能回调 z42 代码
触发 alloc → collect → safepoint。但 native fn 持有 Rust 资源（如 mpsc::Sender
或别的 Mutex guards），可能死锁。

**决定**：v0 接受这一限制。Native fn 内部触发 GC 是边缘 case；标准 native interop
约定不应在 native 代码中持 lock 同时调 alloc。如有需求，后续 `add-safe-ffi-zone`
spec 显式标 unsafe region 解决。

## Implementation Notes

### Interp safepoint insertion points

`exec_function` 入口（每次进入 z42 函数都 check）：

```rust
pub(crate) fn exec_function(ctx: &VmContext, module: &Module, func: &Function, args: &[Value]) -> Result<ExecOutcome> {
    crate::gc::safepoint::check_safepoint(ctx);   // NEW
    let mut frame = Frame::new(args, func.max_reg);
    // ... rest unchanged ...
}
```

`exec_instr` 的 `Br` / `BrCond` terminator handlers 在跳目标地址 *小于等于*
当前 block index 时 check（backward branch heuristic — block index 倒退 =
循环回边）：

```rust
Terminator::Br { label } => {
    let target = func.block_index[label];
    if target <= current_block_idx {
        crate::gc::safepoint::check_safepoint(ctx);  // backward = loop back-edge
    }
    // ... rest unchanged ...
}
Terminator::BrCond { cond, true_label, false_label } => {
    let target = if cond_val { true_label } else { false_label };
    let target_idx = func.block_index[target];
    if target_idx <= current_block_idx {
        crate::gc::safepoint::check_safepoint(ctx);
    }
    // ...
}
```

`Call` 指令返回后（callee 完成后；不需在 callee 入口重复 check —— `exec_function`
入口已 check）：

```rust
Instruction::Call { .. } => {
    let result = call::call(...)?;
    crate::gc::safepoint::check_safepoint(ctx);  // NEW — 回到本帧后 check
    // ... handle result ...
}
```

### `gc/safepoint.rs` 模块结构

```rust
use crate::vm_context::VmContext;
use parking_lot::{Condvar, Mutex};
use std::sync::atomic::{AtomicUsize, Ordering};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GcPhase {
    Idle,
    Requested,
    Marking,
}

/// Cheap fast path called from every safepoint insertion point.
/// Branches off to `park_until_idle` only when GC requested pause.
#[inline]
pub fn check_safepoint(ctx: &VmContext) {
    // Acquire lock briefly; common path sees Idle and returns.
    let phase = *ctx.core.gc_phase.lock();
    if matches!(phase, GcPhase::Requested | GcPhase::Marking) {
        park_until_idle(ctx);
    }
}

fn park_until_idle(ctx: &VmContext) {
    ctx.core.parked_count.fetch_add(1, Ordering::AcqRel);
    ctx.core.gc_phase_cv.notify_all();  // wake collector that's polling parked count
    let mut phase = ctx.core.gc_phase.lock();
    while !matches!(*phase, GcPhase::Idle) {
        ctx.core.gc_phase_cv.wait(&mut phase);
    }
    ctx.core.parked_count.fetch_sub(1, Ordering::AcqRel);
}

/// Called from `collect_cycles` wrapper. Blocks until all *other* VmContexts
/// have parked. Returns a guard whose Drop releases the pause.
pub struct GcPauseGuard<'a> {
    ctx: &'a VmContext,
}

pub fn request_gc_pause(ctx: &VmContext) -> GcPauseGuard<'_> {
    {
        let mut phase = ctx.core.gc_phase.lock();
        *phase = GcPhase::Requested;
    }
    // Wait for all other VmContexts to park.
    let mut phase = ctx.core.gc_phase.lock();
    loop {
        let total = ctx.core.vm_contexts.lock().len();
        let need = total.saturating_sub(1);  // exclude self
        if ctx.core.parked_count.load(Ordering::Acquire) >= need {
            break;
        }
        ctx.core.gc_phase_cv.wait(&mut phase);
    }
    *phase = GcPhase::Marking;
    drop(phase);
    GcPauseGuard { ctx }
}

impl Drop for GcPauseGuard<'_> {
    fn drop(&mut self) {
        *self.ctx.core.gc_phase.lock() = GcPhase::Idle;
        self.ctx.core.gc_phase_cv.notify_all();
    }
}
```

### `ArcMagrGC::collect_cycles` 接入

```rust
// In gc/arc_heap.rs, wherever collect_cycles is called from script-driven
// paths (alloc threshold trip / __gc_force_collect builtin):

fn collect_cycles_safepointed(&self, ctx: &VmContext) {
    let _pause = crate::gc::safepoint::request_gc_pause(ctx);
    // existing mark + sweep impl runs here while all mutators parked
    self.collect_cycles_impl(...);
    // _pause drop releases pause + notifies
}
```

### Edge case: no other mutators

`request_gc_pause` 在 `need == 0` 时立即进 Marking phase。退化为今天的串行行为。

### Edge case: VmContext::new during collect

新 ctx 注册 → `vm_contexts.lock().len()` 增 1 → collector 下一轮等待循环检测到。
新 ctx 第一次 check_safepoint（function entry 触发）→ 看到 Requested/Marking →
park。OK。

### Edge case: VmContext::drop during pause

drop 时若 ctx 已 parked（parked_count 包含它）则 parked_count 应 -1。但 drop
不直接调 safepoint。简单做法：drop 时只清理 vm_contexts 注册表，让 collector
重新读 len 计算 need —— 自动一致。drop 必须在 ctx 未持 phase lock 时调（不可
能 deadlock —— drop 只 lock vm_contexts，与 phase 路径正交）。

## Testing Strategy

- **Rust unit tests** (`gc/safepoint_tests.rs`)：
  - `gc_phase_idle_by_default`
  - `request_pause_sets_phase`
  - `release_pause_resets_phase_and_notifies`
  - `parked_count_increments_on_park` (通过手工 spawn + park 模拟)
- **Cross-thread integration** (`runtime/tests/cross_thread_smoke.rs`)：
  - `gc_collect_with_concurrent_mutators_no_race` — 4 workers loop alloc +
    1 GC driver loop collect_cycles 100 轮 + final stats consistency check
- **现有测试不回归**：alloc / collect / GcRef borrow 路径触及不到 safepoint
  fast path（Idle 短路）—— 关键是 `check_safepoint` 在 hot path 上的开销
- **GREEN gate**：stdlib 69/69 + cargo test 全过；test-all.sh 全 GREEN

## Deferred / Future Work

### `add-gc-safepoint-jit`
- **来源**：本 spec Decision 5
- **触发原因**：JIT native code 不能由 Rust 编译期插 check
- **前置依赖**：本 spec + JIT backend safepoint polling 设计（cranelift /
  inkwell 端各自的 patch point 机制）

### `add-gc-safepoint-counter-throttling`
- **来源**：本 spec Decision 1
- **触发原因**：v0 在 backward branch 每次都 check 有少量 overhead；生产可
  counter-based 节流（每 1024 backward jumps 才 check 一次）
- **触发条件**：profiling 显示 safepoint check 占 hot path > 2%

### `add-safe-ffi-zone`
- **来源**：本 spec Decision 6
- **触发原因**：native fn 内 z42 callback alloc 触发 GC 时持锁 risk
- **触发条件**：用户报死锁，或大型 native interop 工作量
