# Tasks: bootstrap-z42c-from-nightly (replace-csharp S4)

> 状态：🟡 进行中 | 创建：2026-06-22
> 子系统：`toolchain`
> 变更说明：建立脱离 C# 重建 z42c 的闭环（种子从 nightly 下载）。删 C#（S5）的前置。
> 原因：replace-csharp-compiler S4；铁律 S5 前必须有 S4 种子。
> 文档影响：self-hosting.md（S4 闭环 + 种子机制）+ replace-csharp tasks.md。

## 进度概览
- [x] 1. C#-free 重建脚本（本地可验证，种子=当前产物）✅ 全绿
- [ ] 2. nightly 携带 z42c-written 种子 + install-z42 下载
- [ ] 3. C#-free bootstrap action + CI job
- [ ] 4. flip 种子步支持 z42c 种子
- [ ] 5. 文档 + 归档

## 1. C#-free 重建脚本（先做，本地验证）✅
- [x] 1.1 `scripts/bootstrap-no-csharp.sh`：① cargo build z42vm ② 种子 z42c 编 stdlib（源，--workspace --output-dir）→ fresh stdlib ③ 种子 z42c 单 toml 逐成员编 z42c（源，runlibs=fresh-stdlib+种子 z42c，累积 fresh siblings）→ fresh z42c ④ fresh z42c 编 xtask → xtask.zpkg。全程 z42vm only，**零 dotnet**。
- [x] 1.2 不动点检查：rebuilt z42c.* vs 种子 z42c.*，cmp + 16B BLID 尾容差。
- [x] 1.3 本地验证：种子 = z42c-built selfhost-out + 当前 stdlib → 全程跑通；stdlib 22 + z42c 7 + xtask OK；**不动点 7/7 逐字节一致**（含 BLID，确定性重建）。✅ C#-free bootstrap + fixpoint OK (no dotnet)

## 2. nightly 携带 z42c 种子
- [ ] 2.1 `xtask_package_desktop.z42`：runtime artifact 加 `z42c/`（z42c-written z42c.* 7 zpkg，源自 `artifacts/build/z42c/<m>/release/dist`，**非** C# `dotnet publish`）。
- [ ] 2.2 `install-z42.sh`：下载 runtime 时把 `z42c/*.zpkg` 取进 `.z42/z42c/`（或 `.z42/libs` 合并）。
- [ ] 2.3 release-index / 版本元数据若需登记 z42c 种子条目则同步。

## 3. C#-free bootstrap action + CI
- [ ] 3.1 `.github/actions/xtask-bootstrap-no-csharp/action.yml`：cargo z42vm + install-z42（取种子）+ 调 `bootstrap-no-csharp.sh`。
- [ ] 3.2 `ci.yml` job `bootstrap-no-csharp (linux-x64)`：跑该 action；断言成功 + 不动点 + **无 dotnet**（PATH 去 dotnet 或 grep 日志）。不进 publish-nightly needs。
- [ ] 3.3 滚动：先 workflow_dispatch 触发 publish-nightly 产含 z42c 的 nightly；再让本 job 自愈转绿。

## 4. flip 种子步支持 z42c 种子
- [ ] 4.1 `_buildStdlibCore` `_csharpBuildStdlibSeed`：加 env 开关（如 `Z42_SEED_Z42C=<dir>`）→ 有则用种子 z42c 编 stdlib 种子，否则 C# DLL（默认）。C#-free 路径设该 env。
- [ ] 4.2 不回归：默认（无 env）仍 C# 种子 → 现有 gate 全绿。

## 5. 文档 + 归档
- [ ] 5.1 self-hosting.md：S4 闭环（种子来源 nightly / C#-free bootstrap 序 / 不动点 / 滚动自愈）。
- [ ] 5.2 replace-csharp tasks.md：S4 勾选。
- [ ] 5.3 归档 + 释放 toolchain 锁 + commit。

## 备注
- 鸡蛋：种子由 source-bootstrapped publish-nightly（用 C#）产，S4 期允许；S5 删 C# 后 publish-nightly 改用前夜种子（S5 收尾）。
- `z42c build --workspace` 自建 z42c E0402 wrinkle → 本 change 用单 toml 拓扑绕过；根因修复留独立 change。
- 验证：步骤 1（本地种子）本机可全绿；步骤 2-3（真下载）仅 CI。
