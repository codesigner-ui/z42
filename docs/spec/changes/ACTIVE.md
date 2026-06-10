# 活跃变更：子系统占用账本

> 规则见 [`.claude/rules/parallel-development.md`](../../../.claude/rules/parallel-development.md)。
> **一个子系统同一时刻只允许一个 in-flight change 持有。** 开 change（阶段 2）前查此表，被占则排队；归档（阶段 9）后释放。
> `docs` 不上锁（见协议）。

## 子系统持有表

| 子系统 | 当前持有 change | 起始 | 说明 |
|--------|----------------|------|------|
| `compiler` | —（空闲）| — | add-parameter-attribute-reflection 已归档 2026-06-10 → 释放（`ParameterInfo.GetCustomAttributes()`；zbc 1.15 / zpkg 0.17 SIGS per-param 块；dotnet 1556/1556 + e2e）。⚠️ 暴露 pre-existing 多文件 project-build 命名空间双重限定 bug（非本 change，待独立 fix；见 memory）|
| `runtime` | —（空闲）| — | add-parameter-attribute-reflection 已归档 2026-06-10 → 释放（zbc_reader SIGS per-param + `__param_custom_attributes`；cargo 757+21）|
| `stdlib` | —（空闲）| — | add-parameter-attribute-reflection 已归档 2026-06-10 → 释放（`ParameterInfo.GetCustomAttributes()` / `GetAttribute(Type)`）|
| `z42c` | —（空闲）| — | 自举主线全归档：…→zpkg-build✅→tsig✅→**port-z42c-import✅ 已归档 2026-06-10**（跨包消费链 MVP：z42c 编译 stdlib-using 代码 + hello-stdlib 全文件 byte-identical；gate zpkg 对账 3/3 + import e2e；a1fa39d8）。下一步候选：实例方法 DepIndex 命中链 / char 字面量 / interface·异常·闭包 |
| `toolchain` | port-z42c-core | 2026-06-07 | xtask test compiler-z42 接入 z42-test-runner（足迹限 `xtask_compiler_z42.z42`，z42c 主线）。（migrate-xtask-launcher-to-std-cli 已归档 2026-06-10 释放协调共占。）|

## 全部 in-flight change（参考，子系统占用以上表为准）

| change | 子系统（待逐个确认） |
|--------|---------------------|
| scaffold-z42c-selfhost | z42c + compiler（已提交 127b7f11；gate 确认中，归档待绿）|
| port-z42c-core | z42c + toolchain |
| ~~port-z42c-codegen~~ | z42c —— ✅ 已归档 2026-06-09（CG-1A–2，210 cases）|
| ~~port-z42c-zbc-writer~~ | z42c —— ✅ 已归档 2026-06-10（功能完整 .zbc writer + empty 逐字节 + e2e 四向；DBUG/span 移交 add-z42c-source-spans）|
| ~~add-z42c-source-spans~~ | z42c —— ✅ 已归档 2026-06-10（span→DBUG + byte-compare 3/3；7942ab7d）|
| ~~port-z42c-zpkg-build~~ | z42c —— ✅ 已归档 2026-06-10（z42c build 端到端+直跑；e1ff3503）|
| ~~port-z42c-tsig~~ | z42c —— ✅ 已归档 2026-06-10（zpkg 全文件 byte-identical 2/2；05e615cf）|
| ~~port-z42c-import~~ | z42c —— ✅ 已归档 2026-06-10（hello-stdlib byte-identical + import e2e；a1fa39d8）|
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
| ~~add-cli-optional-positional~~ | stdlib —— ✅ 已归档 2026-06-10（`ArgParser.AddOptionalPositional`，z42.cli；11 新 [Test]）。migrate-xtask 前置 |
| ~~fix-reflection-test-compile~~ | stdlib + docs —— ✅ 已归档 2026-06-10（add-field 遗留 reflection.z42 编译失败：`none` 关键字 + 限定基名 `: Std.Attribute` upcast；改 `noneAttrs` + unqualified `: Attribute`；dotnet test 1554/1554。根因 compiler FQN-upcast bug 入 reflection.md Deferred `attr-factory-qualified-base-upcast`）|
| ~~migrate-xtask-launcher-to-std-cli~~ | toolchain —— ✅ 已归档 2026-06-10（xtask+launcher → Std.Cli 嵌套 router；`package`/`feature-matrix` 提顶层；删 `lib` 别名；每层 help；CI build package→package ×5。xtask GREEN 270/22 + test dist 347/0）|
| ~~add-field-attribute-reflection~~ | compiler + runtime + stdlib —— ✅ 已归档 2026-06-10（字段级用户 attribute 反射 `FieldInfo.GetCustomAttributes()`；zbc 1.14 / zpkg 0.16；cargo 799/0 + GoldenTests 1554/1554；参数 attr = follow-up）|
| ~~add-reflection-value-record-flags~~ | runtime + stdlib —— ✅ 已归档 2026-06-10（`Type.IsValueType` / `Type.IsRecord`，读 type-flags 已捕获的 struct/record 位；**无格式 bump**；cargo 800/0 + GoldenTests 1554/1554）|
| ~~fix-chained-property-dispatch~~ | compiler —— ✅ 已归档 2026-06-10（链式 getter 派发 `obj.GetType().BaseType.Name`；唯一根因 P1=Object stub GetType 返 Unknown→改取真实 Std.Type；纯 typecheck，无 zbc 格式/字节漂移；dotnet GoldenTests 1555/1555 + 链式 e2e 全过）|
| ~~add-parameter-attribute-reflection~~ | compiler + runtime + stdlib + z42c —— ✅ 已归档 2026-06-10（参数级用户 attribute 反射 `ParameterInfo.GetCustomAttributes()`；zbc 1.15 / zpkg 0.17，SIGS 每参数 attr-ref 块；z42c writer 同步；dotnet 1556/1556 + param_attributes.z42 e2e + cargo 757+21 + format 78。xtask gate 阻塞于 pre-existing 多文件 project-build 命名空间双重限定 bug → User 裁决单独 fix change 跟踪，本 change 走 dotnet 权威门）|
| fix-multifile-project-namespace-qualify | compiler（待开；pre-existing bug，见 memory `reference_multifile_project_namespace_double_qualify_bug`）|
| plan-0.3.x-three-streams | docs（不上锁） |
