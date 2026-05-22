# Design: GC Pause-Time Histogram

## Architecture

```
  collect_cycles                            HeapStats consumer
       │                                          ▲
       ▼                                          │
  pause_us = end - start ──────────────────►      │
       │                                          │
       ▼                                          │
  ArcMagrGC.pause_histogram (Mutex)         stats()
       │                                          │
       ▼                                          │
  PauseHistogram::record(pause_us)               │
       │                                          │
       ▼                                          │
  buckets[idx]++; min/max/total/count update     │
                                                  │
                                                  │
  Std.GC.PauseHistogram() builtin ─► long[8] ◄──┤
  Std.GC.PauseStatsRaw() builtin ──► long[4] ◄──┘
```

Single histogram per heap. Mutex contention is negligible — recording
happens once per collect (µs–ms-scale operations), not per alloc.

## Decisions

### Decision 1: Fixed 8-bucket logarithmic histogram

**问题**: 桶数 / 边界怎么选？

**选项**:
- A — 等距线性桶（如 0–10 ms 每 1 ms 一桶）：简单但长尾分辨率差
- B — 8 个 log10-spaced 桶（µs 到 10s+）：经典 GC pause 分布友好
- C — t-digest / HDR histogram：精确 percentiles 但 ~200+ LOC + 复杂

**决定**: B. GC pause times span 6 数量级（µs—s）；log scale 给所有
区间相似分辨率。8 桶 = 64 字节 (`[u64; 8]`)，记录是 1 比较+1 inc。

Edge bucket boundaries：`[10, 100, 1_000, 10_000, 100_000, 1_000_000,
10_000_000]` µs. Bucket 0 = 0–10µs (sub-µs collects); bucket 7 =
≥ 10s (catastrophic).

**Trade-off**: 没有 1ms / 100ms 精确边界；用户要 p95 时只能 "p95 落
在 [10, 100) ms" 这种近似。可接受 — 详细 latency analysis 用 t-digest
是后续 perf spec。

### Decision 2: Single histogram, no mode partitioning

**问题**: 每个 GcMode 单独 histogram 还是单 histogram 聚合所有 mode？

**选项**:
- A — 单 histogram (3 modes 共用)：简单；用户 diff PauseHistogram
  before/after set_mode 自己分析
- B — 3 histograms (per mode)：更易直接读 "concurrent 比 stw 快"，但
  API 表面扩大

**决定**: A. v1 简单。Mode 对比通过 stress test / bench / 用户
diff PauseHistogram 解决。Per-mode split 是 perf spec。

### Decision 3: HeapStats 加 pause_histogram 字段

**问题**: Histogram 怎么暴露？新 method on MagrGC trait？扩 HeapStats？
独立 API？

**选项**:
- A — 新 trait method `pause_histogram(&self) -> PauseHistogram`
- B — 扩 HeapStats with `pause_histogram: PauseHistogram`
- C — 独立 ArcMagrGC inherent method

**决定**: B. HeapStats 已经是 GC 状态聚合的 SoT；加字段最自然。
`Std.GC.GetStats()` builtin 已经投射 HeapStats 到 z42 — 单 stats 调
用就能拿到 histogram。两个新 builtins (PauseHistogram /
PauseStatsRaw) 是 raw 访问路径（避免每次 alloc 一个 Std.HeapStats 实
例只为读 histogram）。

### Decision 4: long[] vs new TypeDesc

**问题**: 怎么从 z42 script 视角暴露 `[u64; 8]`?

**选项**:
- A — 用 `Std.HeapStats` pattern: 新 TypeDesc `Std.PauseHistogram`
  with 8 fields。Stats expose 一个 instance.
- B — `long[]` (z42 native array)：8-element array，bucket i 是 index i.
- C — `long[]` for histogram, separate `long[]` for raw stats.

**决定**: C. 简单，无新 TypeDesc，no auto-property boilerplate. z42
script 可以 `var counts = Std.GC.PauseHistogram();` 直接读 `counts[5]`
访问 [100ms, 1s) 桶。Documented bucket boundaries in script-side
doc comment.

### Decision 5: min_us sentinel = u64::MAX

**问题**: Empty histogram 的 min_us 取什么？

**选项**:
- A — 0：但 0 也是 valid pause (sub-µs collect 可能 round 到 0)
- B — u64::MAX (sentinel): 区分 "empty" 与 "0 µs collect"。Caller 检
  `count == 0` 判断 empty。
- C — Option<u64>：z42 没 Optional<T> primitive (would need new shim)

**决定**: B. 简单 sentinel。Script-side doc 写明：先检 `count == 0`
再读 min_us。

### Decision 6: O(1) record cost

`record(pause_us)` 操作：

1. Bucket index via log2-like comparison ladder（unrolled 7 branches）
   或 binary search on `BUCKET_EDGES` array — O(log 8) = 3 比较
2. `buckets[idx] += 1` — 1 store
3. min/max/total/count update — 4 ops

总 ~10 instr + 1 Mutex lock. 每次 collect (µs-ms scale) 加这 ~ns 级别开销 ≪ 1%.

不用 Atomic 替 Mutex (no benefit; collect 已经在 STW)。

## Implementation Notes

### PauseHistogram struct

