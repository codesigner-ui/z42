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
C# driver `build --workspace src/compiler`），冷分支改调它。warm 路径
（`_buildCompilerZ42ViaZ42c`）保持 C#-free。本地验证：z42c 重建 xtask.zpkg 成功
（warm 路径字节不变，改动隔离于 cold 分支）。**这是删 C# 前剩余原子步 #1 的正确边界确认**。

## Phase C — 删 C# + 清 dotnet（不可逆）🔴 User 裁决「staged：先切门后删」+「移除 version/dotnet 配置 + CI」(2026-06-23)
- [~] C1 build 站点全 C#-free：✅ `_buildCompilerZ42`→ViaZ42c（d4471a85）✅ `_ensureZ42cTooling`/units（71d13e53）✅ cross-zpkg（4192423b）✅ **xtask_package_desktop(dotnet×6 全清)**：bin/z42c 由 dotnet publish C# → apphost trampoline 跑 `programs/z42c/z42c.driver.zpkg`（7 z42c.* 种子 colocated，VM colocated-deps 解析兄弟 + stdlib via libs/）；5× `dotnet run z42.Driver build`（workloads + launcher）→ `_z42cBuildToml`（host z42vm 跑 z42c.driver.zpkg）。**正交前置修复**：z42c 此前忽略 `[build] dist_dir`/`output_dir`（恒写 `<projectDir>/dist`），致 workload/launcher/xtask 产物落错位置——补 z42c.project 解析 `[build]` 三目录字段 + Main.`_resolveDistDir` 级联（镜像 C# CentralizedBuildLayout.ResolveSingleProject + `${output_dir}` 替换）。验证：dist_dir/output_dir 正确落位 + fixpoint gen1==gen2 7/7 + C# cold-seed→z42c gen1 7/7（bootstrap-safe）。剩 ⬜ xtask.z42 `_regenCore`/_testAll · `_regenGolden` golden 编译 · xtask_bench · xtask_stdlib `_csharpBuildStdlibSeed` · xtask_cli `build/test compiler`。种子=self-seed 现有 dist / 下载 nightly。
### 🔴 关键发现（2026-06-24，commit 026d36b4）：z42c [Test] 单元 false-green + TIDX 缺口
package_desktop 的 dist_dir 修复（7870db60）把 z42c 单工程**默认** dist 从 `<projectDir>/dist`
改成 C# 的 `artifacts/<profile>/dist` → CI build-and-test ×3 红（stage `test compiler-z42`）。两个根因：
1. **默认越界**：compiler-z42 的 e2e 用 6+ 个无 `[build]` 临时工程 build 后读 `<dir>/dist`，依赖旧默认。
   修：`_resolveDistDir` 仅在 toml **显式**设 output_dir/dist_dir 时走级联，否则保留 `<projectDir>/dist`。
   package workload（显式 dist_dir）仍落 libs/，打包修复不受影响。
2. **z42c [Test] 单元自 71d13e53 起从未真正运行（false-green）**：旧 z42c 忽略单元 toml 的 output_dir
   → 写 `<projectDir>/dist`，而 runner 在 `artifacts/.../tests/<unit>/dist`（镜像）glob → 找到 0 个 zpkg
   → run 循环 0 次 → "all passed" 假绿。dist_dir 修复让单元落到镜像后**暴露真相**：z42c-built 测试 zpkg
   **缺 TIDX（test-index）段** —— runner 靠 TIDX 发现 [Test] 方法（仅 C# emit；z42c 把内建 [Test]/
   [Benchmark] 仅从反射合成里排除，无任何 TIDX builder）→ runner exit 3 无输出。
   **诚实临时处理**：z42c 仍 BUILD 每个单元（验证可编译）+ 0-zpkg 守卫（防再 false-green）+ run 段
   loud SKIP（注明 port-z42c-tidx）。**根因待办 → 见下「z42c TIDX 移植」**。

