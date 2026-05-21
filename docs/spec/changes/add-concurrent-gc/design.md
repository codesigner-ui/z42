# Design: Concurrent Mark + Selectable GC Mode

## Architecture

```
                       ┌──────────────────┐
   ArcMagrGC::new() ──►│  mode: AtomicU8  │ ──┐
                       │  (StwMarkSweep   │   │
                       │   / Concurrent)  │   │
                       └──────────────────┘   │
                                              ▼
   collect_cycles ─────────────────► run_cycle_collection
                                              │
                              ┌───────────────┴───────────────┐
                              ▼                               ▼
                   STW path (unchanged)            Concurrent path (new)
                   - mark_phase BFS (STW)          - request_pause STW
                   - sweep_phase                   - snapshot roots
                                                   - release pause
                                                   - phase=ConcurrentMarking
                                                   - mark thread drains queue
                                                   - mutators push via barrier
                                                   - request_pause (handshake)
                                                   - phase=Marking; drain final
                                                   - phase=Sweeping
                                                   - sweep_phase
                                                   - release pause

   Write barrier dispatch (from interp / JIT FieldSet / ArraySet)
                              │
                              ▼
                       ArcMagrGC::write_barrier_field
                              │
                              ▼
                  match self.mode() {
                    StwMarkSweep   => no-op (+ cfg(test) observer),
                    ConcurrentMSP  => if mark cas-succeeds, push to
                                      mark_queue (+ cfg(test) observer),
                  }
```

## Decisions

### Decision 1: GcMode is a `Atomic` field on ArcMagrGC, not a const

**问题**: 模式存储位置 — 全局静态 const、`AtomicU8` field、还是 Mutex<GcMode>？

**选项**:
- A — Global static (set once at process start): 不灵活；测试切换不便
- B — `AtomicU8` field on ArcMagrGC: 每个 heap 实例独立 mode，set_mode 用 store/load Relaxed，运行时切换无锁
- C — `Mutex<GcMode>` field: 完全同步，开销略大但保证 set_mode/get_mode 原子

**决定**: B。`AtomicU8` 编码 `GcMode`，set_mode 是 `store(Relaxed)`，
读取在 `run_cycle_collection` / `write_barrier_*` 入口 `load(Acquire)`。
原因：mode 在 collect 进行中不应改变（已有 `collector_active` CAS 保护单
collector）；mutator barrier 路径读 mode 是 hot path，必须无锁。Mode 改
变后下次 collect 生效，符合 spec scenario "Mode switch is observable but
cannot interrupt a running collect"。

### Decision 2: Incremental update barrier (not SATB)

**问题**: 屏障算法选 incremental update（post-barrier, mark `new`）还是
SATB（pre-barrier, queue `old`）？

**选项**:
- A — Incremental update (Dijkstra): 简单，与已落地的 barrier 签名（只
  携 `new`，不携 `old`）天然契合
- B — SATB (Yuasa): mark phase 终止更快（不追新写），但需要 pre-barrier
  签名扩展（携 `old`）+ 改全部 5 个 call site

**决定**: A。已落地 barrier 签名只携 `new`；Decision 4 of
`add-write-barriers` 已埋伏笔："SATB-style impls 需要扩 trait 加
`write_barrier_field_pre`"。当前不必付那个代价。Incremental update 配
合 STW 终止 handshake 可保证 termination（短停顿 drain 残余 gray）。

**Tricolor 不变量**：
- White：未标记，可能是垃圾
- Gray：已标记，子节点尚未追踪
- Black：已标记，子节点已追踪
- **No black-to-white edge**：black 节点不能直接指向 white 节点；
  barrier 通过 shading new = mark(new) → gray 保证

### Decision 3: Mark queue is `Mutex<Vec<Value>>` (v1)

**问题**: Mark queue 数据结构 — `Mutex<Vec>`、`crossbeam::SegQueue`、
per-thread queue + steal、Channel？

