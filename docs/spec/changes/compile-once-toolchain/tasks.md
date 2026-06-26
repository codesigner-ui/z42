# Tasks: compile-once toolchain

> 状态：🟡 DRAFT（spec 留待后续实施；User 2026-06-27 决定先归档 remove-dotnet，本 change 暂不开工）| 创建：2026-06-27
> 子系统占用：`toolchain`（xtask）+ docs（**不含 runtime**——format 兜底已延后，第一版不改 reader）
> 🟢 Decision 2 已裁决（2026-06-27）：**第一版不做 format 兜底**，延后到未来 format bump 变更里同步落地（见 design.md）。

## 进度概览
- [ ] **P0** spec 就绪 + User 确认（阶段 6.5 gate）
- [ ] **P1** xtask `--toolchain <dir>` + `build sdk`（地基，不依赖任何裁决）
- [ ] **P2** compile job：内联 S0–S6 + 条件不动点门（gen1==gen2 跳过 gen3）+ goldens/units + 上传 current-sdk
- [ ] **P3** 下游 job 消费 artifact（test-interp / test-jit / host-package / package-* / platform）
- [ ] **P4** cross-bootstrap：改造 bootstrap-no-csharp（种子换本地 SDK，重跑 S2+S3+S5）+ 进 publish needs
- [ ] **P5** 重命名 + 删冗余 job + 删脚本（仅留 install-z42.sh）

---

## P0：spec 就绪
- [ ] 0.1 proposal.md / design.md / specs/bootstrap-toolchain/spec.md / tasks.md 四件齐（本目录）
- [ ] 0.2 Decision 2（format 兜底）展示给 User，等裁决（推荐 A committed seed）
- [ ] 0.3 阶段 6.5 确认 gate 通过（User 明确"可以开始"）

## P1：xtask `--toolchain` + `build sdk`（toolchain 子系统）
- [ ] 1.1 `scripts/xtask_common.z42`：toolchain-dir 解析 helper（默认 `artifacts/.z42`；校验 `programs/z42c/*.zpkg` 存在，缺失明确报错）
- [ ] 1.2 `scripts/xtask_cli.z42`：注册 `--toolchain <dir>` 全局选项 + `build sdk [--out <dir>]` 子命令
- [ ] 1.3 `scripts/xtask_stdlib.z42`：`build sdk` 组装 `.z42` 布局（gen2 编 stdlib + toolchain；复用 package 的 SDK 组装逻辑，无 apphost trampoline）
- [ ] 1.4 `scripts/xtask_compiler_z42.z42`：z42c 定位据 `--toolchain`；**条件不动点 helper**——SDK→gen1→gen2，比较 gen1 vs gen2：等则跳过 gen3，不等则编 gen3 断言 gen2==gen3（逐字节 mod BLID）
- [ ] 1.5 `scripts/xtask_test.z42` / `xtask_test_vm.z42`：build/test 定位据 `--toolchain`；确认 `--no-build` 全链（vm/stdlib/cross-zpkg）尊重预编译 .zbc
- [ ] 1.6 单元/本地验证：`build sdk --out artifacts/.z42` 后 `--toolchain .z42 test` 与 `--toolchain artifacts/.z42 test` 两套等价全绿
- [ ] 1.7 commit + CI 验证（P1 不改 ci.yml 拓扑，仅 xtask 能力）

## P2：compile job（toolchain + ci.yml）
- [x] 2.1 ~~format 兜底落地~~ —— 🟢 Decision 2 裁决：**第一版不做**，延后到未来 format bump 变更同步落地（§5.3 死锁仍为已知开口）。
- [ ] 2.2 `.github/workflows/ci.yml`：新增 `compile` job（linux），内联 S0–S7：
      S0 cargo z42vm → S1 下载 SDK→`.z42/` → S2 SDK 编 xtask → S3 SDK 编解析用 stdlib → S4 SDK 编 gen1（解析=S3）→ S5 gen1 编 gen2 + 条件不动点门 → S6 gen2 编发布 stdlib(当前格式)+toolchain→`artifacts/.z42` → S7 regen goldens + 编 units
- [ ] 2.3 条件不动点作为上传 gate：gen1≠gen2 时编 gen3，gen2≠gen3 → job 失败、不上传
- [ ] 2.4 上传 artifact `current-sdk`（`artifacts/.z42` + goldens.zbc + units.zbc）
- [ ] 2.5 dotnet PATH-mask 一行保留在 compile job（防意外引入）
- [ ] 2.6 commit + CI 验证 compile job 绿 + artifact 产出正确

