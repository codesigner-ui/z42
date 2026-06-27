# Tasks: compile-once toolchain → 分层流水线（6 阶段）

> 状态：🟡 进行中 | 创建：2026-06-27 | 重构为 6 阶段分层模型 2026-06-28（User 确认）
> 子系统占用：`toolchain`（xtask）+ runtime（仅 stage1 rule c 的 cargo-test gate；build.rs 已改）+ docs
> 🟢 Decision 2（format 兜底）：第一版不做，延后（见 design.md）。

## 北极星：6 阶段分层流水线

**CI / xtask / docs 三维同步——本地按阶段跑即镜像 CI。原则：先简化+明确流程，再修 bug / 换实现。**

| 阶段 | 产物 | 共享性 | 触发 | xtask 命令 |
|------|------|--------|------|-----------|
| **1 change-detect** | 改动分类 flag | — | 总是（轻量）| —（CI 专属）|
| **2 bootstrap 边界检查** | 上一版 nightly 能编当前 z42c 源 | — | 仅 z42c 改动 | `xtask bootstrap-check` |
| **3 host package** | `{z42c,stdlib,toolchain}` zpkg + z42vm | **同平台共享**（zpkg 全平台；z42vm per-platform 上传复用）| 非 docs-only | `xtask build sdk` |
| **4 测试资产** | golden/unit/fixture .zbc | **同平台共享**（.zbc 平台无关）| 非 docs-only | `xtask build test-assets` |
| **5 运行测试** | 测试结果（消费 3+4，`--no-build`）| 每 OS/mode | 按 change-detect | `xtask test --toolchain <sdk> --no-build` |
| **6 publish nightly** | nightly release | — | 5 全过 + main push | —（CI 专属）|

## 确认的规则与约束（User 2026-06-28）

**change-detect 规则**：
- (a) 非代码改动（docs/`*.md`/.claude）→ **不触发 CI**（不发起、不取消在跑的）；doc 检查后续再加。
- (b) 未改 z42c（`src/compiler`）→ 跳过自举边界检查（stage 2）。
- (c) 未改 VM（`src/runtime`）→ 跳过 Rust VM 单测（cargo test）。
- 先落地这三条，其余分类后续再拆。

**约束（认清；先简化后修）**：
1. **z42vm per-arch，同平台共享**：host package 带 z42vm，同平台下游直接用 + 上传，不重 cargo build。
2. **test-runner native：暂保留**，`xtask test` 接口不变；后续换 `z42.build` 内部实现（本地/CI 都走 `xtask test`，只换内部）。
3. **release-vm-jit bug：暂用 debug vm / 延后**，先理顺流程再修（见 [[reference_release_vm_jit_miscompiles_default_params]]）。
4. **防死锁**：stage 6 不 gate 在 download-bootstrap job 上（bump 轮它们暂红，gate 上去会死锁）。

**🎯 核心原则：脚本归零（统一引擎）—— User 反复强调**：
**除 `install-z42.sh`（cold-start primer），删除 scripts/ 下所有 shell 脚本；逻辑全搬进 `xtask` 子命令；
CI 与本地都只调 `xtask`（同一套逻辑）。** 唯一不可消除的 bootstrap primer（cargo z42vm + 下载/定位种子 +
种子 z42c 编 xtask，~5 行）**内联进 CI 步骤，不单独成脚本文件**。这是 Layer A 的硬目标，也是"本地镜像
CI"的前提——没有 shell 中间层，CI 步骤即 `xtask <stage>`。

---

## 迭代清单（按阶段推进，一一勾选）

### Stage 1 — change-detect
- [~] 1a CI `on.paths-ignore`：docs/`**/*.md`/.claude → 非代码不触发 CI（rule a）【🟡 进行中】
- [ ] 1b CI `detect-changes` 加 `compiler`(`src/compiler/**`) + `vm`(`src/runtime/**`) 输出（含 `.github/workflows/ci.yml` 保底全跑）
- [ ] 1c CI `verify-selfhost` gate on `compiler`（rule b；needs changes + if）
- [ ] 1d CI `build-and-test` 的 `cargo test`(Windows leg) step gate on `vm`（rule c；needs changes + step if）
- [ ] 1e docs：change-detect 规则表落 bootstrap-and-testing.md
- [ ] 1f 验证：docs-only push 不触发 CI；compiler-only / vm-only 改动按 gate 跳过

