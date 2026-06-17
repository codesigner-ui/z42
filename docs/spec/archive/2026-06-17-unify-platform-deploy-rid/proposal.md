# Proposal: unify-platform-deploy-rid

> 状态：DRAFT（2026-06-17）。统一平台部署命令面（publish/export/run → `--rid`）+ workload remove→uninstall +
> desktop workload 合一（跨平台 apphost 输出）。子系统锁：`toolchain`。

## Why

三处命令面/结构不一致，趁 desktop workload 刚落地一并理顺：

1. **平台部署命令用子命令而非参数**：`publish desktop` / `export ios|android|wasm` / `run desktop` 每平台一个
   子命令。改为 `publish/export/run <toml> --rid <rid>`（rid 的 category 决定平台），全平台统一、加平台不增命令。
2. **删除动词不一致**：runtime 用 `uninstall`，workload 用 `remove`。统一 workload → `uninstall`。
3. **desktop workload per-host 过度拆分**：add-desktop-workload 把 desktop workload 按 host-RID 拆 4 个
   （`hosts.<rid>` schema）。但 apphost stub 小（~350KB），**合成一个 RID-agnostic 包携全 RID `apphost-<rid>`**
   即可——更简单（manifest 回到单 `archive`），且解锁**跨平台输出**：任意 host 上 `publish --rid linux-x64`
   产 linux apphost。

## What Changes

1. **CLI（launcher_cli.z42）**：
   - `publish <toml> --rid <rid>`（删 `_publishRouter`/desktop 子命令）；category 派发：desktop→apphost（实装），
     ios/android/wasm→「B5 未实装」提示。
   - `export <toml> --rid <rid>`（删 `_exportRouter`）；category 派发：ios→Xcode、android→gradle、wasm→HTML/JS；desktop→「无 IDE 工程」。
   - `run <toml> --rid <rid>` = 部署形态（替 `run desktop`）；`run <zpkg>` = 跑字节码（不变）。检测 `--rid` 走部署。
   - `workload uninstall`（替 `remove`）。
2. **desktop workload 合一**：
   - xtask `_buildDesktopWorkload(rid)` → stub 命名 `apphost-<rid>`（per-rid 包供 CI 收集）。
   - CI 把 4 RID 的 `apphost-<rid>` 收进**单包** `z42-workload-<v>-desktop`（manifest `archive` 单值 + `host:["*"]` + `runtimes:[]`）。
   - `_desktopApphostStub(rid)` → `workloads/desktop/apphost-<rid>`；publish/run `--rid` 取目标 RID 的 stub。
   - 删 launcher 的 per-host（`hosts.<rid>`）install 分支——单 `archive` 走既有通用路径。
3. **跨平台输出**：`publish --rid <target>` 用目标 RID 的 stub 产对应平台 apphost。**macos 目标 apphost 需
   macos 上 ad-hoc codesign**（codesign 仅 macos）→ 跨产 macos apphost 仍需 macos host；linux/windows 任意 host 可产。

## Scope（允许改动的文件）

| 文件 | 变更 | 说明 |
|------|------|------|
| `src/toolchain/launcher/core/launcher_cli.z42` | MODIFY | publish/export 改 leaf+`--rid`；run `--rid` 部署派发；workload `uninstall` |
| `src/toolchain/launcher/core/launcher_export.z42` | MODIFY | `_cmdPublish`/`_cmdExport` 按 category 派发；`_desktopApphostStub(rid)` 取 `apphost-<rid>`；publish/run 接 `--rid`；跨产 |
| `src/toolchain/launcher/core/launcher_workload.z42` | MODIFY | `_cmdWorkloadRemove`→`_cmdWorkloadUninstall`；删 per-host desktop install 分支 |
| `scripts/xtask_package_desktop.z42` | MODIFY | `_buildDesktopWorkload` stub→`apphost-<rid>`；manifest host=["*"]、runtimes=[] |
| `.github/workflows/release.yml` | MODIFY | 收 4 RID apphost-<rid> 合成单 `z42-workload-<v>-desktop`；index `workloads.desktop={archive,sha256,host:["*"],runtimes:[]}` |
| `.github/workflows/ci.yml` | MODIFY | publish-nightly 同上；host Verify 调整 |
| `docs/design/toolchain/runtime-workload-distribution.md` | MODIFY | desktop workload 单包 schema + 跨产说明 |
| `docs/design/toolchain/launcher-command-dispatch.md` + `platform-export-lifecycle.md` | MODIFY | 命令面 `--rid` 模型 |
| `docs/spec/changes/unify-platform-deploy-rid/*` | NEW | 本规范 |

## Out of Scope

- ios/android/wasm 的 publish 实装（B5）；它们的 export 实装细节不变（只改命令外壳）。
- 跨平台 publish 的 codesign（macos 目标在非 macos 上）——记 Deferred。
- B1 命令发现。

## 验证

macos-arm64 本地：`publish <toml> --rid macos-arm64` → apphost 跑出 hello,world；`workload uninstall desktop`；
`run <toml> --rid macos-arm64` 部署预览；`export <toml> --rid ios-arm64` 出 Xcode 工程；未装 desktop workload →
publish 门控报错。跨产（`--rid linux-x64`）的 stub 选择逻辑本地验（linux apphost 字节产出；运行需 linux）。
CI correct-by-construction + sim。

## Open Questions（design 定）
- 单 desktop workload 内 stub 布局：`apphost-<rid>`（平铺）vs `<rid>/apphost`？（倾向平铺，简单）