## P3：下游消费 artifact（ci.yml + action + bench/release）
- [ ] 3.1 `.github/actions/xtask-bootstrap-artifact/action.yml`：改为下载 `current-sdk` + `--toolchain artifacts/.z42` 消费
- [ ] 3.2 test 腿（build-and-test→消费 artifact）：下载 + cargo z42vm + `--toolchain artifacts/.z42 test --no-build`（interp）
- [ ] 3.3 jit 腿：同上 + jit + `--shard k/4`（复用 95e9facf 分片，不改机制）
- [ ] 3.4 host-package / package-{ios,android,wasm} / platform-test：全部消费 artifact
- [ ] 3.5 `bench-pr.yml` / `bench-update.yml` / `release.yml`：消费 artifact（或同构内联 compile）
- [ ] 3.6 commit + CI 验证全下游绿 + 关键路径时长下降（目标 ~52→~25-30min）

## P4：cross-bootstrap 交叉验证（改造 bootstrap-no-csharp）
- [ ] 4.1 `scripts/bootstrap-no-csharp.sh` 改造：种子来源从"下载上一版 nightly"换成"**本地 SDK 的打包发布形态**"（消费 P2 的 current-sdk artifact / 或本地 `build package` 产物）
- [ ] 4.2 重跑 S2+S3+S5：本地 SDK 编 xtask（验编成功）+ 编 z42c（逐字节==自带 = gen2==gen3）+ 编 stdlib（逐字节==发布）
- [ ] 4.3 该 job 进 `publish-nightly` 的 needs：不自洽 → 不发布
- [ ] 4.4 commit + CI 验证 cross-bootstrap 绿 + gate 生效（故意改 z42c 制造 gen2≠gen3 验证拦截）

## P5：重命名 + 评估删 job + 删脚本（ci.yml + scripts/）
- [ ] 5.1 重命名 `build-and-test` → `test-interp`、`vm-jit-consistency`+`stdlib-jit-consistency` → `test-jit`
- [ ] 5.2 评估删 `compiler-z42-stdlib` job（确认覆盖已被 compile job + test 阶段完全包含；Open Question）
- [ ] 5.3 删 `scripts/ci-bootstrap-nocs.sh`（逻辑已内联 compile job）
- [ ] 5.4 删 `scripts/ci-stage-toolchain.sh`（折进 `xtask build sdk`）+ `scripts/check-bootstrap-compat.sh`（边界由 compile job 隐式强制）
- [ ] 5.5 **保留 `scripts/install-z42.sh`**（cold-start primer）+ `scripts/bootstrap-no-csharp.sh`（已改造为 cross-bootstrap，不删）
- [ ] 5.6 commit + CI 全绿

## 文档同步（贯穿各 P，归档前必完成）
- [ ] D.1 `docs/workflow/bootstrap-and-testing.md`：每 P 落地勾掉 §6 冗余清单对应行 + 更新 §2/§4.1 现状
- [ ] D.2 `docs/workflow/ci.md`：CI matrix / GREEN 标准随拓扑更新
- [ ] D.3 `docs/design/compiler/self-hosting.md`：SDK/Current 两套 + gen2 编一切 的设计原理
- [ ] D.4 `docs/spec/changes/ACTIVE.md`：登记 toolchain 占用；归档时释放

## 验证（每 P 独立 GREEN）
- [ ] V.1 P1：本地两套 toolchain 等价全绿
- [ ] V.2 P2：compile job 绿 + 条件不动点门生效（gen1==gen2 跳 gen3；故意改 codegen 验 gen3 触发 + gen2≠gen3 拦截）
- [ ] V.3 P3：下游全绿 + 时长下降
- [ ] V.4 P4：cross-bootstrap 绿 + 进 publish needs（本地 SDK 当种子能重建）
- [ ] V.5 P5：重命名/删 job/删脚本后全 CI 绿

## 备注
- Decision 2（format 兜底）第一版不做（已裁决）；全程仅占 `toolchain` 子系统（不含 runtime）。
- **gen3 条件触发**（Decision 6）：稳态 gen1==gen2 跳过 gen3，仅改 codegen 时编 gen3。
- **stdlib 两次编译**（Decision 5）：S3 SDK 编解析用（不发布）+ S6 gen2 编发布件（当前格式才能跑——SDK 编的旧格式 format bump 轮被 z42vm strict-pin 拒）。稳态 S3==S6 时 S6 可跳过（micro-opt）。
- **xtask 不进 SDK**（Decision 5）：S2 由 SDK 编驱动；发布形态由 cross-bootstrap 重编落 `artifacts/xtask/`。
- jit `--shard` 机制（95e9facf）复用不改，不在本变更 Scope。
