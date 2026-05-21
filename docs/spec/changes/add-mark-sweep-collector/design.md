# Design: Mark-sweep GC

## Architecture

```
当前（trial-deletion in arc_heap.rs::run_cycle_collection）：
  collect_cycles() {
      reachable = mark_reachable_set()       // BFS from roots → HashSet<*ptr>
      alive     = snapshot_live_from_registry()
      unreachable = alive ∖ reachable
      // Trial deletion: figure out tentative refcount excluding self
      for v in unreachable:
          tentative[v] = strong_count(v) - 1
          for child in scan_object_refs(v):
              if child in unreachable: tentative[child] -= 1
      // Break cycles where tentative=0 (purely internal references)
      for v in unreachable where tentative[v]==0:
          break_cycle_value(v)
      // alive_vec drops here, Arc Drop cascades
  }

本 spec 后（standard mark-sweep）：
  collect_cycles() {
      // Phase 1: Mark from roots
      let mut work = Vec::new();
      for_each_root(|v| if mark_if_unmarked(v) { work.push(v) });
      while let Some(v) = work.pop() {
          scan_object_refs(v, |child| {
              if mark_if_unmarked(child) { work.push(child); }
          });
      }

      // Phase 2: Sweep + reset
      let mut freed_bytes = 0;
      registry.retain(|arc| {
          if arc.marked.load(Acquire) == 0 {
              freed_bytes += arc.size_bytes();
              false   // remove from registry → Arc drop → finalizer + Drop chain
          } else {
              arc.marked.store(0, Release);   // reset for next cycle
              true
          }
      });
      stats.freed_bytes += freed_bytes;
      stats.gc_cycles += 1;
  }
```

## Decisions

### Decision 1: Mark bit lives inside `GcAllocation`

**问题**：mark bit 放对象内 (`GcAllocation.marked: AtomicU8`) 还是单独
`HashMap<*const, bool>`？

**选项**：
- A. `GcAllocation.marked: AtomicU8` — cache-friendly (read mark while
  walking pointer)，对象增 1 byte
- B. 单独 `marked_set: HashSet<*const>` — registry 外挂数据；无对象 size
  影响；但每次 mark/check 是 HashMap lookup（~10× slower than AtomicU8）

**决定**：**A**. mark 是 BFS 内循环最热路径；HashMap lookup 在 mark phase
执行 N×B 次（N=对象数, B=平均出度），cache-friendly 设计更重要。1 byte 增长
忽略不计（Arc + Mutex + 业务数据已 32-64 bytes/对象）。

### Decision 2: Tracing via inherent method, not Trace trait

**问题**：定义 `trait Trace { fn trace_children(&self, &mut dyn FnMut(&Value)); }`
还是给 Value enum 加 inherent `fn trace_children(&self, &mut F)`？

**决定**：**inherent method on Value**. 理由：
- Value enum 是 closed set（所有 variants 在 metadata/types.rs），不需要
  open dispatch
- inherent method 简单，无 dyn Trace fat pointer overhead
- trait 风格在 Rust GC crate (gc, broom) 中常见因为他们 user 定义 type；
  z42 GC 只追踪 Value，不需要可扩展

实现：把 ArcMagrGC 的 `scan_object_refs` 移到 `Value::trace_children`：

```rust
impl Value {
    pub fn trace_children(&self, visit: &mut impl FnMut(&Value)) {
        match self {
            Value::Array(gc)  => for v in gc.borrow().iter() { visit(v); },
            Value::Object(gc) => for v in gc.borrow().slots.iter() { visit(v); },
            Value::Closure { env, .. } => for v in env.borrow().iter() { visit(v); },
            _ => {}  // primitives, no children
        }
    }
}
```

### Decision 3: STW (stop-the-world) preserved; concurrent GC is A4

**问题**：要不要在本 spec 顺手做 incremental / concurrent mark？

**决定**：**仍 STW**. 理由：
- 已有 add-gc-safepoint 协议处理 stop-the-world 正确性
- A4 (concurrent mark) 需要 write barriers + tri-color invariant，独立工程
- A2 只换算法不动并发模型，bench 比较 trial-deletion vs mark-sweep
  纯算法优劣
- 后续 A4 spec 可以直接拿 A2 的 mark phase 改成 concurrent + write barrier

### Decision 4: Arc drop 路径保留即时释放

**问题**：mark-sweep 期间，user 代码 last-release-Arc 是 immediate drop
还是 deferred 到 sweep？

**决定**：**保留即时 drop**. 行为：
- Arc 自然 Drop 走原 `GcAllocation::Drop` —— 触发 finalizer +
  写 stats.freed_bytes
- 这是 non-cycle 路径（user 显式释放最后一个 Arc）
- cycle 路径（user 不释放但对象只被 cycle 内其他对象引用）：靠 sweep
  阶段从 registry 移除 → Arc drop → finalizer

trial-deletion 现行为：同 immediate drop 思路（trial-deletion 只
break 环让 Arc chain cascade）。**user-visible behavior 一致**。

