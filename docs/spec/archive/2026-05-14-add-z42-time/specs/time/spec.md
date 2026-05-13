# Spec: z42.time — DateTime / TimeSpan / Stopwatch

## ADDED Requirements

### Requirement: DateTime — UTC 时刻值

#### Scenario: 获取当前 UTC 时刻
- **WHEN** 调用 `DateTime.UtcNow`
- **THEN** 返回 `DateTime` 实例，`UnixMs` 等于 `__time_now_ms` 调用瞬时返回值

#### Scenario: 从 Unix epoch 毫秒构造
- **WHEN** 调用 `DateTime.FromUnixMs(1700000000000)`
- **THEN** 返回 `DateTime` 实例，`UnixMs == 1700000000000`

#### Scenario: 两 DateTime 相减得 TimeSpan
- **WHEN** `a = DateTime.FromUnixMs(2000); b = DateTime.FromUnixMs(500); d = a - b`
- **THEN** `d` 是 `TimeSpan`，`d.TotalMilliseconds == 1500`

#### Scenario: DateTime 加 TimeSpan
- **WHEN** `a = DateTime.FromUnixMs(1000); b = a + TimeSpan.FromMilliseconds(500)`
- **THEN** `b.UnixMs == 1500`

#### Scenario: DateTime 减 TimeSpan
- **WHEN** `a = DateTime.FromUnixMs(1000); b = a - TimeSpan.FromMilliseconds(300)`
- **THEN** `b.UnixMs == 700`

#### Scenario: DateTime 比较
- **WHEN** `a = DateTime.FromUnixMs(100); b = DateTime.FromUnixMs(200)`
- **THEN** `a < b`, `a <= b`, `b > a`, `b >= a`, `a != b`, `!(a == b)` 全部为 true

#### Scenario: DateTime ToString
- **WHEN** `a = DateTime.FromUnixMs(1700000000000); s = a.ToString()`
- **THEN** `s` 返回该实例的 UnixMs 数值字符串（v0 临时格式 — ISO 8601 留 follow-up）

---

### Requirement: TimeSpan — 时间段值

#### Scenario: 工厂构造
- **WHEN** `s = TimeSpan.FromMilliseconds(1500)`
- **THEN** `s.TotalMilliseconds == 1500`, `s.TotalSeconds == 1.5`
- **WHEN** `s = TimeSpan.FromSeconds(2.5)`
- **THEN** `s.TotalMilliseconds == 2500`, `s.TotalSeconds == 2.5`
- **WHEN** `s = TimeSpan.FromMinutes(1)`
- **THEN** `s.TotalSeconds == 60`
- **WHEN** `s = TimeSpan.FromHours(1)`
- **THEN** `s.TotalMinutes == 60`

#### Scenario: 纳秒精度
- **WHEN** `s = TimeSpan.FromNanoseconds(1500)`
- **THEN** `s.TotalNanoseconds == 1500`, `s.TotalMilliseconds == 0`（整数除法截断）

#### Scenario: TimeSpan 加减
- **WHEN** `a = TimeSpan.FromSeconds(1); b = TimeSpan.FromSeconds(2); c = a + b`
- **THEN** `c.TotalSeconds == 3`
- **WHEN** `d = b - a`
- **THEN** `d.TotalSeconds == 1`

#### Scenario: TimeSpan 比较
- **WHEN** `a = TimeSpan.FromMilliseconds(100); b = TimeSpan.FromMilliseconds(200)`
- **THEN** `a < b`, `a <= b`, `b > a`, `b >= a`, `a != b`, `!(a == b)` 全部为 true

#### Scenario: 负 TimeSpan
- **WHEN** `a = TimeSpan.FromMilliseconds(-500)`
- **THEN** `a.TotalMilliseconds == -500`, `a < TimeSpan.Zero`

#### Scenario: TimeSpan.Zero
- **WHEN** 读取 `TimeSpan.Zero`
- **THEN** `Zero.TotalNanoseconds == 0`

---

### Requirement: Stopwatch — 单调时钟

#### Scenario: StartNew 立即开始
- **WHEN** `sw = Stopwatch.StartNew()`
- **THEN** `sw.IsRunning == true`

#### Scenario: Elapsed 在 Stop 后稳定
- **WHEN** `sw = Stopwatch.StartNew(); ...do_work...; sw.Stop(); e1 = sw.Elapsed; ...wait...; e2 = sw.Elapsed`
- **THEN** `e1 == e2`（Stop 后 Elapsed 不增长）

#### Scenario: Restart 清零并重启
- **WHEN** `sw = Stopwatch.StartNew(); sw.Stop(); sw.Restart()`
- **THEN** `sw.IsRunning == true` 且后续 Elapsed 从 0 开始累计

#### Scenario: Running 状态下 Elapsed 单调非递减
- **WHEN** 同一 running Stopwatch 连续读两次 `Elapsed`
- **THEN** 第二次 ≥ 第一次

#### Scenario: 默认构造未启动
- **WHEN** `sw = new Stopwatch()`
- **THEN** `sw.IsRunning == false`, `sw.Elapsed == TimeSpan.Zero`

---

### Requirement: VM 原生重命名

#### Scenario: __time_now_mono_ns 替换 __bench_now_ns
- **WHEN** VM corelib 注册表查 `__time_now_mono_ns`
- **THEN** 返回单调 ns 计数（自进程内某 EPOCH 起非递减）
- **WHEN** VM corelib 注册表查 `__bench_now_ns`
- **THEN** 返回 None（旧名已删除；pre-1.0 无 compat）

---

## MODIFIED Requirements

### Requirement: Std.IO.Environment 移除时间 API

**Before:** `Std.IO.Environment.GetCurrentTimeMs()` 暴露 wall-clock UTC ms。

**After:** `GetCurrentTimeMs` 删除。等价能力由 `Std.Time.DateTime.UtcNow.UnixMs` 提供。Pre-1.0 不留 alias。

### Requirement: z42.test.Bencher 使用 z42.time 单调时钟

**Before:** Bencher 内部直接调 `__bench_now_ns` 原生。

**After:** Bencher 通过 `Std.Time.Stopwatch` 测时（或直接调更名后的 `__time_now_mono_ns`，视实施时哪个更轻量）。z42.test 包加 `z42.time` 依赖（若选 Stopwatch 路径）。

---

## IR Mapping

无新增 IR 指令。所有 Time 类型操作走现有 Call / 字段访问 / 算术指令。

VM corelib 注册表仅是 string → fn 映射，新增/重命名条目走现有 `init_corelib_natives` 路径。

## Pipeline Steps

仅 stdlib + 测试代码增量；不动 pipeline 阶段。

- [ ] Lexer / Parser / AST：无变动
- [ ] TypeChecker：无变动（z42.time 类型走 TSIG 跨包导入既有路径）
- [ ] IR Codegen：无变动
- [ ] VM interp：仅 corelib 注册表 `__bench_now_ns` → `__time_now_mono_ns` 一行改名

## Testing Strategy

- 单元测试：每个类型独立 `.z42` 测试文件（`tests/datetime.z42` / `tests/timespan.z42` / `tests/stopwatch.z42`），用 `[z42.test.Test]` 属性，覆盖以上每个 Scenario
- VM golden：无需（spec 行为已被脚本测试覆盖，无新 IR / VM 行为）
- 跨包回归：现有 z42.io console 测试、z42.test bencher 测试改写后仍全绿
