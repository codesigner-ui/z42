# Tasks: compile-once toolchain

> 状态：🟡 进行中（P1 开工 2026-06-27，User "开工"）| 创建：2026-06-27
> 子系统占用：`toolchain`（xtask）+ docs（**不含 runtime**——format 兜底已延后，第一版不改 reader）
> 🟢 Decision 2 已裁决（2026-06-27）：**第一版不做 format 兜底**，延后到未来 format bump 变更里同步落地（见 design.md）。

## 进度概览
- [ ] **P0** spec 就绪 + User 确认（阶段 6.5 gate）
- [x] **P1** xtask `--toolchain <dir>` 选择器 + `build sdk` 产 `.z42`（core-complete；1.4→P2、1.5→P3 重定位）
- [ ] **P2** compile job：内联 S0–S5 + 成对分代 gen1/gen2 + 条件不动点门（gen1==gen2 跳过 gen3）+ goldens/units + 上传 current-sdk
- [ ] **P3** 下游 job 消费 artifact（test-interp / test-jit / host-package / package-* / platform）
- [ ] **P4** 发布门三关进 needs：cross-bootstrap(完整) + 不动点(稳定) + 测试腿(正确)
- [ ] **P5** 重命名 + 删冗余 job + 删脚本（仅留 install-z42.sh）

---

## P0：spec 就绪
- [ ] 0.1 proposal.md / design.md / specs/bootstrap-toolchain/spec.md / tasks.md 四件齐（本目录）
- [ ] 0.2 Decision 2（format 兜底）展示给 User，等裁决（推荐 A committed seed）
- [ ] 0.3 阶段 6.5 确认 gate 通过（User 明确"可以开始"）

