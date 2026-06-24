# Tasks: remove-dotnet-from-builds (replace-csharp S5 Phase A)

> 状态：🟡 进行中 | 创建：2026-06-22
> 子系统：`toolchain`
> 变更说明：把 BUILD 调用点从 C# driver（dotnet）切到 z42c（z42vm + z42c.driver.zpkg），
> 使构建不再依赖 dotnet。保留 C# 作 byte-identical oracle 门（Phase B/C 再替换 + 删 C#）。
> 原因：replace-csharp S5；User 选 Phase A（先切构建、gate 不可逆的删除）。
> 文档影响：self-hosting.md（S5 Phase A）。

## 进度概览
- [x] A1 生产 stdlib flip C#-free（warm：种子存在用 z42c 自建；cold：C# 兜底）
- [ ] A2 其余 build 站点切 z42c（xtask self-build / package / regen / bench / test-unit）
- [ ] A3 bootstrap 提供种子（S4 download）→ CI 默认 warm C#-free（非 cold C#）
- [ ] A4 文档 + 归档

## A1 — 生产 stdlib flip C#-free ✅
- [x] `_buildCompilerZ42ViaZ42c`（xtask_compiler_z42.z42）：用现有 z42c.driver.zpkg 作种子，
  单 toml 拓扑逐成员重建 z42c（accumulate fresh siblings），写 canonical dist，**无 dotnet**。
- [x] `_buildStdlibCore`（xtask_stdlib.z42）：warm（z42c.driver.zpkg + stdlib 存在 && !Z42_STDLIB_CSHARP_SEED）
  → `_buildCompilerZ42ViaZ42c`（C#-free）；cold → `_csharpBuildStdlibSeed` + `_buildCompilerZ42`（C# 兜底）。
- [x] 验证：warm `xtask build stdlib` + dotnet PATH-stub 屏蔽 → 22/22 stdlib 成功，零 dotnet 调用。
- [x] CI 安全：gate 的 flip 单次 cold（fresh checkout 无种子）→ C# 路径不变；oracle 自建 C# z42c 不受影响。

## Phase B — gate 切 z42c（可逆，保留 C#）🟡
- [x] B1 cross-zpkg gate 用 z42c 编 fixtures（commit 4192423b）：`_buildPkgZ42c`/`_invokeBuildZ42c`
  经 z42vm 跑 z42c.driver.zpkg；`_testCrossZpkgCore` 先 `_buildCompilerZ42` 备 z42c。验证 2/2 绿。
- [x] B2 oracle→不动点（commit 71d13e53）：C1 使 canonical=z42c-built(gen1)，`_testZ42cSelfHostByteIdentical` 自动变 gen1==gen2；units 编译改 z42c；删 e2e byte-compare-vs-C#。验证 0 dotnet、7/7 不动点、17 units 绿。
- [~] B3 GREEN gate C#-free — **阻塞于 2 个 z42c 缺口**（已定位，待修，本轮已回退避免红门）：
  - 🔴 **z42c `--emit-zbc` 无跨包 dep 解析**（driver Main.z42:64 `IrDump.ZbcBytes(text,file)` 单文件编，无 DepScan）→ golden（调 Assert/Console 等 stdlib）emit 出 `<error>` 函数名 → 运行期 `undefined function <error>`。修：--emit-zbc 路径接 DepScan(Z42_LIBS)+deps-aware ZbcBytes（类比 BuildModuleD）。这是 golden regen 脱 dotnet 的前置。
  - 🔴 **z42c 编 closure_l3_capture Null deref**（见 [[reference_z42c_closure_l3_capture_emit_bug]]）。
  - ✅ `--emit-zbc` 跨包 dep 解析已加（commit cd28c5d5，fixpoint 过）→ stdlib 调用（Assert/Console）emit 正确。
  - 🔴🔴 **根本阻塞（关键发现 2026-06-23）**：VM golden 阶段 ~199 个 golden 须由 z42c 编译；z42c 对
    **reflection（typeof/GetType/GetFields/FullName/Type）+ closure 组合** 等特性 **emit 缺口** → `<error>`
    函数 → 运行期 `undefined function <error>`。即 **z42c 尚未达到「编译全部 golden」的特性 parity**。
    C# 有全特性支持故 golden 一直 C#-编。**结论：删 C# 不可仅靠机械改线——须先让 z42c 编通所有 golden
    （reflection emit + closure_l3_capture + 其它边缘特性），是实质 z42c 成熟度工作（多特性，独立 z42c 子系统流）。**
  - ✅ 顺带修 `_buildStdlibCore` warm 路径用绝对 release z42vm（_z42vm() 裸名不在 PATH → warm 自建 NotFound）。