### 🆕 新增前置：z42c TIDX 段移植（port-z42c-tidx）
- z42c 需 emit TIDX 段（[Test]/[Benchmark]/[Ignore]/[Skip]/[ShouldThrow]/[Timeout] → TestEntry → TIDX
  section → 嵌入每模块 MODS），镜像 C# `TestEntry.cs` + `ZbcWriter.BuildTidxSection`（layout 已定义，
  zbc reader Rust 端已能读，**无需 format bump**——只是 z42c 不发）。z42c 已有方法 attr 数据（SIGS attr-ref
  反射）→ 检测 [Test] 可行。风险低（additive 段；z42c 成员自身无 [Test] → 其 zpkg 不变 → fixpoint 不破）。
  oracle = C#-built 单元 zpkg 的 TIDX 字节。完成后翻 loud-skip 回真 runner 调用 → z42c [Test] 覆盖恢复 + 全 C#-free。

### 🔀 协同发现（2026-06-25）：TIDX 移植被 retire-test-runner 取代 + CI 优化方向
- **TIDX 移植不再需要**：并行 change `retire-test-runner` 删 Rust `z42-test-runner`，改 `z42.test` 反射驱动
  `TestRunner`（`MethodInfo`/`Method.Invoke` 发现 `[Test]`，**非 TIDX**）+ `z42b test`/`z42b bench` 宿主。
  z42c 已 emit attr-ref 反射 → 反射式 runner 落地后 z42c-built [Test] 单元自然可发现，**无需 port-z42c-tidx**。
  ⇒ port-z42c-tidx 作废；z42c [Test] 单元的 loud-skip（commit 026d36b4）待 retire-test-runner GA 后由反射 runner 接管。
  **边界**：test-runner / bench 的 dotnet 移除归 retire-test-runner（并行 change）；本 change 不碰这两块，避免 shared-worktree race。
- **CI 慢的根因 + JIT 实验结论**：`test all` ~38m 主因 = z42c **解释执行**逐文件编译（golden regen ~199 / stdlib 22 /
  z42c 自建 ×2-3）。试过把 8 处 z42c-compile `--mode interp`→jit（`_z42cMode()` helper）：单次大编译 jit 快 ~3×
  且字节一致（fixpoint 在 x64 CI 已证 jit-built==interp-built），**但全 gate jit 反而更慢**——gate 把 z42c spawn
  数百次短进程，每进程 jit 预热成本 > 执行节省（warmup-bound）。已 revert（未 push）。CI 真正提速杠杆 =
  ① 批量编译（z42c.driver 一进程编多文件，摊销启动+预热）② 并行（跨核/拆 CI job）③ AOT z42c.driver。均为后续独立工作。
- [x] C2 预备：删 cross-zpkg 残留 C# helpers（`_buildPkg`/`_invokeBuild` dead code，commit 见下）—— cross-zpkg 早已走 z42c 路径，C# 版无调用方。验证 2/2 绿。
### 🟢 TIDX 落地（2026-06-25）：z42c [Test] 单元可跑 + ShouldThrow（commits cd53d0f3 + 6f91ceaf，CI 全绿）
- **z42c emit TIDX 段**（cd53d0f3）：byte-identical to C#；17 个 z42c [Test] 单元 compile+run+pass（flip 掉 026d36b4 的 loud-skip）；fixpoint 7/7；C# cold-seed safe。顺带修 3 个 stale record-dump 测试（z42c 位置式 record 降级 field+ctor 镜像 C#，测试早于降级、因单元从不跑而漏）。
- **z42c parse `[Attr<E>]` + emit `[ShouldThrow<E>]` chain**（6f91ceaf）：z42c 之前把 `<E>` 误当 `<` 运算符 → 垃圾 AST + 丢掉所有 ShouldThrow 函数。修 Parser 捕获 `<E>` + IrGen 建 `;`-分隔后代链（Ordinal 排序去 hash-order 不确定）。dogfood 3 个 ShouldThrow 测试过 + 匹配 C#，TIDX byte-identical。

### 🟢 stdlib 单元切 z42c — 已落地（2026-06-25，switch + 3 codegen bug 修复全绿）

**最终状态**：xtask_test.z42（`_compilePrep`/`_runUnitsBatched`/`_testLibCore`）切 z42c（z42vm + z42c.driver.zpkg
+ alllibs flat[stdlib+z42c 7 siblings]，Z42_LIBS 单目录；`dotnet z42c.dll`/`dotnet build z42.Driver` 全删）。
全 272 跑暴露 3 个 z42c codegen bug，逐个 oracle-diff（C#-编 vs z42c-编）定位 + 修：

