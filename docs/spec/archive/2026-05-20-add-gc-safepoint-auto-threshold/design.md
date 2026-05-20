# Design: Safepoint-aware auto-threshold GC trigger

## Architecture

```
当前（after add-gc-safepoint）：
  ctx.heap().alloc_array(...) 内部
    → record_alloc → check_pressure → maybe_auto_collect
      → if pressure tripped: self.collect_cycles()   ❌ no ctx, no safepoint

本 spec 后：
  ctx.heap().alloc_array(...) 内部
    → record_alloc → check_pressure → maybe_auto_collect
      → if pressure tripped:
          if let Some(flag) = self.external_needs_collect.lock().clone() {
              flag.store(true, Release);
          }
        // else (no flag installed): fall back to inline collect — preserves
        // existing single-VmContext-no-threading behavior

  VmCore {
      ..., needs_auto_collect: Arc<AtomicBool>,
  }

  check_safepoint(ctx) {
      let phase = *ctx.core.gc_phase.lock();
      if !Idle { park_until_idle(ctx); return; }
      // NEW: drain auto-collect request
      if ctx.core.needs_auto_collect.swap(false, AcqRel) {
          let _g = request_gc_pause(ctx);
          ctx.heap().collect_cycles();
      }
  }
```

## Decisions

### Decision 1: Flag-set 后到 drain 之间的窗口

**问题**：`maybe_auto_collect` 设置 flag 后，可能很长一段时间没有 mutator 调
`check_safepoint`（线性长函数无 backward branch）。期间 heap 持续超阈值。怎么办？

**决定**：v0 接受。理由：
- 长线性函数最终 return → 父帧的下一个 Call 后 safepoint check → drain
- 实际工作流中 100 ms 数量级延迟无碍
- 极端 corner case 可通过 `Std.GC.Collect()` 显式触发 short-circuit

### Decision 2: 并发 alloc 多个线程同时 set flag

**问题**：N 个 worker 同时跑 alloc 都 trip pressure threshold → 各自 store
true 到 flag。下一次任一线程 safepoint check 把 flag swap 为 false 后 run
collect。其他线程的 trip 信号"丢失"？

**决定**：不丢失，**自然合并**。所有 trip 都 set true（idempotent）；first-checker
drains + collects；collect 完成后 `last_auto_collect_used` 被更新，
maybe_reset_near_limit_warned 重置标志位，下次 pressure 再 trip 重设 flag。
不需要额外的 generation counter。

### Decision 3: 单 VmContext (single-thread) 时仍走 flag 路径吗？

**问题**：单线程下走 flag→drain 是否引入不必要延迟（原来是同步 collect）？

**决定**：**统一走 flag 路径**。理由：
- 单/多线程统一代码路径减少分支
- 单线程下：alloc → set flag → return → 调用方在 backward branch / Call return
  立即 check safepoint → drain → collect。延迟仅几 instructions
- 单线程下 `request_gc_pause` 检测 `vm_contexts.len() - 1 == 0` 立即进 Marking phase（add-gc-safepoint Decision 2）—— 零额外阻塞
- 简化测试矩阵 + 减少回归风险

### Decision 4: MagrGC trait method 默认实现

**问题**：trait 加 `set_external_needs_collect_flag` 是否所有 backend 都需要实现？

**决定**：**默认 no-op**。理由：
- ArcMagrGC 是当前唯一实际 backend（rcheap 已合并）
- 其他未来 backend 若不观察 alloc threshold（如某种 MMTk-style mark-sweep）
  不需要这个机制
- 默认 `{}` 保持向后兼容（host embed 用户自定义 backend 不被破坏）

### Decision 5: ArcMagrGC 内部存 `Option<Arc<AtomicBool>>` 而非 `Arc<AtomicBool>`

**问题**：ArcMagrGC 现有构造路径（`ArcMagrGC::new()`）不能从 VmCore 收 flag
（启动顺序：先构 heap，再构 VmCore 把 heap 装进去）。需要"先构，后 wire"。

**决定**：内部用 `Mutex<Option<Arc<AtomicBool>>>`，构造时 None；VmCore 构造
后立即调 `heap.set_external_needs_collect_flag(...)` 填充。Mutex 因为
`set_external_needs_collect_flag` 通过 `&self`（trait 方法不能 `&mut self`）。

如果 flag 没装（None），`maybe_auto_collect` fallback 到当前 inline `self.collect_cycles()`
路径 —— 保留所有 GC 单测的现有行为（它们直接 `ArcMagrGC::new()`，不走
VmCore 构造）。

## Implementation Notes

### 1. trait 加方法

