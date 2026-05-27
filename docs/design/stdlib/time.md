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
  ├── Stopwatch  — 内部单位：i64 ns；依赖 __bench_now_ns（单调时钟）
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
| `__bench_now_ns` | `corelib/bench.rs` | 单调高精度时钟，ns |

> z42.time 直接使用 VM intrinsic（与 z42.math 同等例外），不通过 z42.core 中转。
> 待 add-std-process 归档后，Decision 2 将 `__bench_now_ns` 重命名为 `__time_now_mono_ns`，Stopwatch.z42 同步更新。

## Deferred / Future Work

### time-future-calendar
- **来源**：add-z42-time 设计期
- **触发原因**：日历分解（年/月/日）依赖时区数据库（tz data），v0 不引入
- **前置依赖**：时区文件加载机制（需 VM 资源嵌入或文件系统 API）
- **触发条件**：有实际格式化/解析需求时
- **当前 workaround**：`DateTime.UnixMs()` 暴露原始 ms，用户自行做日历计算

### time-future-format-parse
- **来源**：add-z42-time 设计期
- **状态**：~~ISO 8601 输出~~ ✅ 已落地 2026-05-26 `add-datetime-iso8601` (`DateTime.ToIso8601()` / `ToIso8601Basic()`) + 2026-05-27 `add-timezone-basics` (`ToIso8601With(tz)`)。**ISO-8601 解析 + `strftime` 风 format string 仍延后** —— 需 `string.Format` 完整格式说明符（L3 IFormattable）
- **当前 workaround for 仍 deferred 部分**：`DateTime.UnixMs()` 拿原始 ms 自己 parse

### time-future-sleep-timer
- **来源**：add-z42-time 设计期
- **状态**：~~`Sleep`~~ ✅ 已落地 2026-05-27 `add-thread-sleep` 在 [`Std.Threading.Thread.Sleep(ms)`](../../../src/libraries/z42.threading/src/Thread.z42)（不在 z42.time 里 —— 时间语义虽属 time，但阻塞执行属 threading）。`Timer` / `Delay`（async 回调）**仍延后**，需 async/await 运行时（L3）

### time-future-datetime-offset
- **来源**：add-z42-time 设计期
- **状态**：**部分解锁** —— `add-timezone-basics` 2026-05-27 给 `DateTime.ToIso8601With(tz)` 提供了渲染层等价能力；用户对绝大多数 "render this UTC in user's local" 场景已够用。**`DateTimeOffset` 类型本体**（拉到 stateful 对象封装 `(DateTime, TimeZone)`）仍延后 —— 当前手写 `(dt, tz)` 凑合

### time-future-tzdata-iana
- **来源**：add-timezone-basics 2026-05-27 实施期延后
- **触发原因**：IANA `America/New_York` 风的命名时区需完整 tzdata 数据库 + DST 切换计算；要么 embed 全表（~600KB）要么调 OS API（每平台 API 不同）
- **前置依赖**：VM 资源嵌入机制 OR 跨平台 tz syscall 抽象层
- **触发条件**：跨时区业务真撞到 DST（夏令时切换日的 ambiguous / non-existent local time 处理）
- **当前 workaround**：手维护 ~22 个短代码（EST/EDT 显式分开 —— 用户自己根据日期选）+ 任意 fixed offset (`TimeZone.FromOffsetMinutes`)

### time-rename-bench-now-ns
- **来源**：add-z42-time Decision 2（暂缓，实施期延后）
- **触发原因**：`__bench_now_ns` 命名来自 benchmarking 历史，语义上应归属 time 子系统
- **前置依赖**：add-std-process 归档（两 spec 共同改 corelib/mod.rs + z42.test/Bencher.z42）
- **触发条件**：add-std-process 完成归档后立即执行
- **当前 workaround**：Stopwatch.z42 中 TODO 注释说明待 rename
