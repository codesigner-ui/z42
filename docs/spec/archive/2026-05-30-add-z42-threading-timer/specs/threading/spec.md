# Spec: Std.Threading.Timer

## ADDED Requirements

### Requirement: StartPeriodic — 周期回调

`Timer.StartPeriodic(long intervalMs, Action callback)` 创建并启动一个
背景 Timer。Callback 第一次在 `intervalMs` 之后触发，之后每隔
`intervalMs` 重复，直到 `Stop()` 调用。

#### Scenario: 周期 callback 触发多次

- **GIVEN** `int[] fires = new int[1]`
- **WHEN** `var t = Timer.StartPeriodic(50, () => { fires[0] = fires[0] + 1; });`
  + `Thread.Sleep(220)` + `t.Stop()`
- **THEN** `fires[0]` ≥ 3（容忍 OS scheduling 抖动，4 个 50ms 窗口预期 3-4 次）

### Requirement: StartOnce — 单次回调

`Timer.StartOnce(long delayMs, Action callback)` 在 `delayMs` 之后触发
callback 一次，然后自动 Stop。

#### Scenario: 单次触发后 IsRunning 返回 false

- **GIVEN** `int[] fires = new int[1]`
- **WHEN** `var t = Timer.StartOnce(50, () => { fires[0] = 1; });` +
  `Thread.Sleep(150)`
- **THEN** `fires[0] == 1` 且 `t.IsRunning() == false`

#### Scenario: StartOnce 在 fire 前被 Stop

- **GIVEN** `int[] fires = new int[1]`
- **WHEN** `var t = Timer.StartOnce(500, ...);` + 立刻 `t.Stop()` +
  `Thread.Sleep(600)`
- **THEN** `fires[0] == 0`（callback 未触发）

### Requirement: Stop 信号 ≤100ms 响应

无论 `intervalMs` 多大，Stop 信号在 ≤100ms 内被 Timer 线程接收。

#### Scenario: 大 interval Stop 快速生效

- **WHEN** `var t = Timer.StartPeriodic(10000, ...);` + `Thread.Sleep(50)` +
  `t.Stop()` + `t.StopAndJoin()` 测耗时
- **THEN** StopAndJoin 在 ≤200ms 内返回（100ms 颗粒度 + 一个调度抖动）

### Requirement: Stop idempotent

#### Scenario: 多次 Stop 不抛

- **WHEN** `t.Stop(); t.Stop(); t.Stop();`
- **THEN** 不抛异常，IsRunning 返回 false

### Requirement: callback 抛异常被 swallow

不让 callback 一次失败杀掉 Timer 线程；周期 Timer 继续。

#### Scenario: periodic callback 抛 → 下次仍触发

- **GIVEN** `int[] fires = new int[1]`
- **WHEN** `var t = Timer.StartPeriodic(50, () => {
    fires[0] = fires[0] + 1;
    if (fires[0] == 1) { throw new Exception("first fire fails"); }
  });` + `Thread.Sleep(220)` + `t.Stop()`
- **THEN** `fires[0]` ≥ 2（第二次 callback 仍被调用）

### Requirement: StopAndJoin 同步等 callback 当前调用结束

#### Scenario: Stop 时 callback 正在跑 → join 等它

- **GIVEN** `bool[] done = new bool[1]`
- **WHEN** `var t = Timer.StartPeriodic(10, () => {
    Thread.Sleep(200);   // long callback
    done[0] = true;
  });` + `Thread.Sleep(50)` + `t.StopAndJoin()`
- **THEN** `done[0] == true`（StopAndJoin 等 callback 跑完）

### Requirement: IsRunning 反映实时状态

#### Scenario: 启动后 IsRunning true，Stop 后 false

- **WHEN** `var t = Timer.StartPeriodic(100, ...);` 立即查 IsRunning →
  true；`t.Stop(); t.StopAndJoin();` 查 → false

## MODIFIED Requirements

无 — 纯新增。

## IR Mapping

无 — 纯 stdlib，复用既有 `Std.Threading.Thread` + `Thread.Sleep` builtin。

## Pipeline Steps

- [ ] Lexer — N/A
- [ ] Parser / AST — N/A
- [ ] TypeChecker — N/A
- [ ] IR Codegen — N/A
- [ ] VM interp — N/A
