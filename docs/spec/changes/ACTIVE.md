# 活跃变更：子系统占用账本

> 规则见 [`.claude/rules/parallel-development.md`](../../../.claude/rules/parallel-development.md)。
> **一个子系统同一时刻只允许一个 in-flight change 持有。** 开 change（阶段 2）前查此表，被占则排队；归档（阶段 9）后释放。
> `docs` 不上锁（见协议）。

## 子系统持有表

| 子系统 | 当前持有 change | 起始 | 说明 |
|--------|----------------|------|------|
| `compiler` | _（空闲）_ | — | scaffold-z42c-selfhost 已提交 127b7f11（gate 后台确认中），释放 compiler |
| `runtime` | add-reflection-mvp | 2026-06-08 | 反射 MVP：corelib reflection builtins + TypeDesc 句柄化（feat 30776fae 已提交）。（add-binary-float 2026-06-09 / add-file-last-write-time 曾例外共存于 `corelib/convert.rs`+`fs.rs` 新 builtin，均已归档释放）|
| `stdlib` | add-reflection-mvp ‖ add-ipaddress-v4mapped | 2026-06-08 | 反射 MVP：z42.core 扩展 Type + Std.Reflection 类（feat 30776fae 已提交，待归档释放）。**例外共存**：add-ipaddress-v4mapped 仅动 `z42.net`（IPAddress.z42 + test），与反射 `z42.core` 零重叠。（add-toml-key-order / add-uri-ipv6-host / add-binary-float / compression-decoder-pull-mode / add-file-last-write-time / add-directory-copy 均曾共存，已归档释放）|
| `z42c` | …→ port-z42c-semantics✅ → port-z42c-codegen | 2026-06-07 | 自举逐子系统移植（顺序续作，单人）：core✅ → syntax✅ → project（manifest/workspace/路径模板✅）→ **semantics 类型检查半 1A–2B✅（已归档 2026-06-09）** → **port-z42c-codegen 进行中**（Bound→IR：z42c.ir 模型从零镜像 IrModule.cs + FunctionEmitter/IrGen lowering） |
| `toolchain` | port-z42c-core | 2026-06-07 | xtask test compiler-z42 接入 z42-test-runner 跑 z42c [Test] |

## 全部 in-flight change（参考，子系统占用以上表为准）

| change | 子系统（待逐个确认） |
|--------|---------------------|
| scaffold-z42c-selfhost | z42c + compiler（已提交 127b7f11；gate 确认中，归档待绿）|
| port-z42c-core | z42c + toolchain |
| port-z42c-codegen | z42c（2026-06-09 开；semantics 归档释放 z42c 锁后占用）|
| inline-jit-safepoint-check | runtime（暂停，不占锁） |
| investigate-concurrent-gc-stale-mark-race | runtime（暂停，不占锁） |
| migrate-scripts-to-z42 | scripts/ + toolchain（不改 src/libraries/，不占 stdlib 锁）|
| add-z42-wasm-playground | runtime? / toolchain?（待回填） |
| add-reflection-mvp | runtime + stdlib（2026-06-08 登记，feat 30776fae 已提交）|
| add-ipaddress-v4mapped | stdlib（仅 z42.net/IPAddress.z42；纯 z42，与 reflection 例外共存）|
| plan-0.3.x-three-streams | docs（不上锁） |
