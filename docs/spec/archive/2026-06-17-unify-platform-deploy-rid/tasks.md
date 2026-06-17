# Tasks: unify-platform-deploy-rid

> 状态：🟢 已完成 | 创建：2026-06-17 | 锁：`toolchain`
> proposal/spec 见同目录。scope 经用户 AskUserQuestion 确认（全统一 publish+export+run + workload uninstall + desktop 单包）。

## 阶段 1：desktop workload 合一（xtask + launcher 解析）
- [x] 1.1 xtask_package_desktop.z42：`_buildDesktopWorkload` stub→`apphost-<rid>`；manifest host=["*"]、runtimes=[]、kind=workload-tooling
- [x] 1.2 launcher_workload.z42：删 per-host（`hosts.<rid>`）install 分支（desktop 现为单 archive，走通用路径）
- [x] 1.3 launcher_network.z42：`_fetchWorkloadEntry` 无需改（desktop 现是 archive+host）；确认通用解析覆盖

## 阶段 2：CLI 统一为 --rid
- [x] 2.1 launcher_cli.z42：`publish` leaf（`<toml>` + `--rid` + `--output`，删 `_publishRouter`）
- [x] 2.2 launcher_cli.z42：`export` leaf（`<toml>` + `--rid` + `--output/--sdk-ver/--bundle-id/--app-id/--entry`，删 `_exportRouter`）
- [x] 2.3 launcher_cli.z42：`run` 派发——检测 `--rid` 走部署形态（`_runLauncher` run 分支）
- [x] 2.4 launcher_cli.z42：workload router `remove`→`uninstall`；dispatch 改名
- [x] 2.5 launcher_export.z42：`_cmdPublish(r)` 按 `_ridCategory(--rid|host)` 派发（desktop→apphost；其它→B5 提示）
- [x] 2.6 launcher_export.z42：`_cmdExport(r)` 按 category 派发（ios/android/wasm；desktop→无 IDE 工程）
- [x] 2.7 launcher_export.z42：`_cmdRunDeploy`（`--rid` 部署形态）；`_desktopApphostStub(rid)`→`apphost-<rid>` + 跨产（macos 目标非 macos host → codesign 提示）
- [x] 2.8 launcher_workload.z42：`_cmdWorkloadRemove`→`_cmdWorkloadUninstall`

## 阶段 3：CI 合成单 desktop workload
- [x] 3.1 release.yml：收 4 RID 的 `apphost-<rid>`（从各 RID 包）合成单 `z42-workload-<v>-desktop` + 单 archive；index `workloads.desktop={archive,sha256,host:["*"],runtimes:[]}`
- [x] 3.2 ci.yml publish-nightly：同上（nightly）；host Verify 调整（apphost-<rid> 命名）
- [x] 3.3 jq + YAML 干跑

## 阶段 4：验证 + docs + 归档
- [x] 4.1 编译 launcher + xtask 清编
- [x] 4.2 本地 e2e（macos-arm64）：`publish <toml> --rid macos-arm64` 产 apphost 跑出 hello,world；未装门控报错；`workload uninstall desktop`；`run <toml> --rid macos-arm64`；`export <toml> --rid ios-arm64`（若 ios 工具链）；跨产 `--rid linux-x64` stub 选择验（产 linux apphost 字节）
- [x] 4.3 CI sim：release.yml 合成单 desktop workload + gate
- [x] 4.4 docs 同步（schema 单包 + 命令面 --rid + 跨产 + codesign 限制入 Deferred）
- [x] 4.5 GREEN + 归档 + 释放锁 + commit

## 备注
- 跨产 macos apphost 在非 macos host 的 codesign → Deferred（design + roadmap）。
- pre-1.0 无兼容：`publish desktop`/`export ios`/`run desktop`/`workload remove` 直接删，不留 alias。

## 验证结果（macos-arm64 本地全程 + CI-sim）
- CLI：`publish <toml> --rid macos-arm64`（无子命令）→ apphost 跑出 hello,world ✓；`run <toml> --rid macos-arm64` 部署形态 → hello,world ✓；`workload uninstall desktop` ✓
- 门控：未装 desktop workload → publish 报「run z42 workload install desktop」✓
- 跨产：`publish --rid linux-x64`（本地只 build macos）→ 清晰「apphost for 'linux-x64' not in installed desktop workload」✓；codesign 按 target rid 判（macos 目标非 macos host 报错）
- desktop workload：单包携 apphost-<rid>；xtask 产 apphost-macos-arm64 ✓
- CI-sim：release.yml 4 per-RID 片合并为单 z42-workload-<v>-desktop.tar.gz（含全 apphost-<rid> + windows.exe）、per-rid 中间件清除、index gate exit 0、workloads.desktop 单 archive+host:["*"]+runtimes:[] ✓；YAML 合法
