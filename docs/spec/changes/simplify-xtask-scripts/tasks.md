# Tasks: 简化 xtask 脚本（去重 + 收敛重复模式）

> 状态：🟡 进行中 | 创建：2026-06-30
> 类型：refactor（不改外部行为，纯内部组织）

**变更说明：** xtask 脚本内部去重——表驱动 compiler e2e、抽公共 accessor/组装器、把通用 util 归位。
**原因：** compiler/test 两个大文件（789/764 行）内部存在大量复制粘贴；helper（`_assembleDriverHome`/`_stdlibMembers`）已存在但只局部采用，对称缺口（`_compilerMembers`）导致同一字面量重复 9 处。
**文档影响：** 无（纯内部重构，不改 CLI 命令面 / 外部行为 / 机制约定）。

## ⚠️ 子系统锁覆盖（User 决策 2026-06-30）

`toolchain` 子系统锁当前被 **compile-once-toolchain** 🟡 持有（它本身在重构 xtask，42 任务未完）。
按 `parallel-development.md` 本应排队；**User 明确选择「现在就做，接受冲突」**。
因此**不抢占 ACTIVE.md 的 toolchain 行**（保持 compile-once-toolchain 为名义持有者），
本 change 以「逐文件 surgical 提交 + 独立产物验证」方式并行推进，碰撞由 User 承担。

## 进度概览
- [x] ① compiler e2e 表驱动（单文件，碰撞面最小）— `test compiler` GREEN（exit 0；e2e 8/8 + 不动点 7/7）
- [x] ② `_compilerMembers` accessor（收敛 10 处字面量）— 与③合并提交
- [x] ③ `_assembleAllLibs` flat 视图抽取（compiler/test/bench 3 处）— GREEN
- [x] ⑤ `_sortedStrings`/`_splitWords` 移到 xtask_common（无调用方改动）— GREEN
- [x] ④ 抽 `_regenForTest` + `_buildDebugVmAndCompression`（收敛 testVmCore/testAll 重复）— GREEN（双否定 rebuild/noBuild 改名刻意不做：churn 高价值低）
- [x] ⑥ 文件拆分：⑥A xtask_test_lib.z42（test.z42 740→211）+ ⑥B xtask_compiler_e2e.z42（compiler.z42 735→465，过 500 限）

## ⑥ 文件拆分
- [x] 6A.1 抽 stdlib [Test] harness（_libUnitCount/_shardLibs/_testLib/_testLibCore/TestUnit/DepBundle/_discoverTestUnits/_dirHasTestMethods/_harvestParentDeps/_appendDeps/_runUnitsBatched/_compilePrep/_renderSyntheticManifest）→ xtask_test_lib.z42；_parseShard 留原文件（_testVmCore 共用）
- [x] 6A.2 toml [sources] + 段注释更新；`test stdlib z42.math` GREEN（13/13）
- [x] 6B.1 抽 e2e oracle 块（_e2eOracle + _testCompilerE2e，自包含）→ xtask_compiler_e2e.z42
- [x] 6B.2 toml [sources]；rebuild clean；`test compiler` 验证（见下）
- 备注：xtask_test_lib.z42 538 行略超 500——harness 是高耦合单一系统，进一步拆会割裂；接受。

## ④⑤ 验证 + 环境插曲
- [x] `test vm interp` GREEN：build-stdlib 22/0 + regen 202/0 + goldens 191/0
- [x] `test compiler` GREEN：7/7 zpkg + 17 units + e2e + 不动点 7/7（覆盖 _sortedStrings）
- 插曲：首跑 test vm regen 202/202 全崩 → 诊断为**陈旧 debug z42vm 缺 `__activator_create`**（并行 reflection change dff252e2 新加的 builtin），非 ④⑤。User 授权清理 6 个陈年僵尸进程（PID 7514 周六起卡 build）+ cargo 重建 debug vm → 重跑全绿。一度误诊「skew 种子」实为 hard-link 同 inode + 错误 Z42_LIBS 隔离兄弟包的假象。

## ① compiler e2e 表驱动 ✅
- [x] 1.1 抽 `_e2eOracle(vm, driver, e2eDir, emitLibs, runLibs, name, expectTrap, source)`，8 个 oracle 用例（selfcheck/callcheck/typecheck/divzero/charcheck/trycheck/ifacecheck/sacheck）退化为数据调用（xtask_compiler.z42）
- [x] 1.2 rebuild xtask.zpkg 无编译错误（619463→616751 字节）
- [x] 1.3 `xtask test compiler` e2e 全过（行为不变）：8/8 oracle ✓ + build/import e2e ✓ + 不动点 7/7 gen1==gen2

## ⑤ util 归位
- [ ] 5.1 `_sortedStrings` / `_splitWords` 定义移至 xtask_common.z42（同 namespace，调用方零改动）

## ③ flat 视图抽取 ✅
- [x] 3.1 `_assembleAllLibs(root, profile) → dir`（reset + link stdlib + link 各 member dist）入 xtask_stdlib.z42（与 _compilerMembers/_assembleStdlibFlatView 同处）
- [x] 3.2 替换 compiler(_testCompilerUnits)/test(_testLibCore)/bench 三处手写组装；修 compiler `i` 复用声明

## ② compiler member accessor ✅
- [x] 2.1 `_compilerMembers(root)` 入 xtask_stdlib.z42（对称 `_stdlibList`）
- [x] 2.2 替换 10 处字面量（compiler×3 / test / bench / bootstrap_check / package_desktop×2 / stdlib×2）

## 验证（②③）
- [x] `test compiler` GREEN：7/7 zpkg + 17 units + e2e + 不动点 7/7
- [x] `test stdlib z42.math` GREEN（_assembleAllLibs test-lib 路径）
- 备注：首次 `test compiler` 被并行活动外部中断（exit 144，waiter 同被杀），干净重跑复现 GREEN — 碰撞非代码问题。

## ④ 构建前置收敛
- [ ] 4.1 `_ensureTestPrereqs(haveTc, rebuild, noBuild)` 收敛 4 处重复（xtask_test.z42）
- [ ] 4.2 统一 `rebuild`/`noBuild` 双否定式命名

## 验证
- [ ] V.1 每个逻辑单元：rebuild xtask.zpkg + 相关 stage 全绿
- [ ] V.2 最终 `xtask test compiler` + `xtask test vm`（受影响 stage）全绿

## 备注
- 验证 harness：`Z42_LIBS=artifacts/build/z42c/alllibs/release z42vm z42c.driver.zpkg --mode interp -- build scripts/xtask.z42.toml --release`（~57s rebuild）→ 跑 `./xtask test <stage>`。
- 碰撞控制：逐文件按路径 stage（永不 `git add <dir>/`），分批提交。
