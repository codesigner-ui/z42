# build-workload-subsystem — proposal（程序级 charter）

> 状态：**DRAFT（立项）**（2026-06-17）。consolidate-platform-into-workload 的 **B 分支**：实现 workload 子系统，解锁 S2/S4/S5。
> **这是净新增子系统，非机械搬迁**——按 workflow 规范先行，各 phase 落地前需各自 proposal/spec + User 确认。本文件只做**程序级立项**（范围 + 阶段 + 开放问题），不含实现。

## Why

consolidate-platform-into-workload 的机械搬迁已完成（S0 设计 / S1 Tier2→host-api / S3' platforms→workload/platforms）。但 host 的彻底解散与"全平台应用开发"目标，依赖一套**当前只有前瞻设计草案、尚未实施**的 workload 子系统：

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
| **B3 (=S2)** | **apphost → desktop workload**：`apphost.z42` 迁出 launcher core，`z42 publish`（桌面）经 desktop workload 派发；建 workload 脚手架骨架 | B1,B2 | launcher-command-dispatch.md §三层 |
| **B4 (=S4)** | **R1–R7 改 workload 驱动**：平台一致性测试 = workload 生成/驱动的工程（test 流程 ≡ 用户建 app）；删 `workload/platforms/*/tests` 手维护脚手架 | B3 | platform-export-lifecycle.md §test 双面 |
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

## Open Questions（需 User 定，再开 B1 spec）

- [ ] B 是否现在就排期实施，还是仅立项、待其他优先级（如 0.3.x 自举线）让路？
- [ ] 命令发现的 MVP 边界：先只支撑 apphost（B3）所需最小集，还是一次做全（B5 export/publish）？
- [ ] apphost 迁移期间 `z42 apphost build` 的过渡兼容（pre-1.0 可直接切，无兼容路径——待确认）。
- [ ] workload 包与 `add-export-command` 的 `runtimes/<rid>/` 产物如何统一。

## 当前不实施

本 change 仅立项。B1 实施前需：本 proposal User 确认 → B1 自己的 proposal/spec/design → 阶段 6.5 gate。