### Stage 2 — bootstrap 边界检查
- [ ] 2a xtask `bootstrap-check`：封装 `check-bootstrap-compat.sh`（下载上一 nightly z42c → 编当前 z42c 源 → 对比无越界），本地可跑
- [ ] 2b CI：bootstrap-check（并入或独立），gate on `compiler`（rule b）
- [ ] 2c docs：support-before-use 纪律 + 本地复现

### Stage 3 — 编译 host package（同平台共享）
- [x] 3a xtask `build sdk [--out]` 产 `.z42`（zpkg + z42vm + toolchain）✅（原 P1）
- [x] 3b CI compile/toolchain-bootstrap 产 `current-sdk-<os>` artifact ✅（原 P2）
- [ ] 3c host package **带 z42vm** + 同平台下游消费它（不重 cargo build；约束1）—— 补 bin/z42vm 到 artifact、下游用
- [ ] 3d per-OS 覆盖（现 linux+macos；按需补 arm/windows host package）
- [ ] 3e docs：stage 3 CI + 本地（`xtask build sdk`）

### Stage 4 — 编译测试资产（同平台共享）
- [x] 4a CI：golden `.zbc` 编一次进 current-sdk（regen --no-stdlib + bundle）✅（原 P2.4）
- [ ] 4b xtask `build test-assets [--toolchain]`：统一命令——regen goldens + 编 test-units + fixtures → 进 `.z42`/独立 artifact
- [ ] 4c CI：test-units / fixtures 也进共享 artifact（供 stdlib/host test `--no-build`）
- [ ] 4d docs

### Stage 5 — 运行测试（消费 3+4，`--no-build`）
- [x] 5a xtask：cross-zpkg + vm goldens 经 `--toolchain` 消费 ✅（原 P3 1.5）
- [x] 5b CI：`test-consume` job 消费 current-sdk 跑 cross-zpkg + vm interp（169/169）✅
- [ ] 5c xtask `test` 编排器：检测 stage3/4 产物在否，缺则补、在则默认 `--no-build` 消费（本地分阶段，不再一把梭）
- [ ] 5d CI：迁移 `build-and-test` 为消费（needs 3+4 + `--no-build`；stdlib 段保留 `xtask test` 接口，内部待换 z42.build）
- [ ] 5e CI：迁移 `vm-jit` / `stdlib-jit` 为消费（jit 暂用 debug vm 绕 release-jit bug，或延后）
- [ ] 5f docs

### Stage 6 — 测试通过上传 nightly
- [x] 6a CI：`publish-nightly` 存在 ✅
- [ ] 6b 确认三关：正确已由 `build-and-test` gate；稳定/完整（fixpoint/cross-bootstrap）**故意不进 needs**（防死锁，约束4）—— 更正 design Decision 8
- [ ] 6c docs：发布门 + 防死锁 rationale

### 跨阶段地基 — xtask 分阶段命令（Layer A）
- [ ] LA xtask 4 个 stage 命令（bootstrap-check / build sdk / build test-assets / test --no-build）+ `test` 编排器，本地镜像 CI

