# Spec: workload-host-gate

## ADDED Requirements

### Requirement: 联网装 workload 前校验 host RID

#### Scenario: host 在允许列表 → 放行
- **WHEN** `release-index.json` 的 `workloads.<wl>.host` 含当前 `_hostRid()`（或为 `["*"]`），用户
  `z42 workload install <wl> --base-url … --version <v>`
- **THEN** 正常下载+校验+解压+铺设（行为同 B2-4）

#### Scenario: host 不在允许列表 → 拒绝，不下载
- **WHEN** `workloads.<wl>.host` 不含当前 host RID 且非 `["*"]`（如在 linux 上装 `host=["macos-arm64"]` 的 ios）
- **THEN** 报清晰错误（`workload '<wl>' not supported on this host (<hostRid>); requires one of: <list>`）并非零退出；
  **不发起归档下载**、不建任何 staging 目录

#### Scenario: host 字段缺失 → 放行（向后兼容）
- **WHEN** `workloads.<wl>` 无 `host` 字段（旧 manifest / 未声明）
- **THEN** 不 gate，正常安装（host 未声明 = 不限制）

#### Scenario: `"*"` 通配 → 任意 host 放行
- **WHEN** `host` = `["*"]`（如 wasm）
- **THEN** 任意 host RID 均放行

#### Scenario: 本地 `--from` 装不受 host gate 影响（回归）
- **WHEN** 用户 `z42 workload install <wl> --from <dir> …`
- **THEN** 不读 manifest host、不 gate（本地显式安装），行为同 B2

## MODIFIED Requirements

### Requirement: CI release-index.json 的 workloads 段带 host

**Before:** `workloads.<wl> = { archive, sha256, runtimes }`（B2-4，无 host）。
**After:** `workloads.<wl> = { archive, sha256, host, runtimes }`，host = 允许的 host RID 列表或 `["*"]`：
ios=`["macos-arm64"]`、android=全桌面 RID、wasm=`["*"]`。

## Pipeline Steps（toolchain only）

- [ ] CI: workloads 段加 host
- [ ] launcher: `_fetchWorkloadEntry` 携 host + `_hostAllowed` gate
- [ ] 本地 mock e2e（放行 / 拒绝 / 通配 / 缺失四态）