### Decision 5: gc_cycles 计数语义不变

**问题**：mark-sweep 是否每次都 increment gc_cycles？

**决定**：**每次都 increment**（与 trial-deletion 一致）。无 reachable
对象时仍计为一次 cycle（mark phase 空跑 + sweep 空操作）。

### Decision 6: Tests fixture migration approach

**问题**：~40 个 arc_heap_tests/ 单测 fixture 是基于 trial-deletion 行为
写的，mark-sweep 后部分 assertion 失效。如何迁移？

**决定**：实施期分阶段：
1. 先跑全套既有测试 baseline （捕捉哪些 assertion fail）
2. 对每个 fail，分析是 (a) mark-sweep 必然变化 (b) 隐含 bug 还是 (c) 应该
   失败的（说明 mark-sweep 漏了 reachable case）
3. (a) 类：调整 assertion 数字
4. (b) 类：修代码
5. (c) 类：修 mark phase 逻辑

预估 ~10-15 个 fixture 需要调整。

## Implementation Notes

### Phased implementation plan

实施 phase 拆分（不在本 spec scope，单独 dedicated session 跑）：

**P1 (~2 天)**: GcAllocation 加 marked bit + Value::trace_children + 新
mark_phase 函数（与现 mark_reachable_set 并行存在）。新单测验证 mark
phase 标记正确。

**P2 (~2 天)**: 新 sweep_phase 函数 + collect_cycles 替换路径（feature
flag 或新分支函数）。Side-by-side: 跑 mark-sweep 后跑 trial-deletion，
diff 结果验证等价。

**P3 (~3 天)**: 删除 trial-deletion 代码（run_cycle_collection 旧版 +
tentative + break_cycle_value）。迁移所有 arc_heap_tests/ assertion。

**P4 (~2 天)**: 性能 benchmark — `bench/microbench/gc_cycle.rs` 跑
环密集 / 浅栈 / 大 array 三类 workload，对比 trial-deletion 旧
commit。撰写 benchmark report 入 design doc。

总计 ~9 天工作量。本 spec 只 land proposal/spec/design 文档；实施 phase
分 P1-P4 分别 commit，每个 commit 独立 GREEN。

### Mark phase pseudo-code (详)

```rust
fn mark_phase(&self) {
    // 1. Reset all marks. (Could be done at end of sweep instead;
    //    here for clarity.)
    // — skip: sweep_phase resets marks on survival.

    // 2. Collect initial roots into work list.
    let mut work: Vec<Value> = Vec::with_capacity(64);
    self.collect_roots(|v| {
        if self.mark_if_unmarked_value(v) {
            work.push(v.clone());
        }
    });

    // 3. BFS scan.
    while let Some(v) = work.pop() {
        v.trace_children(&mut |child| {
            if self.mark_if_unmarked_value(child) {
                work.push(child.clone());
            }
        });
    }
}

fn mark_if_unmarked_value(&self, v: &Value) -> bool {
    match v {
        Value::Object(gc) | Value::Array(gc) | ... => {
            let alloc = GcRef::as_alloc(gc);
            alloc.marked.compare_exchange(0, 1, AcqRel, Relaxed).is_ok()
        }
        _ => false,  // primitives don't have allocations
    }
}
```

### Sweep phase pseudo-code

```rust
fn sweep_phase(&self) -> u64 {
    let mut freed = 0;
    let mut i = self.inner.lock();
    i.registry.retain(|entry| {
        let marked = entry.alloc.marked.load(Acquire);
        if marked == 0 {
            // Unmarked: drop happens when this fn returns + Arc count goes 0.
            freed += entry.size_hint;
            false
        } else {
            entry.alloc.marked.store(0, Release);  // reset for next cycle
            true
        }
    });
    freed
}
```

## Testing Strategy

- **Mark phase**: 新单测 `mark_phase_visits_reachable_only` —
  构造 root → A → B → C 链 + 不可达 D；验证 mark 后 A/B/C marked, D 不
- **Sweep phase**: `sweep_phase_drops_unmarked_increments_freed_bytes`
- **Cycle handling**: `cyclic_unreachable_gets_swept` — A ↔ B 互引但
  无 root → 应都被 sweep
- **GC unit tests**: 全套 arc_heap_tests/ 跑通 + per-test 调整记录在 P3 commit
- **VM e2e**: ./scripts/test-vm.sh + ./scripts/test-stdlib.sh GREEN
- **Bench**: `bench/microbench/gc_cycle.rs` — 对比报告入 design doc P4 commit

## Deferred / Future Work

### `add-write-barriers` (A3 dep)
- Reference assignment 处插桩，跟踪跨代引用；prerequisite for A3 generational

### `add-concurrent-gc` (A4)
- 本 spec 落地后再开。mark phase 改为 concurrent (with write barrier) +
  sweep 改为 background thread

### `add-custom-allocator` (A1)
- 替换 Arc backing 为 bump pointer + region allocator；与 A2 正交但
  叠加后是 production-quality GC 基础
