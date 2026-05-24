# Design: GC Pause-Time Rolling Window

## Architecture

```
 collect_cycles                  HeapStats consumer
      │                                 ▲
      ▼                                 │
 pause_us ──────────►                  │
      │                                 │
      ▼                                 │
 PauseHistogram::record(pause_us)      │
      ├─ buckets[idx] += 1              │ (existing B5 path)
      ├─ min/max/total/count update    │
      └─ NEW: recent_pauses.push(pause_us)
              + pop_front if len > cap
                                        │
                                        │
 Std.GC.RecentPauses()  → long[] ◄──────┘
 Std.GC.PauseWindowCapacity() → long
```

Window lives inside `PauseHistogram`. One `VecDeque<u64>` per heap.
Capacity decided at construction time (env-read once, no
hot-reconfigure).

## Decisions

### Decision 1: Window lives inside `PauseHistogram`

**问题**：另起一个 type，或并入 `PauseHistogram`？

**选项**：
- A — 独立 type `PauseWindow { buf: VecDeque<u64>, cap: usize }`，
  作为 `ArcMagrGC` 的独立 field
- B — `PauseHistogram` 直接加 `recent_pauses: VecDeque<u64>` 字段
- C — 完全独立，对 outside 暴露两个并行 trait method

**决定**：B. record 路径已经在 `pause_histogram.lock()` 内；同一个
锁同一份 update。`HeapStats.pause_histogram` snapshot 自然把
window 一起 clone 出去（`VecDeque` 是 `Clone`），消费端拿一份就够。
独立 type 反而要并行的 lock + stats 投影更复杂。

### Decision 2: Capacity from env (read once) + default 1024

**问题**：window 大小是 const、env、还是 runtime API？

**选项**：
- A — 编译期 const 1024：简单，无配置
- B — `Z42_GC_PAUSE_WINDOW` env-overridable，进程启动 read 一次
- C — runtime API `set_pause_window_capacity(usize)`

**决定**：B. 与现有 `Z42_GC_TENURE` / `Z42_GC_MINOR_THRESHOLD` /
`Z42_SAFEPOINT_THROTTLE` 风格一致。1024 是合理默认（每分钟 ~60
collect 的服务能记下 ~17 分钟历史）；env override 给极端场景。
Runtime API 是过早设计 — 用户实际需要时再开。

环境变量解析：
- 数字 → 取值，clamp `[1, 65536]`
- 0 / 负数 → fallback 1024 + stderr warning
- 非数字 → fallback 1024 + stderr warning

### Decision 3: FIFO via VecDeque, drop oldest on overflow

**问题**：满了之后怎么办？

**选项**：
- A — 丢最新（reject new）：违反 "rolling = 看最新" 直觉
- B — 丢最老（FIFO）：标准 rolling window 行为
- C — Reservoir sampling：保统计学随机性，但用户预期"最近 N 个"

**决定**：B. `VecDeque::push_back` + `pop_front` if `len() ==
cap`。O(1) ring-buffer 行为（VecDeque 内部就是 ring）。

### Decision 4: Builtin returns `long[]`（不引入新 TypeDesc）

与 B5 `PauseHistogram() -> long[]` / `PauseStatsRaw() -> long[]` 一
致：直接 `long[]`，script 端按 index 读，不需要新 TypeDesc。

返回顺序：**oldest first**（chronological order）。Script 端读
`result[result.Length - 1]` 拿到最新一次 pause；`result[0]` 是 window
最老的。

### Decision 5: Empty deque on freshly-created heap

`Default for PauseHistogram` 返回 `recent_pauses: VecDeque::with_capacity(cap)`
但 `len() == 0`。`Std.GC.RecentPauses()` 返长度 0 的 `long[]`。Script
端先 `result.Length == 0` 判 empty。

### Decision 6: Capacity exposed via separate builtin

不和 `PauseStatsRaw` 合并（不破坏 4-element raw 协议）。新独立
builtin `Std.GC.PauseWindowCapacity() -> long`：script 想知道"我读到
1024 条是因为发生了 1024 次 collect 还是因为 deque 满了"时调用，
比对 `RecentPauses().Length`。

## Implementation Notes

### PauseHistogram changes