```rust
// gc/types.rs
#[derive(Debug, Clone)]
pub struct PauseHistogram {
    pub buckets: [u64; 8],
    pub min_us: u64,
    pub max_us: u64,
    pub total_us: u64,
    pub count: u64,
}

const BUCKET_EDGES: [u64; 7] = [
    10,          // bucket 0: < 10 µs
    100,         // bucket 1: [10, 100) µs
    1_000,       // bucket 2: [100µs, 1ms)
    10_000,      // bucket 3: [1, 10) ms
    100_000,     // bucket 4: [10, 100) ms
    1_000_000,   // bucket 5: [100ms, 1s)
    10_000_000,  // bucket 6: [1, 10) s
];                            // bucket 7: ≥ 10 s

impl Default for PauseHistogram {
    fn default() -> Self {
        Self {
            buckets:  [0; 8],
            min_us:   u64::MAX,
            max_us:   0,
            total_us: 0,
            count:    0,
        }
    }
}

impl PauseHistogram {
    pub fn bucket_index(pause_us: u64) -> usize {
        for (i, &edge) in BUCKET_EDGES.iter().enumerate() {
            if pause_us < edge { return i; }
        }
        7
    }

    pub fn record(&mut self, pause_us: u64) {
        let idx = Self::bucket_index(pause_us);
        self.buckets[idx] = self.buckets[idx].saturating_add(1);
        if self.count == 0 || pause_us < self.min_us { self.min_us = pause_us; }
        if pause_us > self.max_us { self.max_us = pause_us; }
        self.total_us = self.total_us.saturating_add(pause_us);
        self.count = self.count.saturating_add(1);
    }
}
```

### HeapStats integration

```rust
#[derive(Debug, Clone, Default)]
pub struct HeapStats {
    // ... existing fields ...
    pub pause_histogram: PauseHistogram,
}
```

### ArcMagrGC field + record

```rust
pub struct ArcMagrGC {
    // ... existing fields ...
    pause_histogram: Mutex<PauseHistogram>,
}
```

At each AfterCollect event point:

```rust
self.pause_histogram.lock().record(pause_us);
self.fire_event(GcEvent::AfterCollect { kind, freed_bytes, pause_us });
```

`stats()`:
```rust
fn stats(&self) -> HeapStats {
    // ... existing code ...
    s.pause_histogram = self.pause_histogram.lock().clone();
    s
}
```

### z42 builtins

```rust
// corelib/gc.rs
pub fn builtin_gc_pause_histogram(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    let stats = ctx.heap().stats();
    let arr: Vec<Value> = stats.pause_histogram.buckets.iter()
        .map(|&n| Value::I64(n as i64))
        .collect();
    Ok(ctx.heap().alloc_array(arr))
}

pub fn builtin_gc_pause_stats_raw(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    let h = ctx.heap().stats().pause_histogram;
    Ok(ctx.heap().alloc_array(vec![
        Value::I64(h.min_us as i64),
        Value::I64(h.max_us as i64),
        Value::I64(h.total_us as i64),
        Value::I64(h.count as i64),
    ]))
}
```

Register in builtin map.

### Std.GC.z42 additions

```z42
/// **add-gc-pause-histogram (2026-05-22)**: 返回 8 元素直方图...
/// (bucket boundaries 列表 in 注释)
[Native("__gc_pause_histogram")]
public static extern long[] PauseHistogram();

/// 返回 4 元素 [min_us, max_us, total_us, count]...
[Native("__gc_pause_stats_raw")]
public static extern long[] PauseStatsRaw();
```

## Testing Strategy

### Unit tests (P0)

- `bucket_index_boundaries` — assert每个 boundary value 落正确桶
- `record_updates_bucket` — record + check 桶 count
- `record_updates_min_max_total_count`
- `record_first_value_sets_min` — sentinel u64::MAX → first pause_us
- `record_saturates_on_u64_overflow` — count/total overflow safe
- `default_is_empty` — `count == 0`, sentinel min
- `histogram_after_multiple_collects_visible_in_stats`
- `histogram_persists_across_mode_switches`

### z42 builtin tests

- `__gc_pause_histogram_returns_array_of_8`
- `__gc_pause_stats_raw_returns_array_of_4`
- 端到端: alloc + force_collect 几次 → `Std.GC.PauseHistogram()`
  非全零 (bucket 0 或 1 应该 > 0 for µs-scale stdlib collect)

### Integration

- 跑 stress test suite 后查 `heap.stats().pause_histogram.count >=
  N`（每个 stress run 跑 ~100 collects；count 应 ≥ stress runs * 50）

## Phasing

- **P0**: PauseHistogram type + record + stats integration + builtins
  + 8 unit tests + 1 z42 e2e test
- **P1**: gc.md "Pause histogram" subsection + B5 backlog "future" →
  "landed" + archive

2 commits, ~1 session total.

## Deferred / Future Work

### add-gc-pause-tdigest
- t-digest 替换 fixed-bucket histogram → 精确 p50/p95/p99 percentiles
- ~200 LOC + 复杂；只在 user 需要精度时开

### add-gc-pause-per-mode
- 3 individual histograms (one per GcMode)。直接对比 mode 性能。

### add-gc-pause-window
- Rolling window (last N collects) 替换 cumulative。Long-running
  process 友好。

### add-gc-pause-sla
- 检测 SLA 违例（e.g. "any pause > 100ms → log warning"）。Hook 进
  AfterCollect event 即可，不一定需要 histogram。

### add-gc-pause-trace-export
- OpenTelemetry / Jaeger 导出。集成层 spec.
