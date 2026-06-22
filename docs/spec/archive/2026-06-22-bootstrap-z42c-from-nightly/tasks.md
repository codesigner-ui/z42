# Tasks: bootstrap-z42c-from-nightly (replace-csharp S4)

> 状态：🟢 已完成 | 完成：2026-06-22 | 创建：2026-06-22
> 子系统：`toolchain`
>
> **✅ S4 端到端验证（CI run 27957753215）**：`bootstrap-no-csharp (linux-x64)` 绿（6m7s）——
> 下载携 z42c/ 种子的 nightly runtime package → 组装 seed（33 zpkg）→ dotnet 被 PATH stub
> 屏蔽（任何 C# 调用即失败）→ z42c 重建 stdlib + z42c + xtask → 不动点 7/7 逐字节一致。
> 滚动自愈已走通：旧 nightly 无种子时 transient 失败（明确 error）→ push 触发 publish-nightly
> republish 携 z42c/ → 重跑转绿。**脱 C# 重建 z42c 闭环成立（S5 删 C# 前置达成）。**
> 变更说明：建立脱离 C# 重建 z42c 的闭环（种子从 nightly 下载）。删 C#（S5）的前置。
> 原因：replace-csharp-compiler S4；铁律 S5 前必须有 S4 种子。
> 文档影响：self-hosting.md（S4 闭环 + 种子机制）+ replace-csharp tasks.md。

## 进度概览
- [x] 1. C#-free 重建脚本（本地可验证，种子=当前产物）✅ 全绿
- [x] 2. nightly 携带 z42c-written 种子（runtime package z42c/；download 直取，install-z42 N/A）
- [x] 3. C#-free bootstrap CI job（gh download nightly runtime → seed → 脚本；dotnet PATH-stub 屏蔽）✅ CI 绿
- [~] 4. flip 种子步 C#-free —— 移交 S5
- [x] 5. 文档（self-hosting.md S4 段 + replace-csharp tasks）+ 归档；滚动自愈已走通

## 1. C#-free 重建脚本（先做，本地验证）✅
- [x] 1.1 `scripts/bootstrap-no-csharp.sh`：① cargo build z42vm ② 种子 z42c 编 stdlib（源，--workspace --output-dir）→ fresh stdlib ③ 种子 z42c 单 toml 逐成员编 z42c（源，runlibs=fresh-stdlib+种子 z42c，累积 fresh siblings）→ fresh z42c ④ fresh z42c 编 xtask → xtask.zpkg。全程 z42vm only，**零 dotnet**。
- [x] 1.2 不动点检查：rebuilt z42c.* vs 种子 z42c.*，cmp + 16B BLID 尾容差。
- [x] 1.3 本地验证：种子 = z42c-built selfhost-out + 当前 stdlib → 全程跑通；stdlib 22 + z42c 7 + xtask OK；**不动点 7/7 逐字节一致**（含 BLID，确定性重建）。✅ C#-free bootstrap + fixpoint OK (no dotnet)

## 2. nightly 携带 z42c 种子
- [x] 2.1 `xtask_package_desktop.z42` `_buildRuntimePackage`：runtime artifact 加 `z42c/`（z42c-written z42c.* 7 zpkg，源自 `artifacts/build/z42c/<m>/release/dist`；先 `_buildCompilerZ42` 确保已建）。runtime package = z42vm + libs(stdlib) + native + **z42c/ 种子** = 完整 C#-free 自举种子。
- [~] 2.2 install-z42.sh —— **N/A**：bootstrap job 直接 `gh release download nightly z42-runtime-nightly-<rid>.tar.gz` 取 runtime package（含 z42c/+libs/），不经 install-z42（install-z42 取 SDK 包；避免改 SDK + install 逻辑）。
- [~] 2.3 release-index —— **N/A**：runtime package 已是既有 release asset（split-release-runtime-package），z42c/ 随包发布，无需额外条目。

## 3. C#-free bootstrap action + CI
- [x] 3.1/3.2 `ci.yml` job `bootstrap-no-csharp (linux-x64)`：**无 setup-dotnet**（dotnet 缺席 = 结构性无 C#）；`gh release download nightly` 取 runtime package → 组装 seed（z42c/+libs/）→ `bash scripts/bootstrap-no-csharp.sh <seed>`；显式 guard `command -v dotnet` 必须缺席。不进 publish-nightly needs（无死锁）。
- [x] 3.3 滚动已走通：旧 nightly transient 失败 → push 触发 publish-nightly republish 携 z42c/（验证 nightly runtime asset 含 z42c/ 7 zpkg）→ 重跑 bootstrap-no-csharp 绿。
- [x] 3.4 guard 修正：`command -v dotnet`（ubuntu-latest 基镜像自带 dotnet → 误失败）改 PATH-stub 屏蔽（dotnet 被调用即 exit 97）—— 证明"从不调用"而非"不存在"。

## 4. flip 种子步支持 z42c 种子 —— **延后到 S5**
- [~] 4.1/4.2 `_buildStdlibCore` C#-free 化（env 选 z42c 种子 vs C# DLL）属生产 `xtask build stdlib` 脱 C#，是 **S5**（删 C# 时）关注点；S4 的 C#-free 闭环由 `bootstrap-no-csharp.sh` 直接建 stdlib 达成，不经 flip。移交 S5。

## 5. 文档 + 归档
- [x] 5.1 self-hosting.md：S4 闭环（种子来源 nightly / C#-free bootstrap 序 / 不动点 / 滚动自愈）。
- [x] 5.2 replace-csharp tasks.md：S4 勾选。
- [x] 5.3 归档 + 释放 toolchain 锁 + commit。

## 备注
- 鸡蛋：种子由 source-bootstrapped publish-nightly（用 C#）产，S4 期允许；S5 删 C# 后 publish-nightly 改用前夜种子（S5 收尾）。
- `z42c build --workspace` 自建 z42c E0402 wrinkle → 本 change 用单 toml 拓扑绕过；根因修复留独立 change。
- 验证：步骤 1（本地种子）本机可全绿；步骤 2-3（真下载）仅 CI。
