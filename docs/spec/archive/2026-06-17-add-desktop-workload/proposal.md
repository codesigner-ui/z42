# Proposal: add-desktop-workload（Decision 9 完全门控）

> 状态：DRAFT（2026-06-17）。落地 Decision 9 / apphost-as-config：desktop 成为可安装 workload，apphost
> 经 desktop workload 交付，`publish desktop` 门控于 `workload install desktop`。子系统锁：`toolchain`。

## Why

Decision 9 早已定：「desktop 亦 workload（publish/export 维度）；apphost = desktop workload 的 publish 产物」。
但当前 apphost stub 仍 **baked**——随 SDK `bin/apphost` + launcher 目录分发，`Apphost.Produce` 在 z42vm 旁/launcher
目录就近找它，`publish desktop` 无需任何 workload。这与「默认 build/run 零 workload，publish/export 才下载平台
workload」的命令模型不一致：ios/android/wasm 已门控（要 publish/export 先装 workload），唯独 desktop 走后门。

不做：desktop 与其它平台不对称；`z42 workload install desktop` 报「not found」（B2-4 已证）；apphost 交付与
版本管理绑死在 launcher，无法独立版本化。

## What Changes

1. **apphost stub 移出 baked**：不再随 SDK `bin/` 或 launcher 目录分发；改由 **desktop workload 携带**
   （per-host-RID，因 apphost 是 per-RID 原生二进制）。
2. **产 desktop workload 包**：`z42-workload-<v>-desktop-<rid>`（每桌面 RID 一个）= apphost stub（`apphost`/`apphost.exe`）
   + manifest.toml（kind=workload-tooling，host=[本 rid]，runtimes=[]，无 target runtime pack——复用 host runtime）。
3. **manifest `workloads.desktop`（per-host schema）**：desktop 的 tooling 是 per-host-RID（≠ ios/android 的
   RID-agnostic tooling）。schema 扩 `workloads.desktop.hosts.<rid> = { archive, sha256 }`（取代单 `archive`）；
   `runtimes: []`。
4. **launcher install desktop 分支**：`workload install desktop` 解析 `workloads.desktop.hosts.<hostRid>` → 下载+校验+
   解压到 `runtimes/<ver>/workloads/desktop/`；无 runtime loop。
5. **`publish desktop` 门控**：`_cmdPublishDesktop` 先检查 desktop workload 已装；未装 → 清晰报错（提示
   `z42 workload install desktop`）。`Apphost._templatePath` 增 `runtimes/<ver>/workloads/desktop/` 为 stub 位置，
   移除 baked launcher-dir 路径。

## Scope（允许改动的文件）

| 文件 | 变更 | 说明 |
|------|------|------|
| `scripts/xtask_package_desktop.z42` | MODIFY | SDK `bin/` 不再放 apphost；NEW `_buildDesktopWorkload`（产 `z42-workload-<v>-desktop-<rid>/` = apphost stub + manifest） |
| `.github/workflows/release.yml` | MODIFY | 桌面 RID 产 desktop workload 归档；release-index `workloads.desktop.hosts.<rid>` |
| `.github/workflows/ci.yml` | MODIFY | publish-nightly 同上（nightly 名） |
| `src/toolchain/launcher/core/launcher_workload.z42` | MODIFY | install desktop 分支（per-host tooling，无 runtime loop） |
| `src/toolchain/launcher/core/launcher_network.z42` | MODIFY | `_fetchWorkloadEntry` 支持 per-host `hosts.<rid>` 解析（或新 helper） |
| `src/toolchain/launcher/core/launcher_export.z42` | MODIFY | `_cmdPublishDesktop` 门控 desktop workload 已装 |
| `src/toolchain/launcher/core/apphost.z42` | MODIFY | `_templatePath` 加 workload-dir stub 位置；去 baked launcher-dir |
| `docs/design/toolchain/runtime-workload-distribution.md` | MODIFY | workloads.desktop per-host schema + 门控流程定稿 |
| `docs/spec/changes/add-desktop-workload/*` | NEW | 本规范 |

**只读引用**：`_buildRuntimePackage`（desktop runtime pack 不变）、ios/android workload 产包模式。

## Out of Scope

- **`export desktop`**：desktop 无 IDE 工程可导出（命令模型：export 仅 ios/android）→ desktop workload 只服务 publish。
- **B1 命令发现**（desktop workload 的命令仍 baked dispatch）。
- **跨 RID publish**（在 macos 上 publish linux apphost）——apphost 当前用 host stub；跨编留后续。
- **自动拉取**（未装时自动 `workload install`）——本次只报错提示；auto-pull 留后续（design Deferred）。

## 验证

本机 macos-arm64 全程可验：xtask 产 `z42-workload-<v>-desktop-macos-arm64` → `workload install desktop`（本地
`--from` + 联网 mock）→ `publish desktop` 门控放行 → 产 apphost exe → **跑 apphost 验证加载 zpkg**；未装时
`publish desktop` 报清晰错误。CI 改动 correct-by-construction（同既往）。

## Open Questions（design.md 定）

- per-host schema：`workloads.desktop.hosts.<rid>.{archive,sha256}` vs 复用 `runtimes` 列表？（倾向前者，语义更准）
- apphost stub 在 workload 内的布局（`bin/apphost`？workload 根？）+ `_templatePath` 查找序。
