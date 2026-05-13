# Proposal: Add z42.time Standard Library Package

## Why

z42 当前没有时间 API，应用层无法记日志时间戳、做超时、测量耗时、比较两个时刻。`__time_now_ms` 原生已实现但错位包装在 `Std.IO.Environment.GetCurrentTimeMs`（z42.io 包），抽象层级与命名都不合理。

roadmap M7 stdlib P0 列表 #1。MVP 不依赖任何未实现语言能力，可立刻落地。

## What Changes

新增 `z42.time` 包（L1 层），提供：

1. **`Std.Time.DateTime`** — UTC 时刻值类型，内部存储 i64 ms since Unix epoch
2. **`Std.Time.TimeSpan`** — 时间段值类型，内部存储 i64 ns
3. **`Std.Time.Stopwatch`** — 单调时钟测量类（Start / Stop / Restart / Elapsed）

VM 侧：
- 重命名 `__bench_now_ns` → `__time_now_mono_ns`（pre-1.0 无 compat，一次切干净）
- `__time_now_ms` 保留位置不变（仍在 `corelib/fs.rs::builtin_time_now_ms`，name 已合理）

z42.io 清理：
- 删除 `Std.IO.Environment.GetCurrentTimeMs`（功能下沉到 `DateTime.UtcNow.UnixMs`）
- 同步删除 z42.io 自测对该方法的引用

z42.test 同步：
- `Bencher` 内部从 `__bench_now_ns` 改用 `__time_now_mono_ns`（z42.test 引入 `z42.time` 依赖）

## Scope（允许改动的文件）

**MODIFY**
| 文件 | 说明 |
|---|---|
| `src/runtime/src/corelib/bench.rs` | `builtin_bench_now_ns` 改名 `builtin_time_now_mono_ns`；文件可保留或合并到新位置 |
| `src/runtime/src/corelib/mod.rs` | 注册名 `__bench_now_ns` → `__time_now_mono_ns` |
| `src/runtime/src/corelib/bench_tests.rs` | 测试引用更名 |
| `src/libraries/z42.io/src/Environment.z42` | 删除 `GetCurrentTimeMs` 方法 |
| `src/libraries/z42.io/tests/console.z42` | 移除 / 改写 `Environment.GetCurrentTimeMs` 调用（改用 `Std.Time.DateTime.UtcNow`）|
| `src/libraries/z42.test/src/Bencher.z42`（若存在）| 改用 `__time_now_mono_ns` + 添加 `z42.time` 依赖（或直接引用原生）|
| `src/libraries/z42.test/z42.test.z42.toml` | 若 Bencher 改走 `Std.Time.Stopwatch`，加 `z42.time` 依赖 |
| `src/libraries/z42.io/README.md` | 移除 GetCurrentTimeMs 行 |

**NEW**
| 文件 | 说明 |
|---|---|
| `src/libraries/z42.time/z42.time.z42.toml` | 包 manifest |
| `src/libraries/z42.time/README.md` | 包 README |
| `src/libraries/z42.time/src/DateTime.z42` | `Std.Time.DateTime` 类 + UtcNow / UnixMs / 运算符 |
| `src/libraries/z42.time/src/TimeSpan.z42` | `Std.Time.TimeSpan` 类 + From* 工厂 + 累加器 + 比较 |
| `src/libraries/z42.time/src/Stopwatch.z42` | `Std.Time.Stopwatch` 类 + StartNew / Stop / Restart / Elapsed |
| `src/libraries/z42.time/tests/datetime.z42` | DateTime 单元测试（用 `[z42.test.Test]` attribute）|
| `src/libraries/z42.time/tests/timespan.z42` | TimeSpan 测试 |
| `src/libraries/z42.time/tests/stopwatch.z42` | Stopwatch 测试 |
| `docs/design/stdlib/time.md` | 包设计文档（API 矩阵 + 决策记录）|

**MODIFY (docs)**
| 文件 | 说明 |
|---|---|
| `src/libraries/README.md` | 包列表加 z42.time |
| `docs/design/stdlib/roadmap.md` | P0 表移除 z42.time 行 |
| `docs/design/stdlib/organization.md` | 现状表加 z42.time |
| `scripts/build-stdlib.sh` | 加 z42.time 编译入口（如该脚本枚举包）|

**只读引用**
- `src/runtime/src/corelib/fs.rs` — 理解 `__time_now_ms` 实现
- `src/libraries/z42.test/src/` — 理解 Bencher 当前 monotonic 用法
- `docs/design/stdlib/organization.md` — L1 层规则

## Out of Scope（v0 不做，留 follow-up）

- **日历分解**：`DateTime.Year / Month / Day / DayOfWeek` 等访问器（含闰年判断、月长度表）
- **本地时区 / DateTimeOffset**：仅 UTC 一种时区
- **Parse / Format**：ISO 8601 字符串往返
- **`Sleep(TimeSpan)`** 线程阻塞：等并发模型落地
- **`Timer` / `Timeout`**：异步等 async/await 就绪

均记入 `docs/design/stdlib/time.md` Deferred 段 + roadmap Deferred Backlog Index。

## Open Questions

无（设计决策已与 User 确认：A + ns + ms + 重命名 + 删除）。
