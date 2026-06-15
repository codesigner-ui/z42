# Proposal: 跨平台测试流程框架（wasm / iOS / Android）

## Why

三平台（wasm/iOS/Android）的 build/test 能力在 2026-05 已分别建成，但是**三套各自为政的
bash 脚本**：①构建工程 与 ②构建测试资产 在 `build.sh` 里混成一团；②的核心逻辑（编
fixture→.zbc + 收 stdlib zpkg）在三个脚本里各抄一遍（最易漂移）；未接入统一 `z42
xtask.zpkg test` gate；与正在进行的 migrate-scripts-to-z42（bash→z42）方向不一致。

把三平台统一到一个 **接口驱动的 z42 框架** 下，三步（构建工程 / 构建测试资产 / 跑测试）
清晰可单独调用，共享部分只写一份。

## What Changes

- 新增共享框架 `xtask_test_platform.z42`：`IPlatformBackend` 接口 + 三步驱动 + 注册表 + 共享的"②构建测试资产"逻辑（编 fixtures + 收 stdlib，落点参数化）
- 新增三平台后端：`xtask_test_wasm.z42` / `xtask_test_ios.z42` / `xtask_test_android.z42`，各实现 `IPlatformBackend`
- 新 CLI：`test platform <wasm|ios|android> [build|assets|run]`（无子命令=全流程）；`test platform all`
- 现有 `platforms/*/build.sh` + `test.sh` **保留不删**（migrate 节奏：z42 版本地验证匹配 → CI-proven 才删，另开 change）
- R1–R7 契约真相源落到 `docs/design/runtime/cross-platform.md`

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `scripts/xtask_test_platform.z42` | NEW | 共享框架：接口 + 驱动 + 注册表 + 共享 assets |
| `scripts/xtask_test_wasm.z42` | NEW | WasmBackend |
| `scripts/xtask_test_ios.z42` | NEW | IosBackend |
| `scripts/xtask_test_android.z42` | NEW | AndroidBackend |
| `scripts/xtask.z42.toml` | MODIFY | [sources] 注册 4 个新文件 |
| `scripts/xtask_cli.z42` | MODIFY | `_testRouter` 加 `platform` 子路由 + `_dispatchTest` 分派 |
| `docs/design/runtime/cross-platform.md` | MODIFY | R1–R7 契约真相源 + 框架实现原理 |
| `docs/spec/changes/ACTIVE.md` | MODIFY | 登记/释放 toolchain 持有 |

**只读引用：**
- `src/toolchain/host/platforms/{wasm,ios,android}/{build,test}.sh` — 移植源（端到端语义对照），本 change 不改
- `scripts/xtask_common.z42`（_exec/_execIn/Process）、`xtask_versions.z42`（_vRead/_vgetList）、`xtask_golden.z42`（fixture 编译惯用法）、`scripts/xtask_test_vm.z42`（stage 文件惯例）
- `src/libraries/z42.cli/src/SubcommandRouter.z42`（AddRouter/Add/Resolve）

## Out of Scope
- 删除 `platforms/*/build.sh` / `test.sh`（CI-proven 后另开 change）
- 接入 CI test job（emulator/simulator/playwright runner）——下一步独立 change
- 把三平台 R1–R7 测试代码（Playwright/XCTest/JUnit）合并（跨语言不可合）

## 并行说明
占 `toolchain`（scripts/xtask_*）。⚠️ migrate-scripts-to-z42（packaging 脚本）+ port-z42c-core 同子系统在飞 → User 2026-06-15 已多次明确授权 toolchain 并行，接受冲突风险；本 change 主要动**新文件** + `xtask_cli.z42`/`xtask.z42.toml`，与 packaging 脚本不重叠。
