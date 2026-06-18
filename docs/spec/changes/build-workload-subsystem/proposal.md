# build-workload-subsystem — proposal（程序级 charter）

> 状态：**DRAFT（立项）**（2026-06-17）。consolidate-platform-into-workload 的 **B 分支**：实现 workload 子系统，解锁 S2/S4/S5。
> **这是净新增子系统，非机械搬迁**——按 workflow 规范先行，各 phase 落地前需各自 proposal/spec + User 确认。本文件只做**程序级立项**（范围 + 阶段 + 开放问题），不含实现。

## Why

consolidate-platform-into-workload 的机械搬迁已完成（S0 设计 / S1 Tier2→host-api / S3' platforms→workload/platforms）。**注（2026-06-18 重组）**：后经独立 refactor 改为**平台优先** `workload/<plat>/{appbuilder,template,platform,tests}`（去 `platforms/` 中间层），host-api→`runtime/crates/z42-host`；结构以 `src/toolchain/workload/README.md` 为准，下文路径 `workload/platforms/*` 应按此读作 `workload/<plat>/platform/*`。但 host 的彻底解散与"全平台应用开发"目标，依赖一套**当前只有前瞻设计草案、尚未实施**的 workload 子系统：

- [launcher-command-dispatch.md](../../../design/toolchain/launcher-command-dispatch.md) — 命令发现机制（Core/SDK/Workload 三层 + 目录发现 + Std.Cli 树合并）⚠️ 未实施
- [runtime-workload-distribution.md](../../../design/toolchain/runtime-workload-distribution.md) — workload 安装 / manifest / 打包 ⚠️ workload 段未实施（runtime 拆包已落地）
- [platform-export-lifecycle.md](../../../design/toolchain/platform-export-lifecycle.md) — `z42 new/export/publish/test`、managed+eject ⚠️ 未实施

没有它：S2（apphost→desktop workload 的命令派发）/ S4（R1–R7 改 workload 驱动）/ S5（host/ 移除）无法干净落地——apphost 仍焊在 launcher、平台测试仍是手维护脚手架。

## What（实现已设计好的三份草案）

把上述三份"前瞻草案"实现为可用子系统。**设计基本就绪，B 是实现 + 细化 spec**，不是重新设计。

## 阶段（各自独立 change，逐个 spec-first；顺序含依赖）

| Phase | 内容 | 依赖 | 主要设计来源 |
|-------|------|------|------|
| **B1** | **命令发现机制**：launcher 扫命令目录 + 读 manifest → 把 SDK/workload 命令注册进 Std.Cli 树（与代码注册 core 同形）| — | launcher-command-dispatch.md |
| **B2** | **workload 包格式 + `z42 workload install/list/remove`**：manifest `workloads` 段实现、版本作用域 `runtimes/<ver>/workloads/<wl>/`、host 校验 | B1 | runtime-workload-distribution.md |
| **B3 (=S2)** | **desktop 平台 export（apphost-as-config）**：加 `[platform.desktop]` 段 + `z42 export/publish desktop` 产 apphost（对称 ios/android/wasm）；**取消 `z42 apphost` 命令**，apphost.z42 stub-patch 逻辑成为 desktop export 实现。**复用现有 `launcher_export*.z42` 框架，不需先做 B1** | —（独立于 B1）| platform-export-lifecycle.md `[platform.desktop]` |
| **B4 (=S4)** | **R1–R7 改 workload 驱动**：平台一致性测试 = workload 生成/驱动的工程（test 流程 ≡ 用户建 app）；删 `workload/<plat>/`（platform/Tests、tests）手维护脚手架 | B3 | platform-export-lifecycle.md §test 双面 |
| **B5** | **导出/发布生命周期**：`z42 new/platform add/export/publish/test`、managed+eject | B1–B4 | platform-export-lifecycle.md |
| **S5** | host/ 顶层移除 + 文档收口（B4 后 host/ 真空）| B4 | consolidate design.md |

> B1 是地基（命令发现），其余皆依赖它。B3=原 S2、B4=原 S4。

## 与已落地工作的衔接

- `add-export-command`（✅ 2026-06-14）已有 `z42 export ios/android/wasm` → `runtimes/<rid>/`；B5 的 export 需与之对齐/吸收（不重复造）。
- `split-release-runtime-package`（✅ 2026-06-14）已拆 `z42-runtime-*` 独立包 + manifest `runtimes` 段；B2 在其上加 `workloads` 段实现。

## Out of Scope

- packager（用户暂缓，后议）。
- 已搬迁内容（S1/S3' 已完成）。
- 重新设计分发/生命周期（三份草案为准，B 只实现 + 细化）。

## Open Questions — User 已裁决（2026-06-17）

- [x] **现在就实施**（Q1）。
- [x] **apphost = 项目配置，非命令**（Q2/Q3）：`[platform.desktop]` 声明桌面输出 → `z42 export/publish desktop` 产 apphost；取消 `z42 apphost` 命令。apphost.z42 stub-patch 逻辑成为 desktop export 实现。pre-1.0 直接切，无兼容路径。已落 launcher-command-dispatch.md / platform-export-lifecycle.md。
- [x] **workload 按需自动下载缺失 runtime**（Q4）：用到某平台、对应 runtime pack 未装 → manifest 驱动自动拉取 + sha 校验（对齐 dotnet）。已落 runtime-workload-distribution.md Decision 10。

## 实施切入点（重排：先做能复用现有框架的 B3）

`z42 export ios/android/wasm` 已存在（`launcher_export*.z42`，baked launcher core），由 `[platform.<plat>]` 驱动。**desktop export（apphost-as-config）是它的对称扩展，不依赖 B1 命令发现** → 作为 B 的**第一个实施 change**：

1. **B3-first `add-desktop-export`**（feat，先做）：`[platform.desktop]` schema + `launcher_export_desktop.z42`（复用 apphost.z42 stub-patch）+ 取消 `z42 apphost` 命令 + 测试 + 文档。解锁 S2 语义。
2. B1（命令发现）/B2（workload 打包 + 自动拉 runtime）/B4（测试改 workload 驱动）/B5（完整 export/publish 生命周期）随后各自 spec-first。

> B1 从"地基"降级为"后续重构"：现有 export/publish 是 baked launcher core，能跑；把它们改成目录发现是隔离优化，不阻塞 desktop-export。
