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
- [ ] ⑤ `_sortedStrings`/`_splitWords` 移到 xtask_common（无调用方改动）
- [ ] ③ `_assembleAllLibs` flat 视图抽取（compiler/test/bench）
- [ ] ② `_compilerMembers` accessor（收敛 9 处字面量）
- [ ] ④ `_ensureTestPrereqs` 构建前置收敛 + 命名统一
- [ ] ⑥ 文件拆分（xtask_test_lib / compiler build·test）——碰撞最大，放最后/视情况

## ① compiler e2e 表驱动 ✅
- [x] 1.1 抽 `_e2eOracle(vm, driver, e2eDir, emitLibs, runLibs, name, expectTrap, source)`，8 个 oracle 用例（selfcheck/callcheck/typecheck/divzero/charcheck/trycheck/ifacecheck/sacheck）退化为数据调用（xtask_compiler.z42）
- [x] 1.2 rebuild xtask.zpkg 无编译错误（619463→616751 字节）
- [x] 1.3 `xtask test compiler` e2e 全过（行为不变）：8/8 oracle ✓ + build/import e2e ✓ + 不动点 7/7 gen1==gen2

## ⑤ util 归位
- [ ] 5.1 `_sortedStrings` / `_splitWords` 定义移至 xtask_common.z42（同 namespace，调用方零改动）

## ③ flat 视图抽取
- [ ] 3.1 `_assembleAllLibs(root, profile) → dir`（reset + link stdlib + link 各 member dist）入 xtask_common.z42
- [ ] 3.2 替换 compiler/test/bench 三处手写组装

## ② compiler member accessor
- [ ] 2.1 `_compilerMembers(root)` 入 xtask_common.z42（对称 `_stdlibMembers`）
- [ ] 2.2 替换 9 处字面量

## ④ 构建前置收敛
- [ ] 4.1 `_ensureTestPrereqs(haveTc, rebuild, noBuild)` 收敛 4 处重复（xtask_test.z42）
- [ ] 4.2 统一 `rebuild`/`noBuild` 双否定式命名

## 验证
- [ ] V.1 每个逻辑单元：rebuild xtask.zpkg + 相关 stage 全绿
- [ ] V.2 最终 `xtask test compiler` + `xtask test vm`（受影响 stage）全绿

## 备注
- 验证 harness：`Z42_LIBS=artifacts/build/z42c/alllibs/release z42vm z42c.driver.zpkg --mode interp -- build scripts/xtask.z42.toml --release`（~57s rebuild）→ 跑 `./xtask test <stage>`。
- 碰撞控制：逐文件按路径 stage（永不 `git add <dir>/`），分批提交。
