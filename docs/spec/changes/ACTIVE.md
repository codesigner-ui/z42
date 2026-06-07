# 活跃变更：子系统占用账本

> 规则见 [`.claude/rules/parallel-development.md`](../../../.claude/rules/parallel-development.md)。
> **一个子系统同一时刻只允许一个 in-flight change 持有。** 开 change（阶段 2）前查此表，被占则排队；归档（阶段 9）后释放。
> `docs` 不上锁（见协议）。

## 子系统持有表

| 子系统 | 当前持有 change | 起始 | 说明 |
|--------|----------------|------|------|
| `compiler` | _（空闲）_ | — | scaffold-z42c-selfhost 已提交 127b7f11（gate 后台确认中），释放 compiler |
| `runtime` | _（空闲）_ | — | 下列两个 runtime change 已暂停，不占锁 |
| `stdlib` | _（待登记）_ | — | migrate-scripts-to-z42 可能占用，下次触碰时回填 |
| `z42c` | port-z42c-core | 2026-06-07 | z42c.core 真实移植；B0 已提交，z42c 顺序续作 |
| `toolchain` | port-z42c-core | 2026-06-07 | xtask test compiler-z42 接入 z42-test-runner 跑 z42c [Test] |

## 全部 in-flight change（参考，子系统占用以上表为准）

| change | 子系统（待逐个确认） |
|--------|---------------------|
| scaffold-z42c-selfhost | z42c + compiler（已提交 127b7f11；gate 确认中，归档待绿）|
| port-z42c-core | z42c + toolchain |
| inline-jit-safepoint-check | runtime（暂停，不占锁） |
| investigate-concurrent-gc-stale-mark-race | runtime（暂停，不占锁） |
| migrate-scripts-to-z42 | stdlib?（待回填） |
| add-z42-wasm-playground | runtime? / toolchain?（待回填） |
| plan-0.3.x-three-streams | docs（不上锁） |
