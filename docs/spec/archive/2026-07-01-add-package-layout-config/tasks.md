# Tasks: 配置驱动的发行包布局

> 状态：🟢 已完成（含已知缺口，见备注）| 创建：2026-06-30 | 完成：2026-07-01 | 子系统：toolchain（+ stdlib z42.project）

## 实施顺序（User 裁决 2026-06-30）
递进、风险后置：**① 支持 apphost bin/payload 配置（不动 xtask）→ ② 让 xtask 用该配置发布
→ ③ 敲定 packages.toml → ④ 最后重构 xtask 代码**。下面阶段编号沿用，但按此顺序推进。

## 进度概览
- [x] 阶段 1: 部署布局声明（组件 toml `bin`/`payload` + z42.project 模型 + publish 消费）— ① 完成
- [x] 阶段 2: publish 输出布局子树 — ① 已含（_cmdPublishDesktop 支持 bin/payload，向后兼容）
- [x] 阶段 3: packages.toml + xtask 组装引擎 — ③
- [x] 阶段 4: 迁移 desktop SDK + runtime 打包到配置驱动 — ②（让 xtask 用配置）→ ④（重构）
- [x] 阶段 5: 验证（产物字节一致）+ 文档 —— 文档四项（5.5-5.8）已完成；端到端字节一致验证（5.1-5.4）受环境阻断，User 裁决归档、转入 Deferred 追踪（见备注 + `packaging-future-byte-identical-verification`）

## 阶段 1: 部署布局声明（① 完成 2026-06-30）
- [x] 1.1 `DesktopConfig.z42` 增 `bin` / `payload` 可选完整路径字段（空 = 约定默认，由消费方推导）
- [x] 1.2 `ManifestLoader.z42` 解析 `bin` / `payload`
- [x] 1.3 单元测试：bin/payload 解析 + publish 子树行为（由阶段 3.4a `test packages-config` + 阶段 4 e2e 覆盖）
- [x] 1.4 给 launcher/z42c.driver/builder 三个 toml 写显式布局（镜像现状）— 由 3.3a 落地

## 阶段 2: publish 输出布局子树（① 完成核心 2026-06-30）
- [x] 2.1 `_cmdPublishDesktop` 扩展：`payload` 设则把已编译 zpkg 复制到 `root/payload`、apphost 落 `root/bin`；未设走 legacy 扁平（向后兼容，现有包零回归——package 重建验证）
- [x] 2.2 apphost 内嵌相对路径复用 `Apphost.Produce` 已有的 `_relPath(dir(bin), payload)` 自动计算（无需新写）
- [x] 2.3 e2e：单组件 publish 产出自洽子树（由 3.3a `z42b publish` 独立验证 + 4.1a 覆盖）
- [x] 2.4 用户面文档 `docs/design/compiler/project.md` `[platform.desktop]` 补 bin/payload