```rust
// gc/types.rs

pub const PAUSE_WINDOW_DEFAULT_CAP: usize = 1024;

/// Read `Z42_GC_PAUSE_WINDOW`; clamp to `[1, 65536]`; fallback to default
/// on parse failure / 0 / negative.
pub fn pause_window_cap_from_env() -> usize {
    match std::env::var("Z42_GC_PAUSE_WINDOW").ok().and_then(|v| v.parse::<i64>().ok()) {
        Some(n) if n >= 1 && n <= 65536 => n as usize,
        _ => PAUSE_WINDOW_DEFAULT_CAP,
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PauseHistogram {
    pub buckets:        [u64; 8],
    pub min_us:         u64,
    pub max_us:         u64,
    pub total_us:       u64,
    pub count:          u64,
    pub recent_pauses:  VecDeque<u64>,  // NEW
    pub window_cap:     usize,           // NEW (constant for instance lifetime)
}

impl Default for PauseHistogram {
    fn default() -> Self {
        let cap = pause_window_cap_from_env();
        Self {
            buckets: [0; 8], min_us: u64::MAX, max_us: 0,
            total_us: 0, count: 0,
            recent_pauses: VecDeque::with_capacity(cap),
            window_cap: cap,
        }
    }
}

impl PauseHistogram {
    pub fn record(&mut self, pause_us: u64) {
        // ... existing bucket / min / max / total / count updates ...

        // NEW: rolling window
        if self.recent_pauses.len() == self.window_cap {
            self.recent_pauses.pop_front();
        }
        self.recent_pauses.push_back(pause_us);
    }
}
```

**Note**: `HeapStats` was `Copy + Default + PartialEq + Eq` because
all old `PauseHistogram` fields were `Copy`. Adding `VecDeque<u64>`
removes `Copy` (but keeps `Clone`). We drop `Copy` from
`PauseHistogram` AND `HeapStats`. `stats()` continues to return a
`HeapStats` by value via `Clone`.

Touch-check on `HeapStats: Copy` usage: only `arc_heap.rs::stats()`
returns by value; callers don't bind by `Copy` semantic. Verified
via grep before commit.

### Builtins

```rust
// corelib/gc.rs

pub fn builtin_gc_recent_pauses(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    let h = ctx.heap().stats().pause_histogram;
    let arr: Vec<Value> = h.recent_pauses.iter()
        .map(|&us| Value::I64(us as i64))
        .collect();
    Ok(ctx.heap().alloc_array(arr))
}

pub fn builtin_gc_pause_window_capacity(ctx: &VmContext, _args: &[Value]) -> Result<Value> {
    let cap = ctx.heap().stats().pause_histogram.window_cap;
    Ok(Value::I64(cap as i64))
}
```

### Std.GC.z42 additions

```z42
/// **add-gc-pause-window (2026-05-24)**: 返回 rolling window
/// 内的所有 pause 样本，chronological order（oldest first）。
/// 长度 ≤ `PauseWindowCapacity()`。空 heap 返长度 0。
[Native("__gc_recent_pauses")]
public static extern long[] RecentPauses();

/// **add-gc-pause-window (2026-05-24)**: 返回当前 heap 的 window
/// 容量（`Z42_GC_PAUSE_WINDOW` env，默认 1024）。
[Native("__gc_pause_window_capacity")]
public static extern long PauseWindowCapacity();
```

## Testing Strategy

### Unit tests (P0)

In `gc/types_tests.rs`:

- `default_has_empty_recent_pauses` — `len == 0`, `capacity == default_cap`
- `record_appends_to_window` — 5 records → window has 5 entries in
  chronological order
- `window_drops_oldest_at_capacity` — N+5 records on cap=N window →
  len == N, first entry == record (N+5)-th-to-last
- `window_cap_from_env_clamps` — set `Z42_GC_PAUSE_WINDOW=0` → falls back
  to default; set to negative → fallback; set to 99999999 → clamped to
  65536; set to valid 32 → uses 32

### Integration tests (P0)

In `arc_heap_tests/pause_histogram.rs` (extend existing):

- `recent_pauses_visible_in_stats_snapshot` — `force_collect` N times
  → `heap.stats().pause_histogram.recent_pauses.len() == N`
- `recent_pauses_drops_oldest_after_capacity_exceeded` — small env-set
  cap → exceed cap → window stays at cap

### End-to-end (P0)

`z42.io/tests/gc_pause_window.z42`:

```z42
[Test]
void test_recent_pauses_length_equals_collect_count() {
    long cap = GC.PauseWindowCapacity();
    Assert.True(cap >= 1, "capacity should be positive");
    for (int i = 0; i < 5; i = i + 1) {
        GC.ForceCollect();
    }
    long[] recent = GC.RecentPauses();
    Assert.True(recent.Length >= 5, "should record each force_collect");
}
```

## Phasing

- **P0**: PauseHistogram field + env helper + record append + 2
  builtins + Std.GC.z42 declarations + 4 unit tests + 2 integration
  tests + 1 e2e test
- **P1**: gc.md "Rolling window" subsection + Phase table row + B5
  deferred sub-spec "future → landed" + archive

2 commits, ~0.5-1 session total.

## Deferred / Future Work

### add-gc-pause-window-per-mode

- 3 separate windows, one per GcMode. Depends on
  `add-gc-pause-per-mode` doing the per-mode split first.

### add-gc-pause-window-timestamped

- Each sample carries timestamp_us alongside pause_us → enables
  time-series analysis on the script side.

### add-gc-pause-window-runtime-resize

- `Std.GC.SetPauseWindowCapacity(n)` builtin — runtime adjust without
  restart. Low priority; env-restart usually OK.
