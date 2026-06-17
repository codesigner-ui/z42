# Spec: desktop-workload

## ADDED Requirements

### Requirement: desktop 是可安装 workload，apphost 经它交付

#### Scenario: install desktop workload（本地 --from）
- **WHEN** `z42 workload install desktop --from <z42-workload-<v>-desktop-<rid>> --version <v>`
- **THEN** tooling 拷到 `runtimes/<v>/workloads/desktop/`，含 `apphost`(`.exe`)；无 runtime pack（runtimes:[]）；
  打印 `✅ workload install desktop done`

#### Scenario: install desktop workload（联网，per-host manifest）
- **WHEN** manifest `workloads.desktop.hosts.<hostRid> = {archive, sha256}`，`z42 workload install desktop --version <v>`
- **THEN** 按 `_hostRid()` 取该 host 的 archive，下载+sha 校验+解压到 `workloads/desktop/`；不拉任何 runtime pack

#### Scenario: host 不支持
- **WHEN** manifest `workloads.desktop.hosts` 无当前 host RID
- **THEN** 报「desktop workload not available for this host (<rid>)」+ 非零退出，不下载

### Requirement: `publish desktop` 门控于 desktop workload

#### Scenario: 已装 → 正常产 apphost
- **WHEN** desktop workload 已装，`z42 publish desktop <app.toml>`
- **THEN** `Apphost.Produce` 从 `runtimes/<v>/workloads/desktop/apphost` 取 stub，patch zpkg 路径，产可执行
  apphost exe（macos 端 ad-hoc 重签名）；该 exe 跑起来正确加载并执行 app 的 zpkg

#### Scenario: 未装 → 门控报错
- **WHEN** desktop workload 未装，`z42 publish desktop <app.toml>`
- **THEN** 报 `desktop workload not installed; run: z42 workload install desktop` + 非零退出，不产任何 exe

## MODIFIED Requirements

### Requirement: apphost stub 来源 — baked → desktop workload

**Before:** apphost stub 随 SDK `bin/apphost` + launcher 目录分发；`Apphost._templatePath` 在 z42vm 旁 / launcher
目录就近找；`publish desktop` 无需 workload。

**After:** SDK 不带 apphost；stub 由 desktop workload 携带（per-host-RID）；`_templatePath` 查
`Z42_APPHOST_TEMPLATE`（override）→ `runtimes/<v>/workloads/desktop/apphost`；找不到即 publish 报门控错误。

### Requirement: CI 发 desktop workload 归档 + manifest workloads.desktop（per-host）

**Before:** 无 desktop workload 归档；release-index `workloads` 仅 ios/android/wasm。

**After:** 每桌面 RID 产 `z42-workload-<v>-desktop-<rid>.tar.gz`；release-index `workloads.desktop = { hosts: {
"<rid>": {archive, sha256} … }, runtimes: [] }`。

## Out of Scope
- `export desktop`（desktop 无 IDE 工程）；auto-pull；跨 RID publish；B1 命令发现。

## Pipeline Steps（toolchain only）
- [ ] xtask 产 desktop workload + SDK 去 apphost
- [ ] CI 归档 + per-host index
- [ ] launcher install desktop（per-host）+ publish 门控 + apphost `_templatePath`
- [ ] 本地 e2e（macos-arm64）：install → publish → 跑 apphost；未装报错