**选项**:
- A — `Mutex<Vec>`: 简单，多 mutator + 单 marker 场景下锁竞争可接受
- B — `crossbeam::SegQueue`: 无锁 MPMC，更适合多 marker；引入 crossbeam 依赖
- C — Per-thread queue + work stealing: 最高吞吐；复杂度爆炸
- D — `std::sync::mpsc::channel`: SPMC，barrier 多 producer 不便

**决定**: A (v1)。z42 实际 mutator 线程数 1-2，竞争低；`parking_lot::Mutex`
未竞争 ~5ns，竞争下 µs 级 — 仍远低于 mark thread 单 BFS 步骤的 µs 级开销。
若 P5 bench 显示锁是瓶颈，独立 perf spec 升级到 SegQueue。

### Decision 4: 单 mark thread (v1)

**问题**: 后台 mark 用单线程还是 work-stealing 多线程？

**决定**: 单 thread (v1)。复杂度爆炸的来源是多线程，先验证算法
正确性。多线程是 P-future spec。

### Decision 5: GcPhase 扩展 `ConcurrentMarking` variant

**问题**: 是新增 phase 还是给 `Marking` 加 flag "is concurrent"？

**选项**:
- A — 新增 `ConcurrentMarking`：状态机显式，转换路径清楚
- B — `Marking` + `bool is_concurrent`: 节省 variant，但语义模糊
  （mutator parking 行为完全不同）

**决定**: A。`Marking` 保留 STW 语义（mutators 必须 park），
`ConcurrentMarking` 新 variant 表 mutators NOT parked。`request_gc_pause` /
`check_safepoint` 内部 match 分支处理（concurrent phase 下 mutator 不阻
塞 wait_while；只跑 barrier override）。

### Decision 6: STW 终止 handshake 而非 完全 concurrent termination

**问题**: Mark thread 怎么知道自己 done？

**选项**:
- A — 完全 concurrent termination (Yuasa-style): mark thread 监控 mutator
  写计数器，无写后 N 个 epoch 即认为完成。复杂，且有 livelock 风险
- B — STW handshake at end: mark thread 当 queue 空 → 请求 STW → 
  parking 后再 drain → 若仍空就 done，否则继续 concurrent。

**决定**: B。简单、可证明终止、且 v1 不需要追求极致 STW pause。STW
handshake 短（drain 一个小 burst，µs 级），仍远低于完全 STW mark 的耗时。

### Decision 7: Sweep 保持 STW

**问题**: sweep 也并发吗？

**决定**: 不。Sweep 涉及 `heap_registry` 修改 + drop 触发 finalizer，
并发 sweep 复杂度高、收益小（reachable-heavy workload sweep 已经很
快）。STW sweep 是经典权衡。后续单独 spec 评估 concurrent sweep ROI。

### Decision 8: 错误模式回退

**问题**: 如果 concurrent path 出 bug 怎么办？

**决定**: env var `Z42_GC_MODE=stw` 强制回退默认。这条规则写在
vm-architecture.md "GC mode selection" 段。Production 用户报问题第一
建议就是先 fallback STW 看是否复现。

## Implementation Notes

### GcMode enum

```rust
// in gc/mod.rs (or new gc/mode.rs)
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum GcMode {
    StwMarkSweep = 0,
    ConcurrentMarkSweep = 1,
}

impl GcMode {
    pub fn from_env() -> Self {
        match std::env::var("Z42_GC_MODE").as_deref() {
            Ok("concurrent") | Ok("concurrent-mark-sweep") => Self::ConcurrentMarkSweep,
            Ok("stw") | Ok("stw-mark-sweep") | Err(_) => Self::StwMarkSweep,
            Ok(other) => {
                eprintln!("z42: unrecognized Z42_GC_MODE={:?}, falling back to stw", other);
                Self::StwMarkSweep
            }
        }
    }
}
```

### ArcMagrGC field