```rust
// src/runtime/src/gc/heap.rs

use std::sync::Arc;
use std::sync::atomic::AtomicBool;

pub trait MagrGC: Debug + Send + Sync {
    // ... existing methods ...

    /// add-gc-safepoint-auto-threshold (2026-05-20):
    /// Wire an external AtomicBool flag the backend should set (instead of
    /// calling collect_cycles inline) when an automatic threshold-based
    /// collection is warranted. The flag is drained by the next
    /// `check_safepoint(ctx)` call, which performs a safepointed collect.
    ///
    /// Default no-op: backends that don't observe alloc thresholds or
    /// don't need cross-thread safety opt out.
    fn set_external_needs_collect_flag(&self, _flag: Arc<AtomicBool>) {}
}
```

### 2. ArcMagrGC 实现

```rust
// src/runtime/src/gc/arc_heap.rs

pub struct ArcMagrGC {
    inner: parking_lot::Mutex<ArcHeapInner>,
    external_root_scanner: parking_lot::Mutex<Option<Box<...>>>,
    external_needs_collect: parking_lot::Mutex<Option<Arc<std::sync::atomic::AtomicBool>>>,  // NEW
}

impl ArcMagrGC {
    fn maybe_auto_collect(&self) {
        // ... existing pressure check ...
        if !pressure_tripped { return; }

        // NEW: defer to safepoint when wired
        if let Some(flag) = self.external_needs_collect.lock().clone() {
            flag.store(true, Ordering::Release);
            return;
        }

        // Fallback: original inline collect (preserves GC unit-test behaviour
        // and single-threaded host without VmCore wiring).
        self.inner.lock().last_auto_collect_used = used;
        self.collect_cycles();
    }
}

impl MagrGC for ArcMagrGC {
    fn set_external_needs_collect_flag(&self, flag: Arc<AtomicBool>) {
        *self.external_needs_collect.lock() = Some(flag);
    }
    // ... existing methods ...
}
```

### 3. VmCore 构造后的 wiring

```rust
// src/runtime/src/vm_context.rs

// In VmContext::new_internal, after Arc::new(VmCore { ... }):
let core: Arc<VmCore> = Arc::new(VmCore {
    // ... all existing fields ...
    needs_auto_collect: Arc::new(std::sync::atomic::AtomicBool::new(false)),
    // ... rest ...
});

// NEW: wire the flag into the heap so maybe_auto_collect can flip it
core.heap.set_external_needs_collect_flag(Arc::clone(&core.needs_auto_collect));

// Existing external_root_scanner wiring follows here
```

Note: in `VmContext::new_with_core`, the flag is already wired from the
original VmCore creation — nothing to do.

### 4. safepoint check 扩展

```rust
// src/runtime/src/gc/safepoint.rs

pub fn check_safepoint(ctx: &VmContext) {
    let phase = *ctx.core.gc_phase.lock();
    if matches!(phase, GcPhase::Requested | GcPhase::Marking) {
        park_until_idle(ctx);
        return;  // park unwound — don't drain flag mid-collect
    }
    // Idle phase — try to drain auto-collect request.
    if ctx.core.needs_auto_collect.swap(false, Ordering::AcqRel) {
        let _pause = request_gc_pause(ctx);
        ctx.heap().collect_cycles();
        // _pause Drop releases the world.
    }
}
```

The `swap(false, AcqRel)` atomic primitive serves as the claim mechanism —
exactly one thread sees `true` per round; others see `false` and skip. If
multiple threads all hit pressure simultaneously, only the first to swap
runs the collect (rest see false, return immediately — they may still trip
pressure on their next alloc, in which case they re-set the flag).

### 5. ArcMagrGC 单测 fallback 路径仍 work

GC 单测大量直接 `ArcMagrGC::new()`（不走 VmCore）→ `set_external_needs_collect_flag`
从未被调 → `external_needs_collect.lock()` is None → `maybe_auto_collect`
fallback 到原 `self.collect_cycles()`。所有 ~40 个 GC 单测无回归。

## Testing Strategy

- **Unit**: `gc/safepoint_tests.rs::auto_collect_flag_drained_at_next_safepoint`
  — 手工置 `core.needs_auto_collect = true`，调 `check_safepoint(ctx)`，验证
  flag drained + gc_cycles 增加
- **Integration**: `runtime/tests/cross_thread_smoke.rs::auto_collect_triggers_via_safepoint_no_race`
  — 4 workers loop alloc + check_safepoint；设小 `max_bytes`（如 4096）
  → 自然 trip auto-threshold 数十次；assert 完成 + gc_cycles > 0
- **回归**：现有 GC 单测（直接构 ArcMagrGC）走 fallback；现有 stdlib threading
  测试走 flag 路径但单 worker / 大堆，不触发 threshold

## Deferred / Future Work

### `add-gc-safepoint-counter-throttling`
- 既存 deferred（继承自 add-gc-safepoint）；本 spec 不改

### `add-gc-safepoint-jit`
- 既存 deferred；本 spec 不改
