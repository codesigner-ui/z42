# z42.time — 设计文档

> 落地版本：2026-05-14（add-z42-time）
> 增量：2026-05-26 `add-datetime-iso8601` · 2026-05-27 `add-thread-sleep`（迁 z42.threading）· 2026-05-27 `add-timezone-basics`
> 包路径：`src/libraries/z42.time/`
> 命名空间：`Std.Time`

## 职责与范围

UTC 时刻（`DateTime`）、时间段（`TimeSpan`）、单调高精度计时器（`Stopwatch`）、固定 offset 时区（`TimeZone`）。

**含**（截至 2026-05-27）：
- `DateTime.ToIso8601()` / `ToIso8601Basic()` / `ToIso8601With(tz)` —— UTC + 任意时区 ISO-8601 渲染（内部用 `_civilFromDays`）
- `TimeZone.FromName(short_code)` / `FromOffsetMinutes(±n)` / `Utc()` —— 固定 offset，~22 短代码（UTC/GMT/EST/PST/JST/IST/…）

**不含**（仍 Deferred — 见各段）：
- IANA tzdata 完整数据库（`America/New_York` 风 + DST 自动切换）
- 用户可调用的日历分解 getter（`.Year / .Month / .Day / .Hour / ...` —— 内部 `_civilFromDays` 已存在，只对 ISO 渲染暴露）
- ISO-8601 之外的格式化（`strftime` / C# format string）+ ISO 解析
- `Sleep` —— 已迁至 [`z42.threading.Thread.Sleep(ms)`](../../../src/libraries/z42.threading/src/Thread.z42)
- `Timer` / `Delay`（异步定时器）
- `DateTimeOffset` 类型（封装 `DateTime + TimeZone`；当前用户手写 `dt.ToIso8601With(tz)` 凑合）

## 架构

```
z42.time (L1)
  ├── TimeSpan   — 内部单位：i64 ns，范围 ±292 年
  ├── DateTime   — 内部单位：i64 Unix epoch ms；依赖 __time_now_ms
  │                 + ToIso8601() / ToIso8601Basic() / ToIso8601With(tz)
  ├── Stopwatch  — 内部单位：i64 ns；依赖 __time_now_mono_ns（单调时钟）
  └── TimeZone   — 固定 offset (i32 minutes) + 名称；~22 短代码查表
                    add-timezone-basics (2026-05-27)
        ↓
    z42.core (L0)
```

## API 约定

z42 当前不支持命名属性 getter（`Name { get { ... } }`），所有访问器均声明为方法：
- `TotalNanoseconds()` / `TotalMilliseconds()` / `TotalSeconds()` / …
- `UnixMs()` / `IsRunning()` / `Elapsed()`

待语言 property getter 完整支持后，可在 Phase 2 将上述方法升级为属性（需走 lang spec 变更流程）。

## VM Native 依赖

| Native 名称 | 所在 Rust 模块 | 用途 |
|-------------|---------------|------|
| `__time_now_ms` | `corelib/time.rs`（或 `corelib/mod.rs` 内联） | 系统 wall-clock，Unix epoch ms |
| `__time_now_mono_ns` | `corelib/bench.rs` | 单调高精度时钟，ns |

> z42.time 直接使用 VM intrinsic（与 z42.math 同等例外），不通过 z42.core 中转。
> 待 add-std-process 归档后，Decision 2 将 `__time_now_mono_ns` 重命名为 `__time_now_mono_ns`，Stopwatch.z42 同步更新。

## Deferred / Future Work

### time-future-calendar
- **来源**：add-z42-time 设计期
- **触发原因**：日历分解（年/月/日）依赖时区数据库（tz data），v0 不引入
- **前置依赖**：时区文件加载机制（需 VM 资源嵌入或文件系统 API）
- **触发条件**：有实际格式化/解析需求时
- **当前 workaround**：`DateTime.UnixMs()` 暴露原始 ms，用户自行做日历计算