### 文档 — ci.md 全面重写（User 2026-06-28）
- [x] DOC1 `docs/workflow/ci.md` 全面更新：**6 阶段流程图**（mermaid / ASCII）+ **每阶段详细说明**（产物 / 共享性 / 触发 / CI job / xtask 命令 / 本地复现）（6 阶段 mermaid 流程图 + 每阶段详解 + GREEN 标准去 dotnet + 本地镜像 CI + job 映射，已落地）
- [ ] DOC2 ci.md 与 bootstrap-and-testing.md 分工：ci.md = CI 拓扑 + 阶段总览；bootstrap-and-testing.md = 自举机制深入
- [x] DOC3 **全面更新 `docs/workflow/` 所有文档**（User 2026-06-28）——18 个文件清除完毕（dotnet/slnx/z42c.dll 命令换 `z42c`/`./xtask`、删 .NET 安装引导、unit-tests.md 重写为 z42c 自举+cargo test、wasm 删 dotnet-serve、windows 删 .NET SDK、artifacts 树/wave 表/对比表去 C#、stale 死符号 `Z42.Tests.*`/`CompilerUtils.*` 清除）：
  - **dotnet/C#/slnx 残留**：`dotnet build src/compiler/z42.slnx` / `dotnet run --project src/compiler/z42.Driver` / `dotnet test ...csproj` / `z42c.dll` / `.NET 10+ 安装引导` → 全部换成 `./xtask` / `z42c`（z42 自举，2026-06-26 dotnet 已删）
  - **旧 job 名**：`build-and-test` / `bootstrap-no-csharp` / `ci-bootstrap-nocs` → 新动作-平台命名（`test (<平台>)` / `verify-selfhost` / `compile`）
  - **旧命令**：`dotnet test ...csproj` 单测、`dotnet run -- build --workspace` → `./xtask` 等价
  - 受影响文件（grep 命中）：`building/{compiler,stdlib,android,ios,wasm,cross-platform,README}.md`、`testing/{README,unit-tests,stdlib-tests,platform-tests,vm-tests}.md`、`debugging.md`、`packaging.md`、`windows.md`、`README.md`、`bootstrap-and-testing.md`

---

## 历史实施明细（原 P1–P5，已映射进上面阶段）

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
- [x] 2.4 **共享编译资产**：toolchain-bootstrap 加 `regen --no-stdlib`（编一次 golden .zbc）+ build sdk 后 `cp -r artifacts/build/tests` 进 current-sdk。consume job extract + `test vm interp/jit --no-rebuild`——interp/jit 跑**同一份预编译 .zbc**，零 N× regen。本地验：`--no-rebuild` 1.7s（vs regen 2min）。jit-via-toolchain CI probe 已绿。
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
- [ ] 4.1 cross-bootstrap（完整关）：`scripts/selfhost-bootstrap.sh` 改造——种子换成"**本地 SDK set 的打包发布形态**"（消费 current-sdk / 本地 `build package` 产物），重跑 S2-S4：编 xtask（验编成功）+ 编 {z42c,stdlib}（逐字节==自带 = {gen2}=={gen3}）
- [ ] 4.2 正确关：**test-interp / test-jit（+ stdlib/cross-zpkg 行为覆盖）进 `publish-nightly` 的 needs**——三腿本就跑 gen2 工具链(=发布件)，直接进 needs。修"稳定地错也能发"缺口
- [ ] 4.3 `publish-nightly` needs = package-* + cross-bootstrap + test-interp + test-jit（三关全过才发）
- [ ] 4.4 commit + CI 验证：① 故意改 z42c codegen 制造 {gen2}≠{gen3} 验稳定关拦截 ② 故意弄挂一个 [Test] 验正确关拦截发布

## P5：重命名 + 评估删 job + 删脚本（ci.yml + scripts/）
- [x] 5.1 **CI job 重命名**（动作-平台-host 约定，display name + matrix platform 标签，id 不变）：build-and-test→`test (<平台>)`、toolchain-bootstrap→`compile (<平台>)`、host-package→`package (<平台>)`、vm-jit→`test-vm-jit-linux-x64`、stdlib-jit→`test-stdlib-jit-linux-x64`、bootstrap-no-csharp→`verify-selfhost-linux-x64`、compiler-z42-stdlib→`test-compiler-stdlib`、feature-matrix→`verify-features`、package/test-{ios→-macos,android/wasm→-linux}、bench-e2e→`bench-linux-x64`、consume→`test-consume-linux-x64`。⚠️ User 需更新 branch protection required checks。
- [ ] 5.2 评估删 `compiler-z42-stdlib` job（确认覆盖已被 compile job + test 阶段完全包含；Open Question）
- [ ] 5.3 删 `scripts/ci-bootstrap.sh`（逻辑已内联 compile job）
- [ ] 5.4 删 `scripts/ci-stage-toolchain.sh`（折进 `xtask build sdk`）+ `scripts/check-bootstrap-compat.sh`（边界由 compile job 隐式强制）
- [ ] 5.5 **保留 `scripts/install-z42.sh`**（cold-start primer）+ `scripts/selfhost-bootstrap.sh`（已改造为 cross-bootstrap，不删）
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
