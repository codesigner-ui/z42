# Proposal: compile-once toolchain（共享 host SDK + 显式 SDK/Current 边界）

> 状态：DRAFT（前置：无硬性依赖；与已完成的 dotnet 移除衔接）
> 子系统：`toolchain`（xtask）+ `runtime`（仅当 format 兜底选 B：zbc reader）+ docs
> 操作流程基准：[`docs/workflow/bootstrap-and-testing.md`](../../../workflow/bootstrap-and-testing.md)

## Why

dotnet 彻底移除、z42c 自举后（2026-06-26），CI 的自举/测试流程暴露大量冗余与一处严密性
开口：

1. **z42c+stdlib+xtask 被独立全量编译 ~16 次**：测试 job（build-and-test×4 / vm-jit×4 /
   stdlib-jit×4 / compiler-z42-stdlib）各自跑 `ci-bootstrap-nocs.sh`（~12min/job），而
   `toolchain-bootstrap` 已把同样的产物上传成 artifact——只有 package/platform job 消费它。
2. **build-wave 二次重建**：即使 bootstrap 建好，`test all` 的 `_regenCore` 又把 z42c+stdlib+
   goldens 重建一遍（~17min，build-and-test 最大块）。
3. **SDK/Current 边界不显式**：每个 job 混着"下载 SDK 编当前"与"当前重编当前"，没有"一处编出
   Current、全下游复用"的单一真相。改 z42c/toolchain 时，被测/被发布的 Current 与 fixpoint
   验证分散在不同 job。
4. **🔴 format-bump 死锁**：`zbc_reader.rs` 精确匹配 major+minor（拒 older minor）。删 C# 后无
   逃生口——下次 zbc/zpkg 格式 bump，新 z42vm 读不了旧 SDK → 全 bootstrap 断 → publish-nightly
   发不出新格式 nightly → 死锁。
5. **fixpoint 不 gate 发布**：fixpoint（gen1==gen2）只在 `bootstrap-no-csharp` 验，publish-nightly
   的 needs 不含它 → 理论上能发出未验自洽性的 nightly。
6. **scripts/ 多个 bootstrap shell CLI**：`ci-bootstrap-nocs.sh` + `bootstrap-no-csharp.sh` 重复，
   `ci-stage-toolchain.sh` 可折进 xtask，`check-bootstrap-compat.sh` 本地工具。

不做的代价：CI 关键路径停在 ~40min（含大量重复编译），且 format bump 会突然死锁、无自动恢复。

## What Changes

把"共享 host SDK、编一次"做成显式架构（详见 design.md）：

1. **xtask `--toolchain <dir>`**：build/test 命令据此定位 z42c+stdlib+xtask（`.z42` 布局）；
   新增 `xtask build sdk --out <dir>` 输出 Current toolchain 成 `.z42` 布局。
2. **单一 `compile` job**：内联 host setup 3 步（cargo z42vm + 下载 SDK→`.z42/` + SDK 编 xtask +
   `xtask --toolchain .z42 build sdk → artifacts/.z42`）+ fixpoint 交叉验证（gen1==gen2，保留 SDK）+
   编 goldens.zbc/test-units.zbc → 上传 artifact。
3. **下游 job 消费 artifact**：test-interp / test-jit / package 全部下载 `artifacts/.z42` +
   `cargo z42vm` + `--toolchain artifacts/.z42 ... --no-build`，不再各自 bootstrap。
4. **format-bump 兜底**（A/B/C 待裁决，见 design.md Decision 2）。
5. **重命名 + 删冗余 job**：build-and-test→`test-interp`、vm-jit+stdlib-jit→`test-jit`；删
   `bootstrap-no-csharp`（fixpoint 进 compile job）+ 评估删 `compiler-z42-stdlib`。
6. **脚本清理**：删 `ci-bootstrap-nocs.sh`/`bootstrap-no-csharp.sh`（内联进 compile job）+
   `ci-stage-toolchain.sh`（折进 xtask）+ `check-bootstrap-compat.sh`（边界由 compile job 隐式强制）；
   **保留 `install-z42.sh`**。
7. **文档**：`bootstrap-and-testing.md` 随各 Phase 落地更新；同步 self-hosting.md/ci.md。

## Scope（允许改动的文件）

| 文件 | 变更 | 说明 |
|------|------|------|
| `scripts/xtask_cli.z42` | MODIFY | 加 `--toolchain` 选项 + `build sdk` 子命令 |
| `scripts/xtask_test.z42` | MODIFY | build/test 据 `--toolchain` 定位工具链；`--no-build` 全链 |
| `scripts/xtask_test_vm.z42` | MODIFY | 同上（vm goldens 定位）|
| `scripts/xtask_stdlib.z42` | MODIFY | `build sdk` 输出 `.z42` 布局；`--toolchain` |
| `scripts/xtask_compiler_z42.z42` | MODIFY | `--toolchain` 定位 z42c；fixpoint helper |
| `scripts/xtask_common.z42` | MODIFY | toolchain-dir 解析 helper |
| `scripts/xtask_package*.z42` | MODIFY | 消费 `--toolchain artifacts/.z42` |
| `scripts/ci-bootstrap-nocs.sh` | DELETE | 逻辑内联进 compile job |
| `scripts/bootstrap-no-csharp.sh` | DELETE | 同上 + fixpoint 进 compile job |
| `scripts/ci-stage-toolchain.sh` | DELETE | 折进 `xtask build sdk` |
| `scripts/check-bootstrap-compat.sh` | DELETE | 边界由 compile job 隐式强制 |
| `.github/workflows/ci.yml` | MODIFY | compile job + 下游消费 + 重命名 + 删冗余 job |
| `.github/workflows/release.yml` | MODIFY | release 也消费 compile 产物（或同构内联）|
| `.github/workflows/bench-pr.yml` / `bench-update.yml` | MODIFY | 消费 artifact |
| `.github/actions/xtask-bootstrap-artifact/action.yml` | MODIFY | 改为 `--toolchain` 消费 `.z42` |
| `src/runtime/src/metadata/zbc_reader.rs` | MODIFY | **仅当 format 兜底选 B**（过渡窗口）|
| `docs/workflow/bootstrap-and-testing.md` | MODIFY | 随 Phase 更新现状 |
| `docs/workflow/ci.md` / `docs/design/compiler/self-hosting.md` | MODIFY | 同步流程 |

**只读引用**：`docs/design/compiler/self-hosting.md`（设计原理）、`.claude/rules/bootstrap-seed.md`、
`.claude/rules/version-bumping.md`。

## Out of Scope

- 改变 z42c/stdlib 的**功能**（纯流程/CI 重构）。
- jit 分片机制（`--shard` 已落地 95e9facf，本变更复用不改）。
- nightly 发布格式 / 包结构（compile job 产物布局除外）。

## Open Questions

- [x] **Decision 2（format-bump 兜底 A/B/C）** —— 🟢 User 裁决 2026-06-27：**第一版不做兜底**，延后到未来 format bump 变更里同步落地（倾向 A committed seed）。§5.3 死锁仍是已知开口。
- [ ] `compiler-z42-stdlib` 是否确认删（覆盖是否被 compile job + test 阶段完全包含）—— 留待 P4 实施时确认。

> 状态：spec 定稿存档；User 2026-06-27 决定**先归档 remove-dotnet-from-builds**、本 change 暂不开工，实施留待后续。
