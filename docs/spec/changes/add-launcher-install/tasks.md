# Tasks: add-launcher-install — launcher P2：install / uninstall / self-update

> 状态：🟡 进行中 | 创建：2026-06-13 | 子系统锁：toolchain（持有中）
> **变更说明：** `z42 install`/`self-update` 实现 GitHub Releases 下载安装流程（launcher P2）。
> **原因：** P1（local link）已归档；prerequisites 就绪：release-index.json manifest、per-platform 包、SHA256 验证。
> **类型：** feature（toolchain）。

## 前置条件（已满足）

- [x] `release-index.json` 随每次发布 publish（`ci.yml` / `release.yml`）
- [x] 所有平台包无 top-level 子目录（`fix(toolchain): remove top-level subdir from release archives`，commit 10c90837）
- [x] `Std.Archive` 提供 `Zip.ExtractAllTo` + streaming `Tar.ExtractStream`/`Gzip.WrapRead`
- [x] `Std.Crypto` 提供 `Sha256.HashHex`
- [x] toolchain 锁由 port-z42c-core 归档释放后，本 change 登记持有

## 实施任务

- [x] 1. 新建 `launcher_network.z42`（从 `launcher.z42` 拆出网络命令）
  - `_cmdInstall` 重写：manifest-first + 流式解压（tgz/zip）+ 无子目录 + Windows zip 支持
  - `_cmdSelfUpdate` 新增：下载全量包替换 `$Z42_HOME/launcher/`，portable 模式拒绝
  - 共享辅助：`_fetchManifest`（release-index.json → [archive, sha256]）、`_extractArchive`（tgz/zip 分支）、`_hostRid`、`_findSum`（从 launcher.z42 移入）
- [x] 2. `launcher.z42`：移除 `_cmdInstall`/`_hostRid`/`_findSum` + 删除无用 using（Net.Http / Compression / Archive / Crypto）
- [x] 3. `launcher_cli.z42`：注册 `self-update` 命令（`--channel` 选项）+ dispatch

## 待验证

- [x] 4. `dotnet run ... build z42.launcher.z42.toml` 编译通过（无 undefined / type error；4/4 files compiled）
- [x] 5. GoldenTests 1561/1561 GREEN（launcher 变更不影响编译器 golden）
- [ ] 6. e2e 验证：`z42 install --help` / `z42 self-update --help`（下次 bootstrap 更新后）

## 行为规格

### `z42 install <version|nightly>`

1. `_hostRid()` 确定 RID（unsupported → exit 2）
2. 尝试 `release-index.json` → 取 `runtimes.<rid>.archive` + `sha256`
3. 若 manifest 失败 → 回退 SHA256SUMS + 命名约定（`z42-<ver>-<rid>.tar.gz` / `.zip`）
4. GET archive → `resp.IsSuccess()` 失败 exit 1
5. 若得到 sha256 → `Sha256.HashHex(body)` 对比 → 失败 exit 1
6. 解压到 staging（`.staging/<ver>.stage`）
   - Windows（`.zip`）：`Zip.ExtractAllTo(body, staging)`
   - 其他（`.tar.gz`）：`Gzip.WrapRead(ms)` → `Tar.ExtractStream(gz, staging)`
7. `File.Move(staging, runtimes/<ver>)`（原子替换）
8. 输出 `installed <ver> → <path>`

### `z42 self-update [--channel <ver>]`

- Portable 模式（`Z42_PORTABLE_VM` 非空）→ 拒绝 exit 2
- `$Z42_HOME/launcher/` 不存在 → 提示 bootstrap exit 2
- 其他同 install，目标为 `$Z42_HOME/launcher/`
- 提示 `restart z42 to use the new version.`

## 延后（Deferred）

- Windows `self-update`：`z42.exe` 仍在运行（父进程 wait z42vm），`Directory.Delete` 会失败。当前行为：抛出 IO 异常，用户需重新运行。后续可用 "rename + copy" 策略或 PowerShell 延迟替换。记入 `docs/design/toolchain/launcher-command-dispatch.md` Deferred 段（`launcher-future-self-update-windows`）。