## 阶段 3: packages.toml + 组装引擎（2026-07-01 修订：z42c-seed 不再是固定 staging handler）
- [x] 3.1 NEW `scripts/package/packages.toml`：sdk + runtime 两个包的 include 列表（DRAFT，z42c 只一个条目，6 兄弟库靠 publish 自动依赖打包）
- [x] 3.2 NEW `scripts/package/xtask_packages_config.z42`：解析 packages.toml + include 名解析（`_PackagesConfig`/`_PkgPackage`/`_PkgComponent` 三个数据类 + `_pkgcfgLoad`/`_pkgcfgFindPackage`/`_pkgcfgFindComponent`/`_pkgcfgResolveInclude`；纯读取，不知道"怎么产出组件"——复用 `xtask_versions.z42` 既有 `_vget`/`_vgetList`/`_scalarStr` 解析基元，风格一致。编译验证：`.z42/bin/z42c build scripts/xtask.z42.toml --release` 0 错误）
- [x] 3.3 稳定 staging handler：z42vm / native / stdlib（复用现有 _copyNativeLibs / _pkgCopyLibs）——**z42c 七成员不在此列**，改由 `builder_publish.z42` 的依赖自动打包覆盖（见 3.3a）。NEW `scripts/package/xtask_stage_components.z42`：`_pubStagingRoot`/`_pkgStageDir`（暂存根 + 单组件目录，每次 stage 前重置不累积）+ `_pkgStageZ42vm`/`_pkgStageNative`（各自落到 `artifacts/publish/<comp>/<dest>`，与 packages.toml 的 dest 字段完全对应——直接复用 `_copyNativeLibs`/`_pkgCopyLibs` 不改一行，验证了 Decision 6"暂存目录可直接顶 pkgDir 角色"的前提）+ `_pkgStageStdlib`（复用 `_pkgCopyLibs`）+ `_pkgBuildAndStageRuntime`（z42vm/native 共享同一次 cargo build，镜像 `_packageDesktop` 现有 [2/5]+[2b/5] 步骤，只是落点换成暂存根而非 pkgDir——本任务只稳定 handler，尚未接入 `_packageDesktop` 主流程，接入是 4.1b 的范围）。NEW `scripts/package/xtask_test_stage_components.z42` + `xtask test packages-staging`（同 3.4a 先例：throw-on-mismatch 自检命令）：用假 cargoOut（这些 handler 只拷贝、不触发 cargo，测试无需真实构建）验证 z42vm/native 落点 + mobile facade 排除 + headers 拷贝 + stage 目录重置语义；stdlib 用仓库真实已建 dist 验证 libs/ 落点。**编译 + 运行时验证**：`.z42/bin/z42c build scripts/xtask.z42.toml --release` 0 错误；`Z42_PORTABLE_VM=.z42/bin/z42vm Z42_LIBS=.z42/libs .z42/bin/z42vm artifacts/xtask/xtask.zpkg -- test packages-staging` → `packages-staging: PASS (z42vm/native/stdlib staging handlers)`；`test packages-config` 复跑仍 PASS（无回归）。
- [x] 3.3a `builder_publish.z42` 新增 workspace 项目依赖发现 + build-if-needed + 拷贝到同一 payload 目录（发布 z42c.driver 时自动带出 6 个兄弟库）；launcher/z42c.driver/z42.builder 三个 toml 补 `[platform.desktop] bin`/`payload`（阶段 1.4 一并做）。**独立验证通过**（2026-07-01）：`z42b publish src/compiler/z42c.driver/z42c.driver.z42.toml --rid macos-arm64 --output <tmp>` 产出 `bin/z42c` + `programs/z42c/`（driver + 6 兄弟 zpkg/zsym），apphost 经 `Z42_PORTABLE_VM` 可正常运行（`z42c --version` 输出正常）。过程中顺带修复 `_pubResolveZpkg` 未处理 workspace 成员 `[workspace.build] output_dir` 模板继承的 bug（workspace 成员 toml 通常无自己的 `[build]` 段，之前会算出错误路径——被 z42c.driver 目录下一份陈旧 gitignored dist 产物意外掩盖，z42c.semantics 无同类陈旧产物才暴露）。
- [x] 3.4a 解析 + include 解析部分：NEW `scripts/package/xtask_test_packages_config.z42` + `xtask test packages-config`（xtask 是 exe、非 stdlib zpkg，`[Test]` 反射 runner 看不到它，故走 `test compiler`/`test compiler-stdlib` 已有先例——xtask 内自带函数式自检命令，throw-on-mismatch 而非 `[Test]` 属性发现）。断言：package/component 数量、sdk.include 6 项解析、**runtime.include 必为 `["native","stdlib"]`、不含 z42c/z42vm**（2026-07-01 纯嵌入式运行时裁定的回归哨兵）、kind 相关可选字段（apphost 无 dest / stdlib-glob 无 project）、未知 package/component 名会 throw。**运行时验证**：`Z42_PORTABLE_VM=.z42/bin/z42vm Z42_LIBS=.z42/libs .z42/bin/z42vm artifacts/xtask/xtask.zpkg -- test packages-config` → `packages-config: PASS (3 packages, 7 components)`。「依赖闭包解析」（z42c 兄弟库 workspace 依赖发现）已属 3.3a 范围、已在那里独立验证，非本模块职责，不重复测。

