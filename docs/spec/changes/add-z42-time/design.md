# Design: z42.time MVP

## Architecture

```
┌────────────────────────────────────┐
│ User code                          │
└────────────────┬───────────────────┘
                 │
                 ▼
┌────────────────────────────────────┐
│ z42.time (z42 .z42 sources)        │
│   Std.Time.DateTime  : i64 UnixMs  │
│   Std.Time.TimeSpan  : i64 ns      │
│   Std.Time.Stopwatch : 2× i64 ns   │
└────────────────┬───────────────────┘
                 │ [Native(...)]
                 ▼
┌────────────────────────────────────┐
│ corelib registry (Rust)            │
│   __time_now_ms        (existing)  │
│   __time_now_mono_ns   (renamed)   │
└────────────────────────────────────┘
```

类型选择：sealed class（z42 当前无 struct on stack）；instance 是 GC 对象，但因不可变 + 只持一个 i64 字段，开销可控。

## Decisions

### Decision 1: TimeSpan 内部单位 = i64 ns，DateTime 内部单位 = i64 UTC ms

**问题**：两类型该用同一单位还是各自最佳？

**选项**：
- A — 都用 ns：DateTime 范围 ±292 年，对 epoch (1970) 实际 +/- 219 年覆盖到 ~2189 年，够用但 vibing 不爽
- B — 都用 ms：TimeSpan 失去亚毫秒精度，Stopwatch 用不了
- C — 都用 C# 100ns tick：i64 范围 ~29200 年，统一单位 tick；与 `__time_now_ms` 转换需 `* 10000`
- **D（选）— DateTime ms + TimeSpan ns**：DateTime 直接镜像 `__time_now_ms`，Stopwatch 全精度 ns；`DateTime - DateTime` 返回 TimeSpan（ms × 1_000_000 = ns，无精度损失）

**决定**：D。每类型选其最自然的单位，转换无精度损失，API 对外仍提供 ms/sec/ns 各级访问器。

### Decision 2: 单调 native 重命名 `__bench_now_ns` → `__time_now_mono_ns`

**问题**：现有 `__bench_now_ns` 只服务 Bencher，新包想用它做 Stopwatch。

**选项**：
- A — z42.time 直接引 `__bench_now_ns`：名字违和（time 类竟然叫 bench）
- **B（选）— 重命名为 `__time_now_mono_ns`**：z42.test 与 z42.time 都用此名；pre-1.0 无 compat，一次切干净

**决定**：B。"不为旧版本提供兼容"原则适用。

### Decision 3: 删除 `Std.IO.Environment.GetCurrentTimeMs`

**问题**：z42.io 当前错位包装 `__time_now_ms` 为 `Environment.GetCurrentTimeMs`。

**选项**：
- A — 保留 + deprecated alias：违反 CLAUDE.md "不留 alias"
- **B（选）— 直接删，调用方迁到 `DateTime.UtcNow.UnixMs`**

**决定**：B。已知调用方仅 z42.io 自测一处，迁移成本低。

### Decision 4: Stopwatch 内部状态

**问题**：Stopwatch 需追踪 Running / Stopped 状态 + 累积 elapsed。

**实施**：两个 i64 字段：
- `_startNs: long` — 当前段开始时的 mono ns 时刻（仅 Running 时有意义）
- `_elapsedNs: long` — Stop 前累积的 ns（不含当前段）
- `IsRunning: bool` — Running 标志

行为：
- `Start()` / `StartNew()`：若已 Running，no-op；否则 `_startNs = monoNow(); IsRunning = true`
- `Stop()`：若 Running，`_elapsedNs += monoNow() - _startNs; IsRunning = false`；否则 no-op
- `Restart()`：`_elapsedNs = 0; _startNs = monoNow(); IsRunning = true`
- `Elapsed` 读取：
  - Running：`return TimeSpan.FromNanoseconds(_elapsedNs + (monoNow() - _startNs))`
  - Stopped：`return TimeSpan.FromNanoseconds(_elapsedNs)`