1. **✅ nullable 局部声明 `T? x = e` 被误解析成三元**（parser `_isVarDeclStart`）：4 个类型形态（`Box? a` /
   `Box[]? a` / `Ns.Type? a` / `Box<int>? a`）的 var-decl lookahead 全漏跳 `?` 后缀 → 落表达式路径 →
   `Box ? a=e : …` ternary 误解析 → IR `BrCond const.null` + 读局部变 `const.null` 占位 → BrCond Null 崩。
   修：4 个检测分支补 `?` 跳过。解 z42.net 50/50（ipaddress TryParse 3 个）等所有 nullable-local 用例。
2. **✅ 数组字面量尾逗号多算元素**（parser `new T[]{...}` 元素循环）：消费逗号后无条件 `_parseExpr` →
   `{1,2,3,}` 把 `}` 当表达式多解析一个 `const.null` 元素 → byte[] 长度 +1。修：吃逗号后若紧跟 `}` break。
   解 io.binary 6/6（byte_array_constructor + reader_position）。
3. **✅ dogfood `test_bencher_stat_invariants`**：bench body lambda 内 `Math.Sqrt` 但 dogfood **没 `using
   Std.Math`、z42.test 也没声明 z42.math**。两编译器都不能无 import 正确解析 `Math.Sqrt`（C# 错限定成
   `T.Math.Sqrt`；z42c 静默误编 instance-on-error → VCall-null 崩）。全量 dogfood 在 C# 下侥幸过、z42c 崩。
   **决定（User 裁决 2026-06-25）**：给 dogfood 加 `using Std.Math`（用了就该 import；两编译器都过）。
   **🟡 tracked z42c 健壮性 bug**：z42c `--emit-zbc` 单文件模式对**未解析的 static 调用**应像 project-build
   那样清晰报 `E0401: undefined: <Class>`，而非静默 emit instance-on-error → VCall-null 运行期崩。根因链见下
   「旧诊断」段（ns-末段同名导入类 `Std.Math.Math` 注册 + instance-on-error 兜底）。单独 change 修。

验证：fixpoint 7/7 gen1==gen2（C#-free）+ VM goldens（interp+jit）+ 全 272 C#-free 全绿。

---
### 旧诊断（被上面取代，留作 tracked 健壮性 bug 的根因参考）