## 阶段 4: 迁移 desktop SDK + runtime
- [x] 4.1a `_packageDesktop` 的 z42/z42c/z42b 三处手工 `File.Copy` + 内联 `_produceApphost` 拼装，改为三次 `z42b publish`
  调用（新增 `_z42bPublish` helper）；xtask 自带的 `_asciiBytes`/`_byteIndexOf`/`_produceApphost` 已删（唯一实现收敛到
  `builder_publish.z42` 的 `_pubProduceApphost`）。[1/5] z42c seed 的手工 copy 循环**保留**——它是 z42b 自身可执行
  之前的自举前置（z42cDir 需先被 colocate 好，`_z42cBuildToml` 才能编 workload/launcher/builder 三个 toml），
  与 bootstrap-seed.md 的鸡蛋问题同构，不属于本任务可消的范围。`z42b publish z42c.driver.z42.toml` 之后仍会
  幂等重拷（`std::fs::copy` 覆盖式写），非新增文件。**编译验证**：`scripts/xtask.z42.toml` 全量编译通过（含新
  `_z42bPublish`）；`builder_publish.z42`（含 zsym-removal 改动）独立编译 `z42.builder.z42.toml` 用仓库自建
  `z42c.driver.zpkg`（colocated 7 兄弟 + `z42vm --mode interp`，与 `_z42cBuildToml` 真实路径一致）编译通过，0 错误。
  之前用 `.z42/bin/z42c`（旧 nightly 种子，2026-06-28）ad-hoc 验证时误报 `E0401: undefined: Runner`——根因是种子
  太旧、读不全当前 zbc/zpkg 格式新增的 `Runner`/`ModuleLoader`/`TestEntry` 类（`strings` 扫两份 `z42.test.zpkg`
  证实：pristine nightly 完全没有这三个类的符号，而仓库自建 stdlib 有），是验证方法本身的问题（bootstrap-seed.md
  同类鸡蛋问题在诊断工具选型上的翻版），非 `builder_test.z42` 或本次改动的真实 bug。**运行时验证**（改用仓库自建
  z42vm + z42.builder.zpkg + Z42_APPHOST_TEMPLATE）：`z42b publish` 分别跑 z42c.driver / launcher / z42.builder 三个
  toml → `bin/z42c`+`programs/z42c/`(6 兄弟)、根 `z42`+`programs/launcher/`、`bin/z42b`+`programs/z42b/` 布局均正确，
  **均无 `.zsym` 侧车文件**（zsym-removal 生效），三个 apphost 经 `Z42_PORTABLE_VM` 均可正常运行（`--version`/`--help`
  输出正常，exit 0）。`./xtask test compiler` 全绿（unit 17/17 + e2e + 自举不动点 7/7 gen1==gen2）。
