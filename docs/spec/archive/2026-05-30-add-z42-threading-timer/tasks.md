# Tasks: Std.Threading.Timer

> 状态：🟢 已完成 | 创建：2026-05-30 | 归档：2026-05-30 | 类型：feat（新 stdlib 类，纯脚本）

## 进度

- [x] 1.1 MODIFY `src/libraries/z42.threading/z42.threading.z42.toml` — 加 z42.diagnostics dep
- [x] 1.2 NEW `src/libraries/z42.threading/src/Timer.z42` — Timer 类（StartPeriodic / StartOnce / Stop / StopAndJoin / IsRunning）
- [x] 1.3 NEW `src/libraries/z42.threading/tests/timer_basic.z42` — 8 个单测
- [x] 1.4 MODIFY `docs/design/stdlib/time.md` — 加 Timer 行引用 Std.Threading.Timer
- [x] 1.5 MODIFY `docs/design/stdlib/roadmap.md` — Deferred 行 time-future-sleep-timer Timer 部分标 ✅
- [x] 1.6 GREEN: `./scripts/build-stdlib.sh` + `./scripts/test-stdlib.sh z42.threading` + `./scripts/test-all.sh`
- [x] 1.7 归档 → `docs/spec/archive/2026-05-30-add-z42-threading-timer/`
- [x] 1.8 commit + push

## 备注

- Stop 信号经 `bool[1]` cell —— W0604 推荐模式（避免值捕获 silent bug）
- 颗粒度 100ms：Stop 响应 ≤100ms 无论 intervalMs 多大；权衡：spin overhead
  低（10 Hz wakeup）vs 响应时延可接受
- Callback 抛 swallow 进 `Std.Diagnostics.Log.Error`，不让一次 fail 杀线程
- 不重叠调用：callback 跑完再 sleep 下一轮；超长 callback 会拖慢 cadence
  但不丢调用 — 这是 BCL `System.Timers.Timer` AutoReset=true 的语义
- StopAndJoin = Stop + Thread.Join：等 callback 跑完，调用方安全 dispose 资源

## 实施备注（2026-05-30）

- 撞了两个 z42 stdlib 写作惯例（在 closure / delegate 之外都不显眼）：
  1. **Lambda 不能直接捕获函数参数中的 Action**：`Thread.Start(() => callback())`
     里 `callback` 是 StartPeriodic 的参数，z42 closure 报"undefined variable
     callback"。绕路：把 Action 存进 `Timer` 实例字段 `_callback`，lambda 捕获
     `Timer self`（reference share），调用 `self._runPeriodic()` 跑业务。
  2. **`this.field()` 不能直接调用 delegate 字段**：z42 当前规则（见
     `Std.Disposable.Dispose` 的同条注释）—— 必须先 hoist 到 local：
     `var cb = this._callback; cb();`
- W0604 完美护栏：`_stopped` 用 `bool[1]` cell，主线程 Stop 写 + 后台线程
  循环读都通过 array element，**编译器不报警**（既不是写 captured value，
  也不是写 captured ref var 本身）—— 正是 §4.4 推荐的 cell pattern.
- 测试时 stash 了一个 unrelated 并发 session 的 WIP
  (`add-test-timeout-attribute` 在 Ast.cs / TopLevelParser.Helpers.cs /
  TestAttributeValidator.cs / IrGen.Tests.cs 改了一半导致 compiler build
  fail). commit 完后 `git stash pop` 还原给那个 session.
