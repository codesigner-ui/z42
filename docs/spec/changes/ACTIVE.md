# 活跃变更：子系统占用账本

> 规则见 [`.claude/rules/parallel-development.md`](../../../.claude/rules/parallel-development.md)。
> **一个子系统同一时刻只允许一个 in-flight change 持有。** 开 change（阶段 2）前查此表，被占则排队；归档（阶段 9）后释放。
> `docs` 不上锁（见协议）。

## 子系统持有表

| 子系统 | 当前持有 change | 起始 | 说明 |
|--------|----------------|------|------|
| `compiler` | add-attribute-reflection | 2026-06-09 | C3 用户自定义 attribute + 反射：parser（class/method 上 `[Name(...)]` 应用）+ typecheck（attribute 类校验 + ctor 解析 + const arg）+ codegen（工厂 thunk 合成 + 元数据 emit）|
| `runtime` | add-attribute-reflection | 2026-06-09 | C3：attribute 元数据载入 TypeDesc/Function + GetCustomAttributes/GetAttribute builtins（调 thunk + 缓存）|
| `stdlib` | add-attribute-reflection | 2026-06-09 | C3：z42.core 加 `Std.Attribute` 基类 + Type/MethodInfo.GetCustomAttributes/GetAttribute |
| `z42c` | …→ port-z42c-codegen✅ → port-z42c-zbc-writer | 2026-06-07 | 自举逐子系统移植（顺序续作，单人）：core✅ → syntax✅ → project（manifest/workspace/路径模板✅）→ semantics 类型检查半 1A–2B✅ → **port-z42c-codegen✅ Bound→IR 内存模型 CG-1A–2（已归档 2026-06-09，210 cases）** → **port-z42c-zbc-writer 进行中**（byte-identical .zbc：IrModule→bytes，镜像 ZbcWriter.cs + Tokens）<br>⚠️ **协调（2026-06-09，User 裁决）**：`add-attribute-reflection`(C3) 会改 .zbc/.zpkg 元数据格式（attribute 持久化 + VersionMinor bump）→ 本 port 需在 C3 落地后**按新格式重新镜像 ZbcWriter.cs**（接受 re-port，byte-identical gate 暂红）。 |
| `toolchain` | port-z42c-core | 2026-06-07 | xtask test compiler-z42 接入 z42-test-runner 跑 z42c [Test] |

## 全部 in-flight change（参考，子系统占用以上表为准）

| change | 子系统（待逐个确认） |
|--------|---------------------|
| scaffold-z42c-selfhost | z42c + compiler（已提交 127b7f11；gate 确认中，归档待绿）|
| port-z42c-core | z42c + toolchain |
| ~~port-z42c-codegen~~ | z42c —— ✅ 已归档 2026-06-09（CG-1A–2，210 cases）|
| port-z42c-zbc-writer | z42c（2026-06-09 开；codegen 归档释放 z42c 锁后占用）|
| inline-jit-safepoint-check | runtime（暂停，不占锁） |
| investigate-concurrent-gc-stale-mark-race | runtime（暂停，不占锁） |
| migrate-scripts-to-z42 | scripts/ + toolchain（不改 src/libraries/，不占 stdlib 锁）|
| add-z42-wasm-playground | runtime? / toolchain?（待回填） |
| ~~add-reflection-mvp~~ | runtime + stdlib —— ✅ 已归档 2026-06-09（feat 30776fae）|
| ~~make-typeof-return-type~~ | compiler + runtime —— ✅ 已归档 2026-06-09（C2，option A）|
| add-attribute-reflection | compiler + runtime + stdlib（C3，进行中：spec 起草）|
| add-apphost | toolchain（**排队**：spec 已就绪，等 `port-z42c-core` 归档释放 toolchain 锁后占用开工；docs 阶段不上锁）|
| plan-0.3.x-three-streams | docs（不上锁） |
