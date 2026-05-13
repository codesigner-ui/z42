# z42.time — 设计文档

> 落地版本：2026-05-14（add-z42-time）
> 包路径：`src/libraries/z42.time/`
> 命名空间：`Std.Time`

## 职责与范围

UTC 时刻（`DateTime`）、时间段（`TimeSpan`）、单调高精度计时器（`Stopwatch`）。

**不含**：日历分解、格式化/解析、Sleep、Timer、DateTimeOffset、时区。

## 架构

```
z42.time (L1)
  ├── TimeSpan   — 内部单位：i64 ns，范围 ±292 年
  ├── DateTime   — 内部单位：i64 Unix epoch ms；依赖 __time_now_ms
  └── Stopwatch  — 内部单位：i64 ns；依赖 __bench_now_ns（单调时钟）
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
- **触发原因**：ISO 8601 格式化 / 解析依赖 `string.Format` + 数值格式说明符（L3 IFormattable）
- **前置依赖**：IFormattable 接入 string 插值、完整 `string.Format` 格式说明符
- **触发条件**：日志 / API 响应需要 ISO 时间字符串
- **当前 workaround**：`DateTime.UnixMs().ToString()` 输出原始 ms 值

### time-future-sleep-timer
- **来源**：add-z42-time 设计期
- **触发原因**：Sleep 需要 async/阻塞 API；Timer 需要回调机制（lambda）
- **前置依赖**：async/await 运行时（L3），或阻塞 Sleep syscall（L2）
- **触发条件**：协议超时 / 轮询 / 动画等场景
- **当前 workaround**：busy-wait 循环（仅测试用）

### time-future-datetime-offset
- **来源**：add-z42-time 设计期
- **触发原因**：DateTimeOffset 含时区偏移，与 DateTimeOffset → DateTime 转换逻辑复杂
- **前置依赖**：time-future-calendar
- **触发条件**：跨时区业务逻辑需求

### time-rename-bench-now-ns
- **来源**：add-z42-time Decision 2（暂缓，实施期延后）
- **触发原因**：`__bench_now_ns` 命名来自 benchmarking 历史，语义上应归属 time 子系统
- **前置依赖**：add-std-process 归档（两 spec 共同改 corelib/mod.rs + z42.test/Bencher.z42）
- **触发条件**：add-std-process 完成归档后立即执行
- **当前 workaround**：Stopwatch.z42 中 TODO 注释说明待 rename
