# Design: add-desktop-workload

## Architecture

```
build/CI                              install                          publish desktop
────────                              ───────                          ───────────────
xtask package --rid <desktop-rid>     z42 workload install desktop     z42 publish desktop app.toml
 → z42-sdk-<v>-<rid>/   (no apphost)    read workloads.desktop.hosts     check workloads/desktop/ exists?
 → z42-runtime-<v>-<rid>/               .<hostRid> = {archive,sha256}      ├─ no → error: run `z42 workload install desktop`
 → z42-workload-<v>-desktop-<rid>/      download+verify+extract →         └─ yes → Apphost.Produce
     apphost(.exe) + manifest           runtimes/<ver>/workloads/desktop/    stub = workloads/desktop/apphost
                                        (no runtime loop — runtimes:[])      patch zpkg path → write exe → codesign
```

apphost stub 是 **per-host-RID 原生二进制** → desktop workload **per-host**（≠ ios/android 的 RID-agnostic tooling）。
desktop workload **无 target runtime pack**（复用 `z42 install <ver>` 的 host runtime；Decision 9 `runtimes:[]`）。

## Decisions

### D1：manifest `workloads.desktop` 用 per-host schema
**问题：** ios/android/wasm 的 `workloads.<wl>.archive` 是单归档 + `runtimes:[rid…]`。desktop 的 tooling 本身
per-host-RID（携带 per-RID apphost stub），没有 target runtime。
**决定：** `workloads.desktop = { "hosts": { "<rid>": { "archive", "sha256" } … }, "runtimes": [] }`（取代单 `archive`）。
launcher 按 `_hostRid()` 取 `hosts.<rid>`。`host` 允许列表 = `hosts` 的 keys。
**理由：** 语义最准——desktop 是「每 host 一份 apphost 工具」，不是「一份 tooling + 多 target runtime」。复用
`runtimes` 列表硬塞 host-RID 会污染「target runtime pack」语义（Decision 9 明确 desktop `runtimes:[]`）。

### D2：apphost stub 在 workload 内布局 + `_templatePath` 查找序
**问题：** stub 现由 `Apphost._templatePath` 在 `Z42_APPHOST_TEMPLATE` / z42vm 旁 / launcher 目录找。
**决定：** desktop workload 包内 stub 放根：`workloads/desktop/apphost`（`apphost.exe` on win）。`_templatePath` 新查找序：
`Z42_APPHOST_TEMPLATE`（override，测试用）→ **`runtimes/<resolved-ver>/workloads/desktop/apphost`** → （去掉 baked
z42vm 旁 / launcher 目录路径）。找不到 → 返回 ""，`Apphost.Produce` 报「desktop workload 未装」。
**理由：** 单一权威来源（workload），消除 baked 后门。保留 env override 供本地/CI 测试不依赖安装。

### D3：`publish desktop` 门控 = 报错提示（不 auto-pull）
**问题：** 未装 desktop workload 时怎么办。
**决定：** `_cmdPublishDesktop` 调 `Apphost.Produce` 前（或 Produce 内 stub="" 时）检查 → 未装则
`ConsoleError`「desktop workload not installed; run: z42 workload install desktop」+ 非零退出。**不** auto-pull（留
Deferred）。**理由：** 显式、可预期；auto-pull 涉及联网下载副作用，单独决策更稳（对齐当前 ios/android 不 auto-pull）。

### D4：SDK 不再带 apphost；desktop workload 携带
**问题：** SDK `bin/apphost` 现存在。
**决定：** `_packageDesktop` 不再拷 apphost 进 SDK `bin/`；NEW `_buildDesktopWorkload` 把 cargo build 出的 apphost
放进 `z42-workload-<v>-desktop-<rid>/`。apphost 二进制照常 cargo build，只是落点改为 workload 包。
**理由：** 完全门控；SDK 仍含 z42c+z42vm（build/run 所需），publish 能力下沉到 workload。

### D5：验证在 macos-arm64 全本地可跑
apphost 是 host-native → 本机 macos-arm64 可端到端：产 desktop workload → install（`--from` + 联网 mock）→
`publish desktop` 门控 → 产 apphost exe → **跑 exe 验证它加载并执行 zpkg**。未装路径验报错。

## Implementation Notes

- **xtask `_buildDesktopWorkload(root, rid, version, profile)`**：`pkgDir = z42-workload-<v>-desktop-<rid>`（内部 dir，
  无 `-release` 后缀，避开 SDK 的 `z42-*-<rid>-release` glob）；拷 `artifacts/build/runtime/<cargoTarget>/<profile>/apphost`(`.exe`)
  → `pkgDir/apphost`；写 manifest.toml（kind=workload-tooling，rid，host=[rid]，runtimes=[]，apphost="apphost"）。`_buildPackageCore`
  desktop 分支追加调用。
- **CI**：`z42-workload-<v>-desktop-<rid>/` → 归档 `z42-workload-<v>-desktop-<rid>.tar.gz`（每桌面 RID）；release-index
  `workloads.desktop.hosts.<rid>={archive,sha256}`。
- **launcher install desktop**：`_workloadInstallNetwork` 对 wl=="desktop" 读 per-host entry（新 helper
  `_fetchDesktopHostEntry(baseUrl, hostRid)` 或扩 `_fetchWorkloadEntry` 返回 hosts map）→ 下载 tooling → 解压到
  `workloads/desktop/`；**跳过 runtime loop**（runtimes:[]）；无平台铺设（apphost stub 即成品，无需 bed）。本地 `--from`
  路径同理（拷 dir）。
- **apphost.z42**：`_templatePath` 改查找序（D2）。需能解析当前 runtime `<ver>`（复用 launcher 的版本解析；publish
  在项目上下文，用 default/pin ver）。
- **gating**：stub="" → `_cmdPublishDesktop` 报错退出。

## Testing Strategy

- **本地 e2e（macos-arm64，权威）**：① `xtask package release --rid macos-arm64` 产 SDK（无 apphost）+ runtime +
  desktop workload。② `z42 workload install desktop --from <tooling>`（本地）→ `workloads/desktop/apphost` 就位。
  ③ `z42 publish desktop examples/.../app.toml` → 产 apphost exe → **跑 exe → 输出正确**。④ 未装时 publish → 报
  「workload install desktop」错误。⑤ 联网 mock：`workload install desktop --base-url <mock>`（per-host manifest）。
- CI correct-by-construction（jq per-host schema 干跑 + archive 名 sim）。

## Pipeline Steps（toolchain only）

- [ ] xtask：SDK 去 apphost + `_buildDesktopWorkload`
- [ ] CI：desktop workload 归档 + per-host index
- [ ] launcher：install desktop 分支 + per-host manifest 解析
- [ ] launcher：publish desktop 门控 + apphost `_templatePath`
- [ ] 本地 e2e（macos-arm64）
- [ ] docs 同步
