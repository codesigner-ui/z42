# Tasks: replace-csharp-compiler (roadmap)

> 状态：🟡 进行中 | 创建：2026-06-21
> 子系统：跨 `z42c` + `toolchain` + `compiler`（分阶段逐个占用）

## 进度概览
- [x] S0 `build --workspace`（z42c-build-workspace ✅ 归档 2026-06-21）
- [ ] S2 叶子编译点切 z42c（先加 z42c-stdlib 验证门，零风险）
- [ ] S3 stdlib dogfood（z42c 重编覆盖）
- [ ] S1 z42c apphost（分发）
- [ ] S4 自举闭环 + committed/下载种子
- [ ] S5 删 C# + 迁新家

## S2（当前）：z42c 进 CI 验证门 + 叶子切换
- [x] S2.1 xtask `test compiler-z42-stdlib`：自足（C# 建 stdlib 种子 + tooling + z42c）→ z42c `build --workspace` 编全 22 stdlib 到私有 dogfood 目录 → 功能验证（z42c-7包 + z42c-built-stdlib 跑 z42c --emit-zbc）。**全私有目录 + File.Copy**（避开 _linkAll hard-link inode 别名污染）。本地通过：22 pkgs + emit OK
  - 踩坑记录：① hard-link 共享 alllibs + rebuild → per-member 源 0 字节（换 copy）；② 陈旧 built stdlib 缺新 extern GetCommandLineArgs（自建 fresh stdlib 种子）
- [ ] S2.2 CI job 挂该验证（linux-x64，仿 stdlib-jit-consistency）
- [ ] S2.3 （验证稳定后）切 test-unit compile / cross-zpkg compile 到 z42c

## S3：stdlib dogfood
- [ ] S3.1 xtask_stdlib：C# 种子 stdlib → build z42c → z42c 重编 stdlib 覆盖 flat（temp→swap 避免读写同目录）
- [ ] S3.2 GREEN gate 全绿（stdlib 测试跑在 z42c-built libs 上）

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
