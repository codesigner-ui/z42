# Spec: platform-deploy-cli

## ADDED Requirements

### Requirement: 平台部署命令用 `--rid` 而非平台子命令

#### Scenario: publish 桌面 apphost
- **WHEN** `z42 publish <app.toml> --rid macos-arm64`（desktop workload 已装）
- **THEN** 用 `apphost-macos-arm64` stub 产 apphost exe；省略 `--rid` 默认 host RID

#### Scenario: publish 未实装平台
- **WHEN** `z42 publish <app.toml> --rid ios-arm64`
- **THEN** 报「publish for ios not yet implemented (B5)」非零退出

#### Scenario: export 平台工程
- **WHEN** `z42 export <app.toml> --rid ios-arm64`
- **THEN** 产 Xcode 工程（等价旧 `export ios`）；`--rid android-*`→gradle；`--rid browser-wasm`→HTML/JS；`--rid <desktop>`→「desktop 无 IDE 工程」

#### Scenario: run 部署形态 vs 字节码
- **WHEN** `z42 run <app.toml> --rid macos-arm64`
- **THEN** 产临时 apphost 并执行（部署预览）；而 `z42 run <app.zpkg>` 仍跑字节码（无 `--rid`）

### Requirement: workload 删除动词统一为 uninstall

#### Scenario: uninstall workload
- **WHEN** `z42 workload uninstall <wl> [--version <v>]`
- **THEN** 删除已装 workload（行为同旧 `remove`）；`z42 workload remove` 不再存在

### Requirement: desktop workload 单包 + 跨平台 apphost 输出

#### Scenario: 单 desktop workload 携全 RID stub
- **WHEN** 装 CI 产的 `z42-workload-<v>-desktop`（manifest `workloads.desktop={archive,sha256,host:["*"],runtimes:[]}`）
- **THEN** `workloads/desktop/` 含 `apphost-{macos-arm64,linux-x64,linux-arm64,windows-x64}`；任意 host 可装

#### Scenario: 跨平台输出 linux apphost
- **WHEN** 在 macos 上 `z42 publish <app.toml> --rid linux-x64`（全 stub desktop workload 已装）
- **THEN** 用 `apphost-linux-x64` stub 产 linux apphost（patch 跨平台可行；linux 无 codesign）

#### Scenario: 跨产 macos apphost 的限制
- **WHEN** 在非 macos 上 `publish --rid macos-arm64`
- **THEN** patch 成功但 ad-hoc codesign（macos-only）不可用 → 报清晰提示（macos apphost 须在 macos host 产）

#### Scenario: 目标 stub 缺失
- **WHEN** `publish --rid <rid>` 但 `workloads/desktop/apphost-<rid>` 不存在（如本地只 build 了 host RID）
- **THEN** 报「apphost for <rid> not in installed desktop workload」+ 提示装完整 workload

## MODIFIED Requirements

### Requirement: desktop workload schema — per-host → 单包

**Before:** `workloads.desktop = { hosts: { "<rid>": {archive,sha256} … }, runtimes: [] }`（add-desktop-workload，per-host）。
**After:** `workloads.desktop = { archive, sha256, host: ["*"], runtimes: [] }`（单 RID-agnostic 包，携全 RID `apphost-<rid>`）。

## Out of Scope
- ios/android/wasm publish 实装（B5）；跨产 macos apphost 的 codesign（Deferred）。

## Pipeline Steps（toolchain only）
- [ ] CLI router：publish/export leaf + `--rid`；run `--rid` 派发；workload uninstall
- [ ] launcher：category 派发 + `_desktopApphostStub(rid)` + 跨产
- [ ] xtask：desktop workload `apphost-<rid>`
- [ ] CI：合成单 desktop workload + 单 archive manifest
- [ ] 本地 e2e