- [x] 4.1b 按 sdk.include 选组件 → 合并暂存子树 + staging 产物。NEW `scripts/package/xtask_package_assemble.z42`：`_pkgAssembleFromStaging(cfg, root, packageName, pkgDir)` —— 解析 `[package.<name>].include`，对每个已解析组件把 `artifacts/publish/<comp>/` 用 `Directory.Copy(_, pkgDir, true)` 合并进 pkgDir（Decision 6：组装只知道"读暂存根拷贝"，不知道"怎么产出"；Decision 5：include 顺序只决定同名文件谁赢，不影响构建顺序）；组件未暂存时 loud-fail（`return 1` + 报错，不静默拼半成品包）。`_packageDesktop` 重写：[2/5] 改调 `_pkgBuildAndStageRuntime`（z42vm+native 一次 cargo build 双组件暂存）；三处 `_z42bPublish` 调用的落点从 `pkgDir` 改为 `_pkgStageDir(root, <comp>)`（暂存根，不再直接写包目录）；[4/5] 改调 `_pkgStageStdlib`；新增 `[assemble]` 终步调 `_pkgAssembleFromStaging(cfg, root, "sdk", pkgDir)` 把 6 个暂存子树合并进 pkgDir。NEW `scripts/package/xtask_test_package_assemble.z42` + `xtask test packages-assemble`（同 3.4a/3.3 先例：throw-on-mismatch 自检）：假两组件验证合并语义（含子目录递归）+ include 顺序决定同名文件胜出 + 未暂存组件报错不静默。**编译 + 自检验证**：`.z42/bin/z42c build scripts/xtask.z42.toml --release` 0 错误；`test packages-assemble` → PASS（merge + copy-order + unstaged-component error path）；`test packages-config`/`test packages-staging` 复跑仍 PASS（无回归）。**端到端字节一致验证受阻**（记入阶段 5 备注，非本任务代码问题）：并行 `add-params-varargs` 会话的 zpkg 格式升级已把共享 `.z42/bin/z42vm` 重建为 zpkg writer minor 0.23，而磁盘上已建 stdlib/z42c zpkg 产物仍是 minor 22，导致此刻任何 `package release` 全量构建（无论新旧代码）都会因 "zpkg minor 22 not supported" 报错失败——与本次重构无关，不在本 change Scope 内修（不应借并行会话未提交的格式升级去重建 stdlib/z42c）。
- [x] 4.2 `_buildRuntimePackage` 改为「按 runtime.include 组装」，删 reuse-from-sdk 特例。**运行时包内容范围已裁定**（2026-07-01 User 裁决）：runtime 包定位为纯嵌入式运行时（`native/` + `libs/`），不含 z42c/z42vm——两者是 host 平台工具，而 runtime 包可能跨 host 使用（如 android runtime 装在 Windows/macOS host 上），塞单一 host 平台意义的二进制没有意义，与 ios/android/wasm 的 runtime 包形态一致。`packages.toml` 的 `[package.runtime] include` 已是 `["native", "stdlib"]`。自举种子改由 SDK 包的 `programs/z42c/`（apphost 布局）提供，`.github/actions/ci-bootstrap/action.yml` + `scripts/build/xtask_bootstrap_check.z42` 均已切换下载源（`z42-sdk-nightly-<rid>` 替代 `z42-runtime-nightly-<rid>`）+ 路径（`programs/z42c/*.zpkg` 替代扁平 `z42c/*.zpkg`）；`docs/design/compiler/self-hosting.md` 种子分发段同步更新。**本任务收尾**（随 4.1b 一并落地）：`_buildRuntimePackage` 的 reuse-from-sdk 特例（拿掉 `sdkPkgDir` 入参，不再从 SDK 包拷贝）已彻底换成真正的「按 runtime.include 组装」——直接调 `_pkgAssembleFromStaging(cfg, root, "runtime", pkgDir)`，与 sdk 包走同一条组装路径，无特例代码。
- [x] 4.3 打包分发 `xtask_package.z42` 接 packages.toml（2026-07-01 确认范围）：`xtask_package.z42` 的顶层分发器 `_buildPackageCore` 本身不含任何组件选取逻辑——desktop 分支依次调 `_packageDesktop`/`_buildRuntimePackage`/`_buildDesktopWorkload`，前两者已在 4.1b/4.2 改为从 `packages.toml` 暂存根组装（`_pkgAssembleFromStaging`），`xtask_package.z42` 无需再改。**User 裁决**（发现 `packages.toml` 的 `[package.sdk].artifact = "z42-sdk-{version}-{rid}"` 字段与 `xtask_package.z42:34` 实际硬编码的包目录名 `"z42-" + version + "-" + rid + "-" + profile"` 不一致，摆出后询问是否要改命名）：**不改命名**——`artifact`/`manifest` 字段本 Phase 仍是声明性文档（`packages.toml` 头部注释已明确 `manifest = "auto"` 保持现状机制不变），改包目录名属于外部可见行为变化、且与本变更"纯重构、产物字节一致"的硬验收方向冲突，不在本次范围。顺带修正 `design.md` Implementation Notes 里过时的 schema 示例（`artifact = "z42-{rid}"` 已与当前 `packages.toml` 实际值 `"z42-sdk-{version}-{rid}"` 不符）。`_buildDesktopWorkload`（workload-desktop 包）本就不在 Phase 1 Scope 内（proposal.md："本变更 Phase 1 只覆盖 desktop SDK + runtime 两个包"），未接入 `_pkgAssembleFromStaging`，维持现状。
- [x] 4.4 registered devtools/interactive into packages.toml（2026-07-01 User 裁决推翻本文件原"独立变更"备注，见下）。`z42.devtools.z42.toml`/`z42.interactive.z42.toml` 补 `[platform.desktop] bin`/`payload`（`bin/z42d`+`programs/devtools/z42.devtools.zpkg`、`bin/z42i`+`programs/interactive/z42.interactive.zpkg`，镜像 z42b 布局）；两者的 "NOT registered.../gate stays off" 注释改为"已注册，仍是占位实现"。`packages.toml` 新增 `[component.devtools]`/`[component.interactive]`（kind=apphost）+ `sdk.include` 追加 `"devtools"`、`"interactive"`（6→8 项）。`_packageDesktop` 追加两组 `_z42cBuildToml`+`_z42bPublish`（同 launcher/z42b 套路，落 `_pkgStageDir(root, "devtools"/"interactive")`）。`xtask_test_packages_config.z42` 更新断言（component count 7→9、sdk.include 6→8、新增 devtools/interactive kind/project 断言）。两个 README 状态段同步"已打包，占位实现"。**验证**：`.z42/bin/z42c build scripts/xtask.z42.toml --release` 0 错误；`test packages-config` → PASS(3 packages, 9 components)；`test packages-staging`/`test packages-assemble` 复跑仍 PASS（无回归）。同一 pre-existing 环境阻断（4.1b 备注所述 zpkg minor 22 vs 0.23）下用 devtools/builder 两个 toml 分别独立跑 `z42c build` 交叉验证：两者报同一个 `undefined function Z42.Project.ManifestLoader.Load` 错误，确认是环境阻断而非本任务引入的回归。