匹配 C# `System.Diagnostics.Stopwatch` 行为。

### Decision 5: z42.time 在 stdlib 包层级

**位置**：独立包 `z42.time`（不并入 z42.core）。

理由：保持 z42.core 最小（仅核心类型 + protocols）；time 是 L1 但语义独立，类比 z42.math。包依赖：`z42.core`（用 string / int / bool / Exception 等）。

### Decision 6: z42.test.Bencher 是否改用 Std.Time.Stopwatch？

**选项**：
- A — Bencher 改走 Stopwatch：减少重复，但 z42.test 增加 `z42.time` 依赖（依赖图变深）
- **B（选）— Bencher 仍直接调 `__time_now_mono_ns`**：避免加依赖。Bencher 与 Stopwatch 走同一原生即可

**决定**：B。Bencher 是性能敏感路径，少一层 OOP 包装更直接；依赖图保持平。

### Decision 7: 比较 / 算术运算符如何实现

z42 当前**没有用户定义 operator overloading**（customization Layer 3 deferred）。所以 `DateTime - DateTime` 等不能写成 `operator-`，必须是命名方法。

**实施**：
- `dt1.Subtract(dt2) → TimeSpan`、`dt1.Add(span) → DateTime`、`dt1.IsAfter(dt2) → bool`、`dt1.IsBefore(dt2) → bool`、`dt1.Equals(dt2) → bool`
- 命名风格对齐 C# BCL `DateTime.Subtract` / `Add` 方法

Scenario 中写的 `a - b` / `a < b` 是描述意图，落地为命名方法调用。Scenario 文字需对应调整（实施时同步）。

> **重要修正**：上面 spec.md 的 Scenario 用了 `-` / `+` / `<` 等中缀符号，必须在实施时改写为命名方法调用。本 design Decision 7 修正 spec 表达，落地以方法调用为准；spec.md 在 tasks 阶段同步更新。

## Implementation Notes

### TimeSpan 类骨架（伪 z42）

```z42
namespace Std.Time;

public sealed class TimeSpan {
    private long _ns;

    public TimeSpan(long nanoseconds) { _ns = nanoseconds; }

    public static TimeSpan Zero => new TimeSpan(0);

    public static TimeSpan FromNanoseconds(long ns) => new TimeSpan(ns);
    public static TimeSpan FromMilliseconds(long ms) => new TimeSpan(ms * 1_000_000);
    public static TimeSpan FromSeconds(double sec) => new TimeSpan((long)(sec * 1e9));
    public static TimeSpan FromMinutes(double min) => new TimeSpan((long)(min * 60.0 * 1e9));
    public static TimeSpan FromHours(double hrs)   => new TimeSpan((long)(hrs * 3600.0 * 1e9));

    public long   TotalNanoseconds  => _ns;
    public long   TotalMilliseconds => _ns / 1_000_000;
    public double TotalSeconds      => (double)_ns / 1e9;
    public double TotalMinutes      => TotalSeconds / 60.0;
    public double TotalHours        => TotalSeconds / 3600.0;

    public TimeSpan Add(TimeSpan other)      => new TimeSpan(_ns + other._ns);
    public TimeSpan Subtract(TimeSpan other) => new TimeSpan(_ns - other._ns);

    public bool IsLessThan(TimeSpan other)    => _ns <  other._ns;
    public bool IsLessEqual(TimeSpan other)   => _ns <= other._ns;
    public bool IsGreaterThan(TimeSpan other) => _ns >  other._ns;
    public bool Equals(TimeSpan other)        => _ns == other._ns;
}
```

### DateTime 类骨架

