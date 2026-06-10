# Tasks: xtask + launcher 迁移到 Std.Cli

> 状态：🟡 规范就绪 | 创建：2026-06-10
> 子系统：`toolchain`（与 port-z42c-core 协调共占，User 授权；非重叠区域）
> 前置：add-cli-nested-subcommands ✅（已归档）

## 进度概览
- [x] 阶段 0: 准备（manifest 依赖 + 命令树定稿）
- [x] 阶段 1: xtask router 树 + Main 三分支 dispatch（xtask_cli.z42 _runCli/_cliRoot/_dispatch）
- [x] 阶段 2: xtask leaf handlers → ParseResult（package/test*/install/bench；core/wrapper 拆分）
- [x] 阶段 3: launcher + apphost 迁移（launcher_cli.z42 _runLauncher/_launcherRoot；handlers→ParseResult；Apphost.BuildOne）
- [x] 阶段 4: CI（build package→package ×5）+ docs（test lib→stdlib + workflow build package→package 命令形）联动
- [x] 阶段 5: 全验证 —— xtask 完整 GREEN gate 270/22；launcher 直接 smoke（每层 help/未知/list）+ **test dist 347/0**（fresh package：launcher smoke ✓ apphost smoke ✓ goldens 全绿）

> 状态：🟢 已完成 2026-06-10

## 阶段 0: 准备
- [ ] 0.1 确认 `scripts/xtask.z42.toml` 含 `z42.cli` 依赖（无则加）
- [ ] 0.2 确认 `src/toolchain/launcher/core/launcher.z42.toml` 含 `z42.cli` 依赖（大概率新增）
- [ ] 0.3 建树辅助：决定 router 树构造放 `xtask.z42` 内还是抽 `xtask_cli.z42`（按行数软限）

## 阶段 1: xtask router 树 + dispatch
- [ ] 1.1 `xtask.z42`：构造 root `SubcommandRouter`（build 子 router + package/feature-matrix/test/deps/bench/regen/audit/clean/run leaves），各 leaf 声明 ArgParser
- [ ] 1.2 `xtask.z42` Main：`Resolve` 三分支（IsHelp/IsUnknown/IsMatch）+ `_dispatch(path, result)`
- [ ] 1.3 `test` 混合 shim（空/leading-`-` → testAll；否则 Resolve）
- [ ] 1.4 `bench` 混合 shim（`stdlib` 子命令 vs 默认 leaf flags）
- [ ] 1.5 删手写 `_help()`；保留 `_ensureDriverVm()` 调用时机

## 阶段 2: xtask leaf handlers → ParseResult
- [ ] 2.1 `xtask_package.z42` `_buildPackage(ParseResult)`：`--rid`/`--variant`/profile
- [ ] 2.2 `xtask_test.z42` `_testVm`/`_testLib`/`_testCrossZpkg`/`_testDist`：mode positional + `--no-rebuild`/`--jobs`
- [ ] 2.3 `xtask_test_changed.z42` `_testChanged`：`--dry-run` + base positional + env 后备
- [ ] 2.4 `xtask_install.z42` `_depsInstall`：`--os`/`--check`/`--drift`/`--print-env`/`--force` + `node`/`android-emulator`
- [ ] 2.5 `xtask_bench.z42` `_bench`/`_benchDiff`：`--quick`/`--diff`/`--current`/`--baseline`/`--threshold-*`/`--quiet`

## 阶段 3: launcher + apphost
- [ ] 3.1 `launcher.z42`：root router（info/list/default/link/which/run/install/uninstall + apphost 子 router）+ 各 ArgParser
- [ ] 3.2 `launcher.z42` Main：apphost 简写捷径（`.zpkg`/`.zbc`）前置 → 否则 Resolve 三分支；删手写 `_help()`
- [ ] 3.3 `_cmdRun`：`--` app-args 切分 + `--runtime` + runtimeconfig.json（行为不变）
- [ ] 3.4 `apphost.z42` `Apphost.Build`：`build` 子命令 + `--out` + `<app|toml>` positional；`apphost build -h`

## 阶段 4: CI + docs
- [ ] 4.1 `.github/workflows/ci.yml`：`build package` → `package`（270/576/678/776）
- [ ] 4.2 `.github/workflows/release.yml`：`build package` → `package`（151）
- [ ] 4.3 `docs/workflow/ci.md` + `docs/workflow/testing/changed-only.md`：`test lib` → `test stdlib`
- [ ] 4.4 toolchain CLI doc（`docs/design/compiler/project.md` 或相应）：新命令树（package/feature-matrix 顶层）
- [ ] 4.5 `src/toolchain/launcher/core/README.md` + （若有）xtask 命令清单 doc 同步

## 阶段 5: 验证
- [ ] 5.1 兼容向量逐条（spec Behavior Invariants 表）经 fresh vm 旁路本地跑，断言行为不变
- [ ] 5.2 每层 help：`xtask -h` / `build -h` / `package -h` / `test -h` / `z42 run -h` / `apphost build -h`
- [ ] 5.3 未知：`xtask bogus` / `build bogus` / `package --no-such-flag` → 报错+help+退出码
- [ ] 5.4 完整 GREEN gate `xtask test`（经迁移后 xtask 自身跑）—— compiler+vm+cross-zpkg+stdlib 全绿
- [ ] 5.5 重建 xtask.zpkg 供 CI（提交前确保 `artifacts/xtask/xtask.zpkg` 是迁移后版本；注：repo-root apphost vm 陈旧不影响 zpkg 本身）
- [ ] 5.6 docs 同步确认

## 备注
- apphost bundled vm 重建（让 `./xtask` 恢复可用）**不在本变更**——属版本 bump 联动/工具链维护。
- 混合节点 shim（test/bench 默认动作）记为已知消费端例外；若未来普遍需要 → 单开 stdlib 变更给 Std.Cli 加"默认 leaf"。
