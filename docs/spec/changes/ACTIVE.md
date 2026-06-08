# 活跃变更：子系统占用账本

> 规则见 [`.claude/rules/parallel-development.md`](../../../.claude/rules/parallel-development.md)。
> **一个子系统同一时刻只允许一个 in-flight change 持有。** 开 change（阶段 2）前查此表，被占则排队；归档（阶段 9）后释放。
> `docs` 不上锁（见协议）。

## 子系统持有表

| 子系统 | 当前持有 change | 起始 | 说明 |
|--------|----------------|------|------|
| `compiler` | _（空闲）_ | — | scaffold-z42c-selfhost 已提交 127b7f11（gate 后台确认中），释放 compiler |
| `runtime` | add-reflection-mvp | 2026-06-08 | 反射 MVP：corelib reflection builtins + TypeDesc 句柄化。（add-file-last-write-time 2026-06-09 曾例外共存：`corelib/fs.rs` 新 builtin + `mod.rs` fs 区一行，已提交 262a163e 归档释放）|
| `stdlib` | add-reflection-mvp | 2026-06-08 | 反射 MVP：z42.core 扩展 Type + Std.Reflection 类。（compression-decoder-pull-mode 2026-06-09 / add-file-last-write-time / add-directory-copy 均曾例外共存于非 z42.core 的 stdlib 库，已归档释放）|
| `z42c` | …→ port-z42c-project → port-z42c-semantics | 2026-06-07 | 自举逐子系统移植（顺序续作，单人）：core✅ → syntax✅ → project（manifest/workspace/路径模板✅）→ semantics 进行中（设计✅ + 1A-1 Z42Type✅） |
| `toolchain` | port-z42c-core | 2026-06-07 | xtask test compiler-z42 接入 z42-test-runner 跑 z42c [Test] |

## 全部 in-flight change（参考，子系统占用以上表为准）

| change | 子系统（待逐个确认） |
|--------|---------------------|
| scaffold-z42c-selfhost | z42c + compiler（已提交 127b7f11；gate 确认中，归档待绿）|
| port-z42c-core | z42c + toolchain |
| inline-jit-safepoint-check | runtime（暂停，不占锁） |
| investigate-concurrent-gc-stale-mark-race | runtime（暂停，不占锁） |
| migrate-scripts-to-z42 | scripts/ + toolchain（不改 src/libraries/，不占 stdlib 锁）|
| add-z42-wasm-playground | runtime? / toolchain?（待回填） |
| add-reflection-mvp | runtime + stdlib（2026-06-08 登记）|
| plan-0.3.x-three-streams | docs（不上锁） |