## 🔴 关键结论（2026-06-23）— 全删 C# 的真实前置
两个**编译器门**（cross-zpkg、compiler-z42 自举不动点+units+e2e）已 **C#-free 验证**。但 **VM golden 门**需
z42c 编通全部 ~199 golden，而 z42c 对 reflection/closure 等特性 emit 仍出 `<error>`/崩溃 → **z42c golden-
compilation parity 是删 C# 的硬前置**（非 toolchain 机械改线可解）。在 z42c 补齐这些特性前，C# 仍是 golden
编译的唯一全特性后端，**不可删**。建议：开独立 z42c 子系统流补 reflection/closure emit → 编通全 golden →
再回到 C3 删除 + CI 清理。package/bench/cli C#-free + C# 删除 + CI/version 清理 均排在此之后。

## 🎉🎉 硬前置完全清除（2026-06-24）：z42c golden parity 全绿 + CI 全平台绿（commit da0d547b，run 28091362209 success）
z42c 现编通**全部 ~333 golden**（含此前 CI 失败的 22 个：raw string / 调用+导入默认参数 /
is-pattern 绑定 / 本地类遮蔽（ns-aware）/ lambda 返回类型 / 泛型异常 catch+arity-mangle /
实例方法组 thunk / lifted 异常表 / multicast+singlecast event 访问器合成 / in/out 参数 +
out-var 内联 / 实例字段 ++/-- 写回）。**CI 4 平台 build-and-test + bootstrap-no-csharp +
jit-consistency + 全 package + 全 downstream test + stdlib + cross-zpkg + fixpoint 全绿。**
另修：cold-start C# z42c 种子兜底回归、Windows .exe 路径、Windows running-exe relink 锁、
本地 harness 假阳性（assert-only golden 崩 stderr 被误判 PASS）。教训：改 z42c 须验
C#-built 自举 self-build（见 [[reference_selfhost_change_must_verify_self_build]]）。
**删 C# 的硬前置完全满足，main 绿。** 下一步 = Phase C 删 dotnet（CI cold-seed 切 nightly 下载，
4 平台 nightly runtime 包均带 z42c/ 种子已确认；删 src/compiler + 清 setup-dotnet）。

## 🎉 硬前置清除（2026-06-24）：z42c golden parity 130/130（历史记录）
关键结论的硬阻塞（上节）已解除——z42c 现编通全部 130 golden（C#-free，bootstrap fixpoint
gen1==gen2 7/7，零回归）。9 提交修复 extern_impl/gc_handle/chained_property/attribute×4/
multicast/local_fn/closure_l3。**删 C# 的硬前置满足。** 详见 [[reference_z42c_closure_l3_capture_emit_bug]]。

C 阶段进展（2026-06-24，commit 49ed1dfb + a65be8eb）：
- ✅ GREEN gate `_testAll` 删 `dotnet build z42.slnx` + `dotnet test z42.Tests` stage。
- ✅ golden regen `_compileCase` 切 z42c（z42vm 跑 z42c.driver.zpkg --emit-zbc，driver-home
  复用 cross-zpkg `_assembleZ42cDriverHome`，golden stdlib 依赖走 Z42_LIBS）。
