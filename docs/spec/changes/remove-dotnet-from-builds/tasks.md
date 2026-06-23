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
  - 已验证可行的部分：drop dotnet test stage + _testAll build wave C#-free 结构成立；待上述 2 缺口修复后重做 _regenGolden→z42c + _testAll。
  - ✅ 顺带修 `_buildStdlibCore` warm 路径用绝对 release z42vm（_z42vm() 裸名不在 PATH → warm 自建 NotFound）。

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