```rust
pub struct ArcMagrGC {
    // ... existing fields ...
    mode: AtomicU8,                    // GcMode encoded as u8
    mark_queue: Mutex<Vec<Value>>,     // gray objects pending trace (concurrent path)
}
```

### Concurrent collect loop

```rust
fn run_cycle_collection_concurrent(&self) -> u64 {
    // 1. STW phase: snapshot roots, mark them gray
    //    (reuses existing GcPhase::Marking handshake — short, microseconds)
    {
        let _pause = request_gc_pause(...);  // existing API
        self.snapshot_roots_into_mark_queue();  // populate mark_queue
        // drop pause → all mutators resume
    }

    // 2. Transition to ConcurrentMarking phase
    self.set_gc_phase(GcPhase::ConcurrentMarking);

    // 3. Mark thread loop (current thread; we're the collector, mutators are running)
    loop {
        // Drain queue
        let mut local: Vec<Value> = std::mem::take(&mut *self.mark_queue.lock());
        if local.is_empty() {
            break;  // queue empty → request termination handshake
        }
        while let Some(v) = local.pop() {
            // Mark + trace
            if Self::mark_if_unmarked(&v) {
                v.trace_children(&mut |child| {
                    if Self::mark_if_unmarked(child) {
                        local.push(child.clone());
                    }
                });
            }
        }
        // local now empty; mark_queue may have new entries from barriers
    }

    // 4. Termination handshake (STW)
    let final_freed = {
        let _pause = request_gc_pause(...);
        // Phase transition: ConcurrentMarking → Marking → Sweeping
        // Drain any final-burst gray (mutators were parked just now;
        // they may have run barrier in the request_pause race window).
        self.set_gc_phase(GcPhase::Marking);
        let mut residual: Vec<Value> = std::mem::take(&mut *self.mark_queue.lock());
        while let Some(v) = residual.pop() {
            v.trace_children(&mut |child| {
                if Self::mark_if_unmarked(child) {
                    residual.push(child.clone());
                }
            });
        }

        // 5. STW sweep
        self.set_gc_phase(GcPhase::Sweeping);
        self.sweep_phase()
    };  // pause guard dropped → mutators resume

    final_freed
}

fn mark_if_unmarked(v: &Value) -> bool {
    match v {
        Value::Object(gc) => GcRef::mark(gc),
        Value::Array(gc)  => GcRef::mark(gc),
        Value::Closure { env, .. } => GcRef::mark(env),
        Value::Ref { kind: RefKind::Array { gc_ref, .. } | RefKind::Field { gc_ref, .. } } => {
            GcRef::mark(gc_ref)
        }
        _ => false,
    }
}
```

### Barrier override

```rust
#[allow(unused_variables)]
fn write_barrier_field(&self, owner: &Value, slot: usize, new: &Value) {
    #[cfg(test)]
    self.fire_barrier_field(owner, slot, new);

    match self.mode() {
        GcMode::StwMarkSweep => {}  // no-op
        GcMode::ConcurrentMarkSweep => {
            // Caller filter (Decision 1 of add-write-barriers) guarantees
            // new.is_heap_ref() — debug-assert as contract enforcement.
            debug_assert!(new.is_heap_ref());

            // Shade gray: mark if unmarked; if newly marked, enqueue
            // for tracing. Idempotent on already-marked.
            if Self::mark_if_unmarked(new) {
                self.mark_queue.lock().push(new.clone());
            }
        }
    }
}
```

### Snapshot roots STW phase

```rust
fn snapshot_roots_into_mark_queue(&self) {
    let mut queue = self.mark_queue.lock();
    queue.clear();
    // Pinned roots
    for v in self.inner.lock().roots.values() {
        if Self::mark_if_unmarked(v) {
            queue.push(v.clone());
        }
    }
    // External root scanner
    if let Some(scan) = self.external_root_scanner.lock().as_ref() {
        scan(&mut |v| {
            if Self::mark_if_unmarked(v) {
                queue.push(v.clone());
            }
        });
    }
}
```