- ✅ `_regenCore` 删 dotnet build C# driver。
- ✅ 删 `_buildCompiler`（dotnet slnx）+ CLI build/test compiler 入口；_testDist 改 _buildCompilerZ42。
- ✅ oracle dogfood `_testCompilerZ42Stdlib` C# seed → warm _buildStdlib（z42c）。
- 🔴 **剩余（耦合的不可逆原子步，本地部分不可验）**：
  1. `_buildStdlibCore` 冷启动 C# 兜底删除（warm-only）—— 与 CI 冷启动种子 provisioning 耦合
     （fresh checkout 须下载 nightly 种子，bootstrap-no-csharp job 已绿可复用）。
  2. **test-lib 单元编译切 z42c**（xtask_test.z42 单文件 `--emit zbc` + dir-mode `build toml`
     经 dotnet driver；在 GREEN gate stage 4，272 stdlib 测试；deps 解析 C# DepIndex→z42c Z42_LIBS，
     须跑全 272 lib 测试 C#-free 验证）。
  3. bench（dotnet driver）+ package_desktop（dotnet×6）+ install（dotnet 检查）+ platform（_driverDll）切 z42c/删。
  4. 删 src/compiler（280 .cs）+ z42.Tests + z42.slnx + _csharpBuildStdlibSeed/_driverDll/_driverProj。
  5. CI（ci.yml + bench-pr + bench-update + release.yml）setup-dotnet → bootstrap-no-csharp 种子流。
  方法：commit-no-push，User push 前复审 CI（本地不可验 packaging/CI）。

## 🔧 回归修复（2026-06-24，commit f8a16812）：cold-start C# z42c 种子兜底
**症状**：1e933950 推送后 CI 全红——所有建 stdlib 的 job（build-and-test ×4 OS +
package-{android,ios,wasm} + download-bootstrap gate vm/stdlib-jit-consistency +
compiler-z42-stdlib）同一错误 `error: no z42c seed at .../z42c.driver.zpkg`。
**根因**：d4471a85 把 `_buildCompilerZ42` 改成 C#-free 自种子（缺种子即 return 1），
但 `_buildStdlibCore` 冷启动分支仍调它当「C# z42c 兜底」。CI fresh checkout 无 z42c
种子 → 报错。即 tasks.md A1 文档化的「cold → C# 兜底」被 d4471a85 误删。
**教训**：**冷启动 C# 兜底删除 与 CI seed-provisioning 是耦合的原子步**——不能单独删
cold C#（必须同时让 CI 提供种子，否则 fresh checkout 无路可走）。d4471a85 越过了这个耦合。
**修复**：新增 `_csharpBuildCompilerZ42Seed`（镜像 `_csharpBuildStdlibSeed`，dotnet run
C# driver `build --workspace src/z42c`），冷分支改调它。warm 路径
（`_buildCompilerZ42ViaZ42c`）保持 C#-free。本地验证：z42c 重建 xtask.zpkg 成功
（warm 路径字节不变，改动隔离于 cold 分支）。**这是删 C# 前剩余原子步 #1 的正确边界确认**。

## Phase C — 删 C# + 清 dotnet（不可逆）🔴 User 裁决「staged：先切门后删」+「移除 version/dotnet 配置 + CI」(2026-06-23)
- [~] C1 build 站点全 C#-free：✅ `_buildCompilerZ42`→ViaZ42c（d4471a85）✅ `_ensureZ42cTooling`/units（71d13e53）✅ cross-zpkg（4192423b）。剩 ⬜ xtask.z42 `_regenCore`/_testAll · `_regenGolden` golden 编译 · xtask_package_desktop(dotnet×6) · xtask_bench · xtask_stdlib `_csharpBuildStdlibSeed` · xtask_cli `build/test compiler`。种子=self-seed 现有 dist / 下载 nightly。
- [ ] C2 删 `src/compiler/`（280 .cs/6.7M）+ `z42.Tests` + `z42.slnx` + `_driverDll`/`_driverProj` + cross-zpkg 残留 C# helpers。
- [ ] C3 CI 清 dotnet：ci.yml（~15 setup-dotnet@v4 + dotnet-version '10.0.x' + build/test/run）→ z42c 种子流（复用 bootstrap-no-csharp job）；bench-update/bench-pr/release.yml 同；windows dotnet-test 腿删。
- [ ] C4 验证 C#-free 闭包：bootstrap-no-csharp.sh（fixpoint 7/7）+ cross-zpkg（2/2）+ stdlib。CI 须 User push 后验证。
- [ ] C5 文档：self-hosting.md S5 完成；roadmap 自举线收官。

## 备注
- A1 默认 warm→C#-free，但 CI fresh = cold → 仍 C#；A3（bootstrap 种子）后 CI 才默认 C#-free。
- 种子 bootstrap 是 C 的硬核：C# 删后 z42c 从种子起（S4=下载 nightly；bootstrap-no-csharp.sh 已证）。
- 风险：CI 改动本地不可验，commit-no-push → User push 前复审。
