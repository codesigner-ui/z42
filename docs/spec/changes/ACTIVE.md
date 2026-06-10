# 活跃变更：子系统占用账本

> 规则见 [`.claude/rules/parallel-development.md`](../../../.claude/rules/parallel-development.md)。
> **一个子系统同一时刻只允许一个 in-flight change 持有。** 开 change（阶段 2）前查此表，被占则排队；归档（阶段 9）后释放。
> `docs` 不上锁（见协议）。

## 子系统持有表

| 子系统 | 当前持有 change | 起始 | 说明 |
|--------|----------------|------|------|
| `compiler` | —（空闲）| — | add-field-attribute-reflection 已归档 2026-06-10 → 释放（字段 attr + zbc 1.14 / zpkg 0.16）|
| `runtime` | —（空闲）| — | add-field-attribute-reflection 已归档 2026-06-10 → 释放（`field_attributes` + `__field_custom_attributes`）|
| `stdlib` | —（空闲）| — | add-field-attribute-reflection 已归档 2026-06-10 → 释放（`FieldInfo.GetCustomAttributes()`）|
| `z42c` | …→ port-z42c-codegen✅ → port-z42c-zbc-writer | 2026-06-07 | 自举逐子系统移植（顺序续作，单人）：core✅ → syntax✅ → project（manifest/workspace/路径模板✅）→ semantics 类型检查半 1A–2B✅ → **port-z42c-codegen✅ Bound→IR 内存模型 CG-1A–2（已归档 2026-06-09，210 cases）** → **port-z42c-zbc-writer 进行中**（byte-identical .zbc：IrModule→bytes，镜像 ZbcWriter.cs + Tokens）<br>⚠️ **协调（2026-06-09，User 裁决，格式已变更）**：C3 attribute 反射已落地并 bump **.zbc → 1.11**（1.10 TYPE-section per-class attr refs + 1.11 SIGS-section per-function attr refs）+ **.zpkg → 0.13**（含 ZpkgWriter global SIGS 同步）→ 本 port 需**按 1.11 新格式重新镜像 ZbcWriter.cs**（接受 re-port，byte-identical gate 在 re-port 完成前暂红）。<br>⚠️ **追加协调（2026-06-10）**：add-reflection-type-flags 再 bump **.zbc → 1.12 / .zpkg → 0.14**（TYPE section 每类追加 `flags:u8`）→ port **直接对齐 1.12**（趁 mid-re-port，同周期不多一轮）。<br>⚠️ **追加协调（2026-06-10）**：add-reflection-static-fields 再 bump **.zbc → 1.13 / .zpkg → 0.15**（TYPE section 每类 flags 后追加静态字段块）→ port 对齐 1.13（同 re-port 周期）。<br>⚠️ **追加协调（2026-06-10）**：add-field-attribute-reflection 再 bump **.zbc → 1.14 / .zpkg → 0.16**（TYPE section 每字段记录追加 attr refs）→ port 对齐 1.14。 |
| `toolchain` | port-z42c-core **+ migrate-xtask-launcher-to-std-cli**（协调共占）| 2026-06-07 / 2026-06-10 | port-z42c-core：xtask test compiler-z42 接入 z42-test-runner（足迹限 `xtask_compiler_z42.z42`，z42c 主线）。<br>⚠️ **协调（2026-06-10，User 授权）**：migrate-xtask-launcher-to-std-cli 共占 toolchain，足迹为 xtask Main/build/package/test/deps/bench dispatch + launcher/apphost —— 与 port-z42c-core 的 `xtask_compiler_z42.z42` 非重叠区域，User 裁决可并行。|

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
| ~~add-attribute-reflection~~ | compiler + runtime + stdlib —— ✅ C3a（class-level）已归档 2026-06-09（feat 56d9cefb + 1377bfdb）|
| ~~add-attribute-reflection-methods~~ | compiler + runtime + stdlib —— ✅ C3b（method-level）已归档 2026-06-09（SIGS attr refs，zbc 1.11/zpkg 0.13）|
| ~~add-apphost~~ | toolchain —— ✅ 已归档 2026-06-09（feat a3720a16；per-app 原生 apphost，framework-dependent + 本地优先 + macOS 重签名）|
| ~~fix-dist-runner-test-dirs~~ | toolchain —— ✅ 已归档 2026-06-09（dist runner 跳过 [Test]-based lib 测试 dir，修 secp256k1 误失败；镜像 GoldenTests.ContainsTestRunnerAttribute）|
| ~~fix-fqn-class-resolution~~ | compiler + stdlib —— ✅ 已归档 2026-06-09（ResolveMemberType namespace-aware FQN→类；移除 C3b FindByType workaround；GoldenTests 1545/1545）|
| ~~add-reflection-properties~~ | runtime + stdlib —— ✅ 已归档 2026-06-09（`Type.GetProperties()` + `PropertyInfo`，纯 runtime 派生自 get_/set_，无 zbc 格式 bump；GoldenTests 1549/1549）|
| ~~add-reflection-type-flags~~ | compiler + runtime + stdlib —— ✅ 已归档 2026-06-10（`Type.IsAbstract`/`IsSealed`；zbc 1.12 / zpkg 0.14，TYPE section flags 字节；cargo 795/0 + GoldenTests 1552/1552）|
| ~~add-cli-nested-subcommands~~ | stdlib —— ✅ 已归档 2026-06-10（`Std.Cli` 嵌套 `AddRouter`/`Resolve`/`CommandResolution`；14 新 [Test]；GREEN 269 文件/22 lib）。② xtask/launcher 迁移（合流 migrate-scripts-to-z42）解锁可开 |
| ~~add-reflection-static-fields~~ | compiler + runtime + stdlib —— ✅ 已归档 2026-06-10（`GetFields()` 含静态 + `FieldInfo.IsStatic`；zbc 1.13 / zpkg 0.15，TYPE section 静态字段块；cargo 797/0 + GoldenTests 1553/1553）|
| migrate-xtask-launcher-to-std-cli | toolchain（2026-06-10 开，与 port-z42c-core 协调共占）；xtask+launcher 迁移 Std.Cli 嵌套 router；package/feature-matrix 提顶层；删 lib 别名；每层 help。消费 add-cli-nested-subcommands |
| ~~add-field-attribute-reflection~~ | compiler + runtime + stdlib —— ✅ 已归档 2026-06-10（字段级用户 attribute 反射 `FieldInfo.GetCustomAttributes()`；zbc 1.14 / zpkg 0.16；cargo 799/0 + GoldenTests 1554/1554；参数 attr = follow-up）|
| plan-0.3.x-three-streams | docs（不上锁） |
