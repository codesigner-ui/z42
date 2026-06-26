# Tasks: replace-csharp-compiler (roadmap)

> 状态：🟢 已完成 | 创建：2026-06-21 | 完成：2026-06-26
> 子系统：跨 `z42c` + `toolchain` + `compiler`（分阶段逐个占用）
> **roadmap umbrella change**：S0–S5 各阶段均由独立子 change 落地并已各自归档；本伞 change 收官归档。

## 进度概览
- [x] S0 `build --workspace`（z42c-build-workspace ✅ 归档 2026-06-21；含 M2 per-member drop-in）
- [x] S2 叶子编译点切 z42c：S2.1/S2.2 ✅；S2.3（cross-zpkg/test-unit 切换）✅ 由 **port-z42c-impl-block**（归档 2026-06-23，z42c 端口 `impl Trait for Type` 跨包，两 cross-zpkg fixture z42c 全绿）+ remove-dotnet stdlib unit 切换 共同解锁完成
- [x] S3 stdlib dogfood（z42c 接管生产 stdlib build）：✅ **已落地 2026-06-22**（dogfood-z42c-stdlib-build 归档）。`_buildStdlibCore` 翻转 z42c 接管，full gate 全绿（z42c-built stdlib 272/272）。dogfood 暴露并修复 8 个 z42c/stdlib bug（各独立归档）
- [x] S1 z42c apphost（分发）✅ 由 **package_desktop C#-free**（commit 7870db60）落地：`bin/z42c` = 原生 apphost trampoline 跑 `programs/z42c/z42c.driver.zpkg`（7 z42c.* 种子 colocated，VM colocated-deps 解析）；package release 验证 OK
- [x] S4 自举闭环 + 下载种子（bootstrap-z42c-from-nightly ✅ 归档 2026-06-22）：脱 C# 重建 z42c 闭环成立。`scripts/bootstrap-no-csharp.sh`（z42vm only 重建 stdlib+z42c+xtask，不动点 7/7 逐字节一致）+ runtime package 携 z42c/ 种子 + CI job `bootstrap-no-csharp (linux-x64)` **端到端绿**（下载携种子 nightly → dotnet PATH-stub 屏蔽 → C#-free 重建 + 不动点）。滚动自愈走通。flip `_buildStdlibCore` 脱 C# 移交 S5
- [x] S5 删 C# + 迁新家 ✅ 由 **remove-dotnet-from-builds**（归档 2026-06-26）落地：删 src/compiler 旧 280 .cs + z42.Tests + z42.slnx；src/z42c 重命名为 src/compiler；全 5 workflow 清 setup-dotnet；dotnet 实质完全移除

## S2（当前）：z42c 进 CI 验证门 + 叶子切换
- [x] S2.1 xtask `test compiler-z42-stdlib`：自足（C# 建 stdlib 种子 + tooling + z42c）→ z42c `build --workspace` 编全 22 stdlib 到私有 dogfood 目录 → 功能验证（z42c-7包 + z42c-built-stdlib 跑 z42c --emit-zbc）。**全私有目录 + File.Copy**（避开 _linkAll hard-link inode 别名污染）。本地通过：22 pkgs + emit OK
  - 踩坑记录：① hard-link 共享 alllibs + rebuild → per-member 源 0 字节（换 copy）；② 陈旧 built stdlib 缺新 extern GetCommandLineArgs（自建 fresh stdlib 种子）
- [x] S2.2 CI job `compiler-z42-stdlib (linux-x64)`（仿 stdlib-jit-consistency；不进 publish-nightly needs）
- [x] S2.3 切 test-unit / cross-zpkg compile 到 z42c —— ✅ **解锁完成**：阻塞的 impl-block 特性由 **port-z42c-impl-block** 端口（归档 2026-06-23，parse ImplDecl + 同包 binder + IMPL 段；两 cross-zpkg fixture z42c 全绿）；test-unit 切换由 remove-dotnet stdlib unit 切换完成。

## S3：stdlib dogfood ✅ 完成（dogfood-z42c-stdlib-build，归档 2026-06-22）
- [x] S3.0 可行性验证：z42c M2 per-member 编全 22 库 + z42c-built stdlib 272/272（`test stdlib --no-build`）
- [x] S3.0b 顺带修复 z42c TSIG 可选参数 bug（fix-z42c-tsig-optional-params）：12 delegate/event/weak golden 编译失败 → 修 `ExportedTypeExtractor._requiredCount` → 全绿
- [x] S3.1 `_buildStdlibCore` 改 z42c 接管 —— ✅ 已落地（C# 种子 → build z42c → run-libs → z42c interp 重编 per-member → verify + flat view）
- [x] S3.2 GREEN gate —— ✅ 全绿（dogfood 暴露并修复 8 个 z42c/stdlib bug）：
  - ✅ **TSIG 可选参数**：fix-z42c-tsig-optional-params（归档）
  - ✅ **multicast aggregate**：fix-z42c-generic-ctor-arity（归档，`new C<T>()` arity-overload）
  - ✅ **BLID sidecar**：port-z42c-strip-symbols（归档，z42c 产 `.zsym`+BLID；z42c.driver FULLY byte-identical 含 BLID）
  - ✅ **blake3 多块**：fix-blake3-multichunk-root-flag（归档，strip build_id 暴露 z42.crypto >1024B BLAKE3 错误）
  - ✅ **compression `[Native]` named-entry**：fix-z42c-native-named-entry（归档；18→8）
  - ✅ **cross-ns 静态调用**：fix-z42c-static-call-cross-ns（归档；Zip→Deflate；8→4）
  - ✅ **静态字段 mutation 不持久**：fix-z42c-static-field-assign（归档；`_emitAssign` 加 StaticSetInstr 分支）
  - ✅ **ctor `: this(...)` 委托**：fix-z42c-ctor-this-delegation（归档；MethodDecl 加 IsThisInit + TypeChecker 按位选 TargetCls）
  - ⓘ **「blake3 多块 codegen」实为回归测试 golden 误写**——已改正，无 codegen bug
- [x] S3 完成：full gate 全绿（C# 1571/vm 169+165/cross-zpkg 2/stdlib 272 z42c-built/compiler-z42 7/7+17 units+e2e）

## S1：z42c apphost（分发）
- [x] S1.1 `_produceApphost` 复用 → 产 `z42c` 原生（payload=z42c.driver.zpkg + colocated z42c.* deps）✅ package_desktop 7870db60
- [x] S1.2 SDK 布局 + 运行时解析验证 ✅ package release 验证 OK（colocated-deps 解析 driver 6 兄弟 + stdlib via libs/）

## S4：自举闭环 + 种子
- [x] S4.1 z42c build z42c（脱离 C#，用 prebuilt z42c 种子）✅ bootstrap-z42c-from-nightly（归档 2026-06-22）
- [x] S4.2 种子机制（下载 nightly）+ fresh-checkout bootstrap 验证 ✅ runtime package 携 z42c/ 种子 + CI job 端到端绿

## S5：删 C#
- [x] S5.1 全调用点迁 z42c（~11 xtask 脚本）+ C# Tests 删除 ✅ remove-dotnet-from-builds（归档 2026-06-26）
- [x] S5.2 删 src/compiler/（旧 C#）+ src/z42c 重命名为 src/compiler + CI 清 dotnet ✅ remove-dotnet-from-builds

## 备注
- 铁律：S5 前必须有 S4 种子（脱离 C# 重建 z42c）。
- 整包 zpkg vs C# 字节差异 pre-existing（gate 只验 --emit-zbc），功能正确即可，不追整包 byte-identical。
