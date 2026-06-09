# Tasks: simplify-apphost-direct-run

> 状态：🟢 已完成并归档（2026-06-10）| supersedes add-apphost Decision 5（裸 apphost 经 launcher.zpkg）

**变更说明（User 裁决）**：apphost 产出的 `./app` 改为**直接** `z42vm <app.zpkg> -- argv`（设 Z42_LIBS），**不再经 `launcher.zpkg`/muxer**。部署一个 app 只需 apphost exe + app.zpkg + 可解析运行时(z42vm+libs)，**不需要 launcher.zpkg**。单 VM 进程（去掉原先 launcher.zpkg 那一跳 + 双 VM）。与 .NET apphost 一致（published apphost 不走 dotnet muxer）。

**原因**：原设计(add-apphost D5)让 apphost 走 `z42vm launcher.zpkg -- app.zpkg`(复用 launcher 核心裸 apphost 形式)→ 需 launcher.zpkg 在场 + 双 VM。User 要求简化、去掉 launcher.zpkg 依赖。符合"z42 优先"：stub 只做"找 VM + 跑 app"(最小原生核),不实现 z42 逻辑,只是少做(不做版本 muxing/runtimeconfig)。

**代价(已知)**：apphost 不再读 `<app>.runtimeconfig.json`(版本 pin + configProperties)——那套逻辑在 launcher.zpkg,只有 `z42 run` 才生效。需要版本选择/GC 旋钮的 app 用 `z42 run`,或后续给 stub 加最小版本检查(Deferred)。`launcher.zpkg` 仍留 SDK 供 `z42` muxer(run/list/install/apphost build)。

**子系统**：`toolchain`(launcher crate；与 port-z42c-core 并行,User 授权,文件不重叠)。refactor/design-change 型。

- [x] 1.1 `src/toolchain/launcher/src/lib.rs`：加 `AppRuntime{vm,libs}` + `probe_app_runtime`(不需 launcher.zpkg) + `resolve_app_runtime[_in]` + `exec_app`(z42vm app.zpkg -- argv)；删 `resolve_apphost_runtime[_in]`(apphost 专用,已替)。`probe_runtime`/`exec_core`(trampoline)保留不变。
- [x] 1.2 `src/toolchain/launcher/src/apphost.rs`：run path 改 `resolve_app_runtime` + `exec_app`(直跑);更头注。
- [x] 1.3 单测：新增 `probe_app_runtime_needs_no_launcher_zpkg`(只 z42vm+libs,无 launcher.zpkg → probe_runtime None / probe_app_runtime Some);解析顺序测试改 resolve_app_runtime_in。crate 13/13 绿。
- [x] 1.4 e2e：build app → apphost build → 跑产出 exe(Z42_HOME 指向**无 launcher.zpkg** 的 runtime)→ `DIRECT_OK args=2` 退出码 2 ✅。
- [x] 1.5 `docs/design/runtime/launcher.md` apphost 段同步直跑模型 + runtimeconfig 代价。

## 备注
- patcher(`core/apphost.z42`)、打包(`xtask_package.z42` 铺 bin/apphost)、`install.sh`、dist smoke 均**不变**(smoke 仍跑产出 exe 断言 APPHOST_OK;produced exe 现直跑)。
- runtimeconfig 版本/配置 → 若需要,后续 `apphost-future-runtimeconfig`(stub 内最小 JSON 解析 / 版本检查)。