## Testing Strategy

### Unit tests (P5)

- `mode_default_is_stw_mark_sweep`
- `set_mode_changes_observable_mode`
- `env_var_z42_gc_mode_concurrent_selects_concurrent`
- `barrier_no_op_in_stw_mode_preserves_stats`
- `barrier_shades_new_value_in_concurrent_mode`
- `concurrent_collect_preserves_reachable_chain` (single-thread sim,
  no real concurrency yet — runs barrier override path inline)
- `concurrent_collect_frees_unreachable_cycle`
- `mode_switch_during_collect_takes_effect_next_collect`

### Multi-threaded stress (P5)

- `cross_thread_smoke_with_concurrent_gc`: existing
  `cross_thread_smoke` tests re-run with `Z42_GC_MODE=concurrent`;
  must remain GREEN.

### End-to-end

- `test-all.sh --scope=full`: both modes GREEN
  - First run: default (STW) — verify zero behavior change
  - Second run: `Z42_GC_MODE=concurrent ./scripts/test-all.sh --scope=full` —
    verify all tests still pass under concurrent path

### Bench (P6)

- `gc_cycle_bench`: extend with concurrent-mode variants of the 3
  workloads. Compare:
  - STW pause time (current bench)
  - Concurrent STW-handshake time (new)
  - Concurrent total mark time (background; not blocking mutators)
  - Mutator throughput during concurrent mark vs. baseline

## Phasing

Implementation in 7 phases, each independently committable + GREEN:

- **P0**: `GcMode` enum + `mode` field + `set_mode/mode` API + env var
  parsing + dispatch stub (`run_cycle_collection` matches mode, both
  arms go to STW path). Tests: `mode_default_is_stw_mark_sweep`,
  `set_mode_changes_observable_mode`, `env_var_z42_gc_mode_concurrent`.
- **P1**: `GcPhase` enum extension + safepoint protocol awareness
  (no behavior change yet — `ConcurrentMarking` exists but isn't used).
- **P2**: `mark_queue: Mutex<Vec<Value>>` field + `snapshot_roots_into_mark_queue`
  + `mark_if_unmarked` helper. Tests verify queue manipulation directly.
- **P3**: Barrier override branches on mode; concurrent mode shades + enqueues.
  Tests verify barrier dispatches in concurrent mode, no-op in STW mode.
- **P4**: Concurrent mark loop (`run_cycle_collection_concurrent`).
  Tests verify reachable chains preserved + unreachable cycles freed
  under concurrent path.
- **P5**: Cross-thread stress: existing `cross_thread_smoke` test with
  env var set + new unit tests for race scenarios.
- **P6**: Bench + report (extend `gc_cycle_bench.rs`).
- **P7**: Docs sync (`vm-architecture.md` "GC mode selection" + A4 → landed) + archive.

P0–P3 are mostly mechanical (no concurrency logic yet — switch + dispatch
+ wire up). P4 is the bulk of the new logic. P5–P7 are verification +
docs.

## Deferred / Future Work

### `add-concurrent-sweep`
- Sweep stays STW in this spec. Future spec evaluates ROI of concurrent
  sweep (mostly relevant for very large old generations under generational).

### `add-satb-barrier`
- Alternative to incremental update. Pre-barrier with old value. Trade-off:
  faster mark termination, more cost per write. Open if profiling shows
  incremental update's termination handshake is the bottleneck.

### `add-work-stealing-mark`
- v1 uses single mark thread. Multi-thread with work stealing is a
  perf spec once single-thread mark is the bottleneck on big heaps.

### `add-lockfree-mark-queue`
- v1 uses `Mutex<Vec>`. Lock-free upgrade (crossbeam SegQueue or
  similar) if profiling shows mutex contention is hot.

### `add-concurrent-root-scanning`
- v1 STW root scan. Concurrent root scan only matters for very large
  stacks; deferred until measured pain.