## 阶段 5: 验证 + 文档
- [x] 5.1 sdk 包树逐文件/逐字节 == 改造前 → **Deferred `packaging-future-byte-identical-verification`**（zpkg minor 22↔0.23 环境阻断，roadmap:442 已索引）
- [x] 5.2 runtime 包同上 → **Deferred**（同 5.1）
- [x] 5.3 `z42 xtask.zpkg test`（全 stage）全绿 → **Deferred**（同 5.1 环境阻断；重构逻辑经单元 `test packages-*` + 部分 e2e 独立验证）
- [x] 5.4 `z42 xtask.zpkg test dist`（发行包验证）全绿 → **Deferred**（同 5.1）
- [x] 5.5 NEW `docs/design/toolchain/packaging.md`（实现原理：暂存布局、packages.toml schema、producer 边界、Deferred）
- [x] 5.6 `docs/design/compiler/project.md` 增 `[platform.desktop] bin`/`payload` 用户面说明
- [x] 5.7 `src/toolchain/README.md` 指向 packaging.md + packages.toml
- [x] 5.8 spec scenarios 逐条覆盖确认（重构 = 声明化，行为不变；scenarios 由 `test packages-config`/`-staging`/`-assemble` 三自检覆盖）

## 备注
- Phase 1 不动 mobile（ios/android/wasm）打包 → Deferred packaging-future-mobile。
- ~~z42d/z42i 真正登记进 build/workspace 是独立变更；本变更只让「登记后加 include 即可」成立。~~
  **推翻（2026-07-01 User 裁决，见 4.4）**：User 明确要求现在就注册，接受两者仍是纯占位实现
  （子命令/入口只打印 "planned"）的现状——不再等到"真正实现"才注册。
- 硬验收 = 产物字节一致：本质是重构（声明化），不得改变发行包内容。
- **5.1/5.2 端到端字节一致验证暂时受阻**（2026-07-01，非本变更代码问题）：并行 `add-params-varargs` 会话的 zpkg 格式升级已把共享 `.z42/bin/z42vm` 重建为 zpkg writer minor 0.23，磁盘上已建 stdlib/z42c zpkg 产物仍是 minor 22，导致此刻任何 `package release` 全量构建（改造前基线或改造后）都会报 "zpkg minor 22 not supported" 而失败。待并行会话的格式升级落地（stdlib/z42c 重建为 minor 0.23）后补跑 5.1/5.2。
