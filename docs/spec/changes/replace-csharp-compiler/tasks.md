# Tasks: replace-csharp-compiler (roadmap)

> 状态：🟡 进行中 | 创建：2026-06-21
> 子系统：跨 `z42c` + `toolchain` + `compiler`（分阶段逐个占用）

## 进度概览
- [x] S0 `build --workspace`（z42c-build-workspace ✅ 归档 2026-06-21；含 M2 per-member drop-in）
- [~] S2 叶子编译点切 z42c：S2.1/S2.2 ✅；S2.3（cross-zpkg/test-unit 切换）**阻塞**于 z42c 缺 impl-block 特性（独立 workstream，非 leaf switch）
- [~] S3 stdlib dogfood（z42c 接管生产 stdlib build）：可行性已验证（M2 + 272 pass）+ 顺带修 z42c TSIG 可选参数 bug；**生产接管阻塞**于 z42c 两个 parity gap（BLID sidecar + multicast aggregate 行为）→ dogfood-z42c-stdlib-build 🔴 blocked
- [ ] S1 z42c apphost（分发）
- [ ] S4 自举闭环 + committed/下载种子
- [ ] S5 删 C# + 迁新家

## S2（当前）：z42c 进 CI 验证门 + 叶子切换
- [x] S2.1 xtask `test compiler-z42-stdlib`：自足（C# 建 stdlib 种子 + tooling + z42c）→ z42c `build --workspace` 编全 22 stdlib 到私有 dogfood 目录 → 功能验证（z42c-7包 + z42c-built-stdlib 跑 z42c --emit-zbc）。**全私有目录 + File.Copy**（避开 _linkAll hard-link inode 别名污染）。本地通过：22 pkgs + emit OK
  - 踩坑记录：① hard-link 共享 alllibs + rebuild → per-member 源 0 字节（换 copy）；② 陈旧 built stdlib 缺新 extern GetCommandLineArgs（自建 fresh stdlib 种子）
- [x] S2.2 CI job `compiler-z42-stdlib (linux-x64)`（仿 stdlib-jit-consistency；不进 publish-nightly needs）
- [~] S2.3 切 test-unit / cross-zpkg compile 到 z42c —— **阻塞**：cross-zpkg 用 `impl Trait for Type`（跨包 trait impl），z42c 尚未端口该特性 → 需先给 z42c 端口 impl-block（parse+bind+IMPL section），非 build-invocation 切换。独立 workstream，待 z42c 特性补齐后回来。

## S3：stdlib dogfood 🔴 BLOCKED（dogfood-z42c-stdlib-build；reverted，未落地）
- [x] S3.0 可行性验证：z42c M2 per-member 编全 22 库 + z42c-built stdlib 272/272（`test stdlib --no-build`）
- [x] S3.0b 顺带修复 z42c TSIG 可选参数 bug（fix-z42c-tsig-optional-params）：12 delegate/event/weak golden 编译失败 → 修 `ExportedTypeExtractor._requiredCount` → 全绿
- [~] S3.1 `_buildStdlibCore` 改 z42c 接管 —— 已实现并验证 build 成功，但 **reverted**（full gate 暴露阻塞）
- [~] S3.2 GREEN gate —— **阻塞收窄到 1 项**（解了 4 个 z42c/stdlib bug）：
  - ✅ **TSIG 可选参数**：fix-z42c-tsig-optional-params（归档）
  - ✅ **multicast aggregate**：fix-z42c-generic-ctor-arity（归档，`new C<T>()` arity-overload）
  - ✅ **BLID sidecar**：port-z42c-strip-symbols（归档，z42c 产 `.zsym`+BLID；z42c.driver FULLY byte-identical 含 BLID）
  - ✅ **blake3 多块**：fix-blake3-multichunk-root-flag（归档，strip build_id 暴露 z42.crypto >1024B BLAKE3 错误）
  - 🔴 **z42.compression `[Native]`（唯一剩余）**：z42c-built（strip）z42.compression `_CompressRaw` 运行时 undefined（18 test）。其余全绿（C# tests/vm/cross-zpkg/254-272 stdlib）。见 self-hosting-future-z42c-compression-native-binding
- **前置**：compression 解除后翻转 `_buildStdlibCore` + 重跑 full gate

## S1：z42c apphost（分发）
- [ ] S1.1 `_produceApphost` 复用 → 产 `z42c` 原生（payload=z42c.driver.zpkg + colocated z42c.* deps）
- [ ] S1.2 SDK 布局 + 运行时解析验证

## S4：自举闭环 + 种子
- [ ] S4.1 z42c build z42c（脱离 C#，用 prebuilt z42c 种子）
- [ ] S4.2 种子机制（committed z42c.zpkg 或下载 nightly）+ fresh-checkout bootstrap 验证

## S5：删 C#
- [ ] S5.1 全调用点迁 z42c（~11 xtask 脚本）+ C# Tests 去留决策
- [ ] S5.2 删 src/compiler/ + src/z42c/ 迁新家 + CI 更新

## 备注
- 铁律：S5 前必须有 S4 种子（脱离 C# 重建 z42c）。
- 整包 zpkg vs C# 字节差异 pre-existing（gate 只验 --emit-zbc），功能正确即可，不追整包 byte-identical。