> stdlib-switch WIP 已 revert（2026-06-25）—— 见上方 🟢 段，已重做并落地。
切换代码（xtask_test._compilePrep/_runUnitsBatched/_testLibCore + xtask_bench → z42c）已写好+验证机制正确（z42.math 13/13；z42c-编 vs C#-编单元 **byte-identical**，见 blake3/binary_basic 实测），但**全 272 跑暴露真 z42c codegen 缺口**：
- **环境性假失败（非 z42c 问题）**：blake3 multi-chunk + 2 个 binary-stream —— 隔离实测 z42c-编 == C#-编 byte-identical 且 pass；全跑失败是**本地超载机器 + churned/stale artifacts**所致，clean CI 不会触发。
- **真 z42c codegen 缺口（z42.test/dogfood 2 个）**：`test_testio_capture_nested_stdout`（TestIO 嵌套捕获）+ `test_bencher_stat_invariants`（Bencher 统计）—— z42c-编 fail / C#-编 pass，旧种子也 fail（pre-existing，非 ShouldThrow 引入）。
- **进度（2026-06-25 grind）**：
  - 🔴→↩️ **嵌套闭包捕获**（feefef4d 已 **revert** 03e08089）：`_bindLambda` 不 save/restore 外层 lambda frame
    → 内层 lambda 清空外层捕获 → 外层被当无捕获 emit LoadFn（无 env）→ array_get 落到实参崩溃。简单 save/restore
    修了「外层自身捕获丢失」，但**破了 `closure_l3_capture` golden**（CI 全平台红 + vm-jit）：旧共享 `_lambdaCaps`
    意外做了 **transitive 透传**（内层捕获 enclosing 变量 → 外层继承捕获），save/restore 把它弄丢了。golden case 4
    `f=()=>{ g=()=>k2*2; return g(); }`：f 不直接用 k2，仅 g 用 → f 必须透传捕获 k2。
    **完整修复要二者兼顾**：① save/restore 外层 frame ② 内层捕获中**不在外层 locals** 的变量透传进外层捕获。
    **教训**：改 z42c codegen push 前必跑 **VM goldens（test vm）**，不能只 fixpoint（fixpoint 只覆盖 z42c 自身源码，
    无此嵌套模式 → 漏网）。见 [[reference_selfhost_change_must_verify_self_build]]（已更新）。重做留待 stdlib switch 时连 golden 一起验。
  - 🔴 **跨包 `Math.Sqrt` → vcall null**（深度 pinpoint，待修，阻 dogfood `test_bencher_stat_invariants`）：
    精确根因链：① `Math` 类在 **namespace `Std.Math`**（全名 `Std.Math.Math`，**类名 == 命名空间末段**）
    → z42c 未把它注册进符号表 Classes（`HasClass("Math")` = **false**；`Console`=`Std.IO.Console` 正常注册，故
    `Console.WriteLine` 同为 static extern 却能用）② HasClass false → `_bindMemberCall`（TypeChecker:1210-1224）
    静态路径不命中 → 落 instance path → 收者 = `_bindExpr(Math)` = **BoundError**（dump-bound: `(call-inst (error) Sqrt)`）
    ③ emit instance-on-error 走 IC-2 兜底 `Deps.GetInstance("Sqrt", arity)`，但 DependencyIndex.AddModule
    **只把 `!IsStatic` 方法放进 bare-name Instances 索引**（DependencyIndex.z42:76）→ static extern Sqrt 不在 →
    GetInstance miss → `VCall recv.Sqrt`，recv=null → 崩。
    **修复方向**：让 z42c 正确注册 namespace 末段同名的导入类（`Std.Math.Math`）进 Classes → `Math.Sqrt` 绑成
    static → emit 走 `Deps.GetStatic("Math","Sqrt$1")`（Statics 索引**有** Sqrt）→ 正确 Call。即根在
    imported-class 注册（SymbolCollector/ImportedSymbolLoader 对 class-name==ns-last-segment 的处理），
    **非** DepIndex 也非 emit。注：dogfood 无 `using Std.Math` 却能编 Math.Sqrt（z42.test 声明 z42.math 依赖
    自动引入）但仍 vcall-null —— 确认是注册/解析问题非纯 using 问题。深度 codegen，建议干净机器专注修。
- **下一步**：① 修跨包 static extern 调用 bug ② 在**干净机器**跑全 272 拿完整真缺口清单（blake3/binary 等环境假失败需排除）③ 逐个修剩余 z42c codegen 缺口 ④ 缺口清零后 re-apply stdlib switch（代码在 commit 历史可恢复）+ 全 272 C#-free 绿 → commit。这是删 src/compiler 的硬前置。

- [ ] C2 删 `src/compiler/`（280 .cs/6.7M）+ `z42.Tests` + `z42.slnx` + `_driverDll`/`_driverProj`。
- [ ] C3 CI 清 dotnet：ci.yml（~15 setup-dotnet@v4 + dotnet-version '10.0.x' + build/test/run）→ z42c 种子流（复用 bootstrap-no-csharp job）；bench-update/bench-pr/release.yml 同；windows dotnet-test 腿删。
- [ ] C4 验证 C#-free 闭包：bootstrap-no-csharp.sh（fixpoint 7/7）+ cross-zpkg（2/2）+ stdlib。CI 须 User push 后验证。
- [ ] C5 文档：self-hosting.md S5 完成；roadmap 自举线收官。

## 备注
- A1 默认 warm→C#-free，但 CI fresh = cold → 仍 C#；A3（bootstrap 种子）后 CI 才默认 C#-free。
- 种子 bootstrap 是 C 的硬核：C# 删后 z42c 从种子起（S4=下载 nightly；bootstrap-no-csharp.sh 已证）。
- 风险：CI 改动本地不可验，commit-no-push → User push 前复审。
