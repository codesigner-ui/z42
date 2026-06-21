# Tasks: dogfood-z42c-stdlib-build (replace-csharp S3)

> 状态：🔴 BLOCKED（实现+验证可行性，full gate 暴露 z42c parity gap → revert，未落地）| 创建：2026-06-21
> 子系统：`toolchain`（**锁已释放** —— 无代码落地，reverted；阻塞在 z42c 侧）
> 变更说明：生产 stdlib 构建由 z42c 接管（C# 种子 → z42c M2 per-member 重编覆盖）。
> 原因：replace-csharp-compiler S3；C# 全程是种子（不删 C#）。

## 🔴 阻塞结论（2026-06-21）
S3 实现（`_buildStdlibCore` 改 z42c）+ `build stdlib` 验证成功，但 full GREEN gate（z42c-built stdlib）暴露 2 个 z42c parity gap，已 **revert** `_buildStdlibCore` 回 C#（保持 gate 绿）：
1. **BLID/.zsym sidecar**：z42c 不 emit BLID 段（deferred）→ C# `StdlibSidecarPairingTests`（断言每 stdlib zpkg 有 sidecar）失败。
2. **multicast aggregate 行为差异**：`multicast_func/predicate_aggregate` golden 在 C# in-process VM 抛未捕获 `Std.MulticastException`（z42vm subprocess 通过）→ z42c-built MulticastAction aggregate 路径需排查。

**收获（已落地，独立 commit）**：S3 dogfood 暴露并修复 z42c TSIG 可选参数 bug → 见 `fix-z42c-tsig-optional-params`（z42c）。

**前置（解阻塞后回来翻转 `_buildStdlibCore`）**：① z42c emit BLID（或放宽 sidecar 门）；② multicast aggregate 行为对齐。

## 进度概览
- [x] 0. 可行性验证（z42c M2 编 22 库 + 272/272 + TSIG bug 修复）
- [~] 1. `_buildStdlibCore` 改 z42c 接管（实现+build 验证 OK；**reverted**——阻塞）
- [ ] 2. full GREEN gate 全绿 —— 🔴 阻塞（BLID sidecar + multicast aggregate）
- [ ] 3. 文档同步 + 归档 —— 待解阻塞

## 1. _buildStdlibCore（scripts/xtask_stdlib.z42）
- [ ] 1.1 factor `_csharpBuildStdlibWorkspace`（现 step-1 C# build 抽出，作种子）
- [ ] 1.2 接线：C# 种子 stdlib → `_buildCompilerZ42` → `_buildRuntime`(ensure z42vm)
- [ ] 1.3 run-libs 组装（C#-种子 stdlib + z42c 7 包，copy；复用 `_copyAll`/`_resetDir`）
- [ ] 1.4 z42c 重编 stdlib（z42vm + driver.zpkg --mode interp -- build --workspace --release，cwd=src/libraries，Z42_LIBS=run-libs，M2 per-member 覆盖）
- [ ] 1.5 verify + flat view 不变（z42c-built）
- [ ] 1.6 更新文件头 cold-bootstrap 注释

## 2. 验证
- [ ] 2.1 `./xtask build stdlib` → z42c 接管，22 库产出 + flat view
- [ ] 2.2 `./xtask test stdlib`（rebuild 路径）→ 272/272（跑在 z42c-built libs）
- [ ] 2.3 `./xtask test`（full GREEN）→ 全绿（compiler+vm+cross-zpkg+stdlib+compiler-z42）
- [ ] 2.4 restore：确认 C# 仍可独立建 stdlib（铁律：种子未断）

## 3. 文档 + 归档
- [ ] 3.1 docs/design/compiler/self-hosting.md：生产 stdlib build 由 z42c 接管（S3）
- [ ] 3.2 replace-csharp-compiler/tasks.md：S3.1/S3.2 勾选 + 措辞（per-member drop-in）
- [ ] 3.3 归档 + 释放 toolchain 锁 + commit

## 备注
- bootstrap 序铁律：C# 全程种子；本 change 不删 C#（S5 才删，须先 S4 种子）。
- perf：z42c interp ~30s/build（dogfood 税）；jit 加速留 self-hosting.md Deferred + roadmap 索引。
