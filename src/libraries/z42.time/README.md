# z42.time

## 职责
UTC 时刻（`DateTime`）、时间段（`TimeSpan`）、单调计时器（`Stopwatch`）。
不含日历分解、格式化/解析、Sleep、Timer、DateTimeOffset（见 Deferred 段）。

## 核心文件
| 文件 | 职责 |
|------|------|
| `src/TimeSpan.z42` | 时间段值，内部 i64 ns |
| `src/DateTime.z42` | UTC 时刻值，内部 i64 Unix epoch ms |
| `src/Stopwatch.z42` | 单调高精度计时器，使用 `__bench_now_ns` |

## 入口点
- `Std.Time.TimeSpan` — 工厂：`FromMilliseconds()` / `FromSeconds()` / `FromNanoseconds()` / `Zero()`；访问器：`TotalNanoseconds()` / `TotalMilliseconds()` / `TotalSeconds()`
- `Std.Time.DateTime` — 工厂：`UtcNow()` / `FromUnixMs()` / `UnixEpoch()`；访问器：`UnixMs()`
- `Std.Time.Stopwatch` — 工厂：`StartNew()`；方法：`Start()` / `Stop()` / `Restart()`；访问器：`IsRunning()` / `Elapsed()`

> 注：z42 当前不支持命名属性 getter（`Name { get { ... } }`），所有访问器均为方法。

## 依赖关系
依赖 `z42.core`；无其他 stdlib 依赖。