```z42
namespace Std.Time;

public sealed class DateTime {
    private long _unixMs;

    public DateTime(long unixMs) { _unixMs = unixMs; }

    [Native("__time_now_ms")]
    private static extern long NowMs();

    public static DateTime UtcNow => new DateTime(NowMs());
    public static DateTime FromUnixMs(long unixMs) => new DateTime(unixMs);
    public static DateTime UnixEpoch => new DateTime(0);

    public long UnixMs => _unixMs;

    public TimeSpan Subtract(DateTime other) =>
        TimeSpan.FromNanoseconds((_unixMs - other._unixMs) * 1_000_000);

    public DateTime Add(TimeSpan span) =>
        new DateTime(_unixMs + span.TotalMilliseconds);

    public DateTime SubtractSpan(TimeSpan span) =>
        new DateTime(_unixMs - span.TotalMilliseconds);

    public bool IsAfter(DateTime other)  => _unixMs >  other._unixMs;
    public bool IsBefore(DateTime other) => _unixMs <  other._unixMs;
    public bool Equals(DateTime other)   => _unixMs == other._unixMs;
}
```

### Stopwatch 类骨架

```z42
namespace Std.Time;

public sealed class Stopwatch {
    private long _startNs;
    private long _elapsedNs;
    private bool _running;

    [Native("__time_now_mono_ns")]
    private static extern long MonoNs();

    public Stopwatch() {
        _startNs = 0;
        _elapsedNs = 0;
        _running = false;
    }

    public static Stopwatch StartNew() {
        var sw = new Stopwatch();
        sw.Start();
        return sw;
    }

    public bool IsRunning => _running;

    public void Start() {
        if (_running) return;
        _startNs = MonoNs();
        _running = true;
    }

    public void Stop() {
        if (!_running) return;
        _elapsedNs = _elapsedNs + (MonoNs() - _startNs);
        _running = false;
    }

    public void Restart() {
        _elapsedNs = 0;
        _startNs = MonoNs();
        _running = true;
    }

    public TimeSpan Elapsed {
        get {
            long ns = _elapsedNs;
            if (_running) ns = ns + (MonoNs() - _startNs);
            return TimeSpan.FromNanoseconds(ns);
        }
    }
}
```

> z42 当前 property syntax 支持情况待 check — 若不支持 `get { ... }` 块，改为 `public TimeSpan GetElapsed()` 方法。spec 同步更新。

### VM 改动

[src/runtime/src/corelib/bench.rs](../../../../src/runtime/src/corelib/bench.rs):
```diff
- pub fn builtin_bench_now_ns(_ctx: &VmContext, _args: &[Value]) -> Result<Value> {
+ pub fn builtin_time_now_mono_ns(_ctx: &VmContext, _args: &[Value]) -> Result<Value> {
```

[src/runtime/src/corelib/mod.rs](../../../../src/runtime/src/corelib/mod.rs):
```diff
- ("__bench_now_ns",   bench::builtin_bench_now_ns),
+ ("__time_now_mono_ns", bench::builtin_time_now_mono_ns),
```

可选：把 `bench.rs` 改名为 `time.rs`（or `clock.rs`），把 `builtin_bench_black_box` 留在原文件 / 移到 `bench.rs` 单独保留。实施时定。

## Testing Strategy

z42 stdlib 已有 [Test] dogfood 框架（[z42.test 包](../../../../src/libraries/z42.test/)）。本 spec 走相同路径：

- `tests/timespan.z42`：FromMilliseconds/FromSeconds/FromMinutes/FromHours 各 1 测；Zero / 负数 / 加减 / 比较 各 1 测 ≈ 10 个 case
- `tests/datetime.z42`：UtcNow 非负 / FromUnixMs / Add/Subtract Span / Subtract DateTime / 比较 ≈ 7 个 case
- `tests/stopwatch.z42`：StartNew → IsRunning / Stop 后 Elapsed 稳定 / Restart 清零 / Running 单调 / 默认构造未启动 ≈ 5 个 case

跨包回归：
- z42.io 自测改写 `Environment.GetCurrentTimeMs` → `DateTime.UtcNow.UnixMs`，仍全绿
- z42.test Bencher 自测：`__bench_now_ns` 改名后仍全绿
- 全 test-all.sh 6 stage 全绿