## P1：xtask `--toolchain` + `build sdk`（toolchain 子系统）
- [x] 1.1 `scripts/xtask_common.z42`：toolchain 解析 helper——`_toolchainDir`（读 Z42_TOOLCHAIN，相对路径对 root 解析）/ `_toolchainLibs` / `_toolchainDriver`（programs/z42c/z42c.driver.zpkg）/ `_activeLibsDir`（consumer 读：toolchain libs 或回退 canonical）。`_libsDir` 保持 canonical（producer 写）。本地重建 xtask 编过。
- [x] 1.2 `scripts/xtask_cli.z42`：✅ 全局 `--toolchain <dir>` 拦截（`_applyToolchainOpt`）+ ✅ `build sdk [--out <dir>]` 子命令注册（_buildRouter + _dispatchBuild → `_buildSdk(r.GetOption("out"))`）。
- [x] 1.3 `scripts/xtask_stdlib.z42`：`_buildSdk(string outOpt)` 组装 `.z42` 布局——`_buildStdlib()`(warm C#-free 建 z42c+stdlib+确保 z42vm) + `_resetDir` + 拷 programs/z42c/(7 包+siblings)、libs/(stdlib)、bin/z42vm。默认 --out=artifacts/.z42。**验证：`build sdk --out artifacts/.z42` exit 0；用组装出的 .z42 自身(z42vm+driver+libs 完全自包含)编出 xtask.zpkg 554KB → 工具链功能完整**。成对分代 gen1/gen2 在 P1.4 叠加（当前 _buildStdlib warm 产出即用）。
- [→P2] 1.4 **条件不动点 helper**（gen1 vs gen2 对，等跳 gen3／不等验 {gen2}=={gen3}）——**移到 P2**：现有 `_testZ42cSelfHostByteIdentical` 已覆盖稳态 gen1==gen2；条件 gen3 的失败路径只在「改 codegen」时触发，本地无 codegen-diff 难验，落在 P2 compile job（真有 SDK→gen1→gen2 序列）里实现+验证更自然。
- [→P3] 1.5 **test 据 `--toolchain` 定位 + `--no-build` 全链**——**移到 P3**：test consume 路径（`_testLibCore` 的 alllibs flat 单目录 + 多 stage）是 GREEN gate 关键路径，与「下游 job 消费 artifact」是同一件事，放 P3 一起做+CI 验证（避免在关键测试路径上孤立改动）。consumer helper `_activeLibsDir`/`_toolchainDriver` 已就位待接。
- [x] 1.6（部分）`build sdk` 产出的 .z42 自包含等价验证：用 .z42 自身工具链编出 xtask（见 1.3）。两套 `test` 等价全绿随 P3 test-consume 落地。
- [x] 1.7 commit（P1 选择器基础 8776451b + build sdk 893794be；不改 ci.yml，仅 xtask 能力）

> **P1 收敛说明**：两个头部能力 ——`--toolchain` 选择器 + `build sdk` 产 `.z42`—— 已实现并本地验证。
> 原 1.4/1.5 重定位到 P2/P3（各自的可验证上下文），见上。P1 视为 core-complete。

## P2：compile job（toolchain + ci.yml）

> **增量法（降低 CI 风险）**：现有 `toolchain-bootstrap` job 已是「编一次 → 上传 toolchain-<os> artifact」
> 的雏形（package job 已消费）。不另起炉灶——先在它上**追加** `build sdk --no-build` + 上传 `current-sdk-<os>`
> （additive，不动现有 staging/artifact，不破坏 package job），CI 验证 build sdk 在 CI 能产出 .z42；
> 再逐步把成对分代/条件不动点/goldens 接进来。

- [x] 2.1 ~~format 兜底落地~~ —— 🟢 Decision 2 裁决：**第一版不做**，延后到未来 format bump 变更同步落地。
- [x] 2.2a `build sdk --no-build`（assemble-only，跳过 _buildStdlib，从 warm canonical 组装）+ CLI flag。本地验证：0.5s 组装出完整 .z42（vs ~2min 重建）。
- [x] 2.2b `ci.yml` toolchain-bootstrap **追加**步骤：`build sdk --no-build → $RUNNER_TEMP/z42sdk` + 上传 `current-sdk-<os>`（additive）。YAML 校验通过。⚠️ **CI 不可本地验，待 push 后盯 run**。
- [ ] 2.3 成对分代 gen1/gen2 + 条件不动点门（稳定关）接进 toolchain-bootstrap/compile（gen1≠gen2 编 gen3）—— 后续
- [ ] 2.4 goldens.zbc/units.zbc 进 current-sdk artifact —— 后续（P3 下游 --no-build 需要）
- [ ] 2.5 dotnet PATH-mask 保留（现有 bootstrap 已有）
- [ ] 2.6 push + CI 验证 current-sdk-<os> artifact 产出正确

## P3：下游消费 artifact（ci.yml + action + bench/release）
- [x] 3.0 **consume 验证 job**（additive）：新增 `consume-current-sdk` job——下载 current-sdk-ubuntu-latest，用其自带 z42vm+driver+libs 编 xtask，证跨 job artifact 是可消费工作工具链。continue-on-error，不动现有 test 主体。YAML 校验通过，待 push 验。
- [x] 3.1a **cross-zpkg --toolchain 接线**（第一个真 test stage 消费）：`_testCrossZpkgImpl` locator + `_testCrossZpkgCore` rebuild 跳过据 `--toolchain`（向后兼容：未设走原 debug-vm/canonical 路径）。本地双验：`--toolchain artifacts/.z42 test cross-zpkg` 2/2 绿（纯工具链零重建）+ 无 --toolchain 仍 2/2 绿。consume-current-sdk job 加跑此 gate。
- [ ] 3.1 `.github/actions/xtask-bootstrap-artifact/action.yml`：改为下载 `current-sdk` + `--toolchain artifacts/.z42` 消费
- [x] 3.1b **vm goldens --toolchain 接线**（第二个真 test stage）：`_runVmGoldens` vmBin + `_regenGolden` driver/libs/vm + `_testVmCore` 跳 cargo（toolchain 供 z42c+stdlib+vm；0 golden 用 compression native）。本地双验：`--toolchain artifacts/.z42 test vm interp` regen 199 + shard 10/10 绿 + 无 --toolchain --no-rebuild 10/10 绿。consume job 加跑全 interp goldens。复用 helper `_activeVm`/`_toolchainDriverHome`/`_toolchainVm`（cross-zpkg 同步重构）。
- [ ] 3.2 test 腿（build-and-test→消费 artifact）：下载 + cargo z42vm + `--toolchain artifacts/.z42 test --no-build`（interp）
- [ ] 3.3 jit 腿：同上 + jit + `--shard k/4`（复用 95e9facf 分片，不改机制）
- [ ] 3.4 host-package / package-{ios,android,wasm} / platform-test：全部消费 artifact
- [ ] 3.5 `bench-pr.yml` / `bench-update.yml` / `release.yml`：消费 artifact（或同构内联 compile）
- [ ] 3.6 commit + CI 验证全下游绿 + 关键路径时长下降（目标 ~52→~25-30min）

## P4：发布门三关进 needs（完整 / 稳定 / 正确）
- [ ] 4.1 cross-bootstrap（完整关）：`scripts/bootstrap-no-csharp.sh` 改造——种子换成"**本地 SDK set 的打包发布形态**"（消费 current-sdk / 本地 `build package` 产物），重跑 S2-S4：编 xtask（验编成功）+ 编 {z42c,stdlib}（逐字节==自带 = {gen2}=={gen3}）
- [ ] 4.2 正确关：**test-interp / test-jit（+ stdlib/cross-zpkg 行为覆盖）进 `publish-nightly` 的 needs**——三腿本就跑 gen2 工具链(=发布件)，直接进 needs。修"稳定地错也能发"缺口
- [ ] 4.3 `publish-nightly` needs = package-* + cross-bootstrap + test-interp + test-jit（三关全过才发）
- [ ] 4.4 commit + CI 验证：① 故意改 z42c codegen 制造 {gen2}≠{gen3} 验稳定关拦截 ② 故意弄挂一个 [Test] 验正确关拦截发布

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
- **成对分代 {z42c, stdlib}**（Decision 5）：gen1 = SDK 编一对；gen2 = gen1 编一对（发布件）。发布的 stdlib 必须 gen2 编 = 当前格式才能跑（format bump 轮旧格式被 z42vm strict-pin 拒）。
- **gen3 条件触发**（Decision 6）：稳态 gen1==gen2 跳过 gen3，仅改 codegen 时编 gen3 验 {gen2}=={gen3}。
- **发布门三关**（Decision 8）：完整(gen2/cross-bootstrap 编出来) + 稳定({gen2}=={gen3}) + **正确(测试腿进 publish needs)**。{gen2}=={gen3} 只证稳定不证正确。
- **版本成对匹配**：z42c+z42vm+stdlib 同格式；跑 SDK z42c 用旧 vm、跑 gen2 用当前 vm（bump 轮过渡期两个）。
- **xtask 不进 SDK**：S2 由 SDK 编驱动；发布形态由 cross-bootstrap 重编落 `artifacts/xtask/`。
- jit `--shard` 机制（95e9facf）复用不改，不在本变更 Scope。