### time-future-format-parse
- **来源**：add-z42-time 设计期
- **状态**：~~ISO 8601 输出~~ ✅ 已落地 2026-05-26 `add-datetime-iso8601` (`DateTime.ToIso8601()` / `ToIso8601Basic()`) + 2026-05-27 `add-timezone-basics` (`ToIso8601With(tz)`)；~~ISO 8601 解析~~ ✅ 已落地 2026-05-27 `add-datetime-iso8601-parse` (`DateTime.ParseIso8601(string)`)，接受日期 / 带 'T'/'space' / `Z` / `±HH:MM` / `±HHMM` 后缀 + 1-9 位 fractional seconds (truncated to ms)。**`strftime` 风 format string 仍延后** —— 需 `string.Format` 完整格式说明符（L3 IFormattable）
- **当前 workaround for 仍 deferred 部分**：custom-format 用户自行 substring + ParseIso8601 适配预格式化输入

### ~~time-future-sleep-timer~~ — ✅ 已落地 2026-05-30
- **来源**：add-z42-time 设计期
- **状态**：
  - ~~`Sleep`~~ ✅ 2026-05-27 `add-thread-sleep` 在 [`Std.Threading.Thread.Sleep(ms)`](../../../src/libraries/z42.threading/src/Thread.z42)
  - ~~`Timer` / `Delay`（同步回调，无需 async/await）~~ ✅ 2026-05-30 `add-z42-threading-timer` 在
    [`Std.Threading.Timer`](../../../src/libraries/z42.threading/src/Timer.z42) ——
    `StartPeriodic(intervalMs, callback)` / `StartOnce(delayMs, callback)` / `Stop` /
    `StopAndJoin` / `IsRunning`；纯脚本 over Thread + Sleep，100ms 颗粒度
    chunked sleep 让 Stop 响应快；callback 抛 swallow + Log.Error；不重叠调用
  - `async Delay`（与 await 自然集成的回调）**仍延后**，需 L3 async/await

### ~~time-future-datetime-offset~~ — ✅ 已落地 2026-06-03 (`add-datetime-offset`)
- `Std.Time.DateTimeOffset` — `(DateTime utc, TimeZone tz)` pair with
  BCL-aligned surface: ctor + `Now(tz)` / `FromLocal(...)` / `Parse(iso8601)`
  factories; `UtcDateTime` / `LocalDateTime` / `Offset` accessors; local
  field accessors (`Year` / `Month` / ... delegate to `LocalDateTime`);
  `ToIso8601` round-trip; `Equals` (UTC-only) + `EqualsExact` (UTC + offset);
  `Subtract(DateTimeOffset) → TimeSpan`; `Add(TimeSpan)` / `SubtractSpan`.
  21 tests cover construct / null / from-local / Parse positive/negative/
  no-colon offset / no-offset-throws / iso8601 round-trip / equality
  semantics / Subtract / Add / Now.
- **Implementation note**: class definition lives at the bottom of
  `DateTime.z42` rather than its own file as a workaround for a
  z42.time-package-specific TypeChecker E0402 bug (`expected X, got X`)
  triggered by new src files referencing sibling cross-file types. See
  `archive/2026-06-03-add-datetime-offset/tasks.md` "落地概览" for the
  minimal repro and suspected root cause (recent metadata refactors
  `272b0115` / `06b57853`). Move to own file once TypeChecker fix lands.

### time-future-tzdata-iana
- **来源**：add-timezone-basics 2026-05-27 实施期延后
- **触发原因**：IANA `America/New_York` 风的命名时区需完整 tzdata 数据库 + DST 切换计算；要么 embed 全表（~600KB）要么调 OS API（每平台 API 不同）
- **前置依赖**：VM 资源嵌入机制 OR 跨平台 tz syscall 抽象层
- **触发条件**：跨时区业务真撞到 DST（夏令时切换日的 ambiguous / non-existent local time 处理）
- **当前 workaround**：手维护 ~22 个短代码（EST/EDT 显式分开 —— 用户自己根据日期选）+ 任意 fixed offset (`TimeZone.FromOffsetMinutes`)

### ~~time-rename-bench-now-ns~~ — **✅ 已落地 2026-06-03 (`rename-bench-now-ns-to-time-mono`)**
- **来源**：add-z42-time Decision 2
- **落地**：VM builtin `__bench_now_ns` → `__time_now_mono_ns` (corelib/mod.rs + corelib/bench.rs + bench_tests.rs); z42 caller Bencher.z42 + Stopwatch.z42 同步重命名 (`[Native]` attr + 调用点); 设计文档 time.md / organization.md / testing.md / cross-platform-testing.md 同步; 旧名称在 pre-1.0 不留兼容路径
