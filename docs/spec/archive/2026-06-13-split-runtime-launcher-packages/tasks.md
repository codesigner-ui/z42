# Tasks: split-runtime-launcher-packages

> 状态：🟢 已完成 | 创建：2026-06-13 | 子系统锁：toolchain（已释放）
> **变更说明：** 把 desktop 发布包拆成三个独立 artifact：SDK（bootstrap/portable）、runtime（`z42 install`）、launcher（`z42 self-update`）。
> **原因：** `z42 install <ver>` / `z42 self-update` 不应下载含 z42c 的全量 SDK；launcher/runtime 各需专属小包。
> **类型：** feature（toolchain）。

## 实施任务

- [x] 1. 拆 `xtask_package_desktop.z42`：从 `xtask_package.z42` 提取 desktop 专用函数（+ 新增两个 builder）
- [x] 2. 新增 `_buildLauncherPackage`：layout = `{z42vm, launcher.zpkg, libs/, apphost}`（直接映射 `launcher/`）
- [x] 3. 新增 `_buildRuntimePackage`：layout = `{z42vm, libs/}`（映射 `runtimes/<ver>/`）
- [x] 4. `_buildPackageCore`：desktop 分支追加调用两个新 builder（含 inline `if (c != 0)` 检查）
- [x] 5. `launcher_network.z42`：`_fetchManifest` 加 `packageType` 参数，读 `runtimes.<rid>.<type>.{archive,sha256}`，回退旧格式 `runtimes.<rid>.{archive,sha256}`
- [x] 6. `_cmdInstall`：改用 `_fetchManifest(baseUrl, rid, "runtime")`
- [x] 7. `_cmdSelfUpdate`：改用 `_fetchManifest(baseUrl, rid, "launcher")`（无需 layout 探测：`probe_runtime` 已支持 z42vm 在根或 bin/ 两种 layout）
- [x] 8. `docs/design/runtime/launcher.md`：新增「三包发布结构」section，更新 P2 命令表描述
- [x] 9. 编译验证（launcher 4/4 files GREEN；xtask 23/23 files GREEN）

## 打包结构

```
release tag v<ver>
  z42-<ver>-<rid>.tar.gz          # SDK（含 z42c，bootstrap/portable 专用）
  z42-launcher-<ver>-<rid>.tar.gz # launcher/：z42vm + launcher.zpkg + libs/ + apphost
  z42-runtime-<ver>-<rid>.tar.gz  # runtimes/<ver>/：z42vm + libs/
```

`release-index.json` 新格式（CI 待更新）：
```json
{
  "runtimes": {
    "macos-arm64": {
      "launcher": { "archive": "z42-launcher-<ver>-macos-arm64.tar.gz", "sha256": "..." },
      "runtime":  { "archive": "z42-runtime-<ver>-macos-arm64.tar.gz",  "sha256": "..." }
    }
  }
}
```
旧格式（顶层 `archive` key）作为回退兼容。

## 实施备注

- `probe_runtime(dir)` 已同时支持 `<dir>/z42vm`（installed）和 `<dir>/bin/z42vm`（portable SDK）两种 layout，故 `_cmdSelfUpdate` 无需 layout 探测/重组：新 launcher 包（z42vm 在根）→ installed layout；旧 SDK 包（z42vm 在 bin/）→ portable layout，trampoline 均能正确找到。
- `_pkgEmitHelloC`, `_pkgEmitHelloRust`, `_utcNow`, `_quotedDirList`, `_quotedSubdirs`, `_quotedExisting` 移入 `xtask_package_desktop.z42` 后，android/ios/wasm/bench 文件无需修改——Z42Xtask 命名空间跨文件共享，加入 toml includes 即可。

## 延后

- CI `release.yml`：上传 launcher + runtime 包、生成新格式 `release-index.json`（CI 文件未在此 change scope 内）
- `z42 uninstall` 检查是否是 launcher 内置 runtime（`launcher/` 专管，不走 `runtimes/`，实际已隔离，无需额外保护）
