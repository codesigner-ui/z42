# Proposal: add-workload-manifest-install（B2-4）

> 状态：**DRAFT**（2026-06-17）。impl-workload-install（B2）的后续：把 workload 从「本地 `--from` 装」
> 升级为「manifest 联网装」，并修好 CI release 因 B2-3 runtime/workload 分包而留下的产物错配。
> 子系统锁：`toolchain`。

## Why

B2 落地了三平台 LOCAL `z42 workload install <wl> --from <tooling> --runtime <pack>`，但：

1. **CI release.yml 与 B2-3 分包脱节**：平台 RID 的 Archive 步把 `z42-<ver>-<rid>-release/`（B2-3 后已是
   **workload tooling**）打成 `z42-runtime-<ver>-<rid>.tar.gz`，**从不上传真正的 runtime pack**
   `z42-runtime-<ver>-<rid>/`。于是 release-index.json 的 `runtimes.<rid>.runtime` 指向的字节是 tooling，
   不是 runtime pack——联网装会装错东西。**这是 B2-3 引入的 regression，必须修。**
2. **release-index.json 无 `workloads` 段**：launcher 无从得知某 workload 的 tooling 归档名 + 它依赖哪些
   runtime RID（android 双 ABI / ios 真机+模拟器）。
3. **launcher 联网装未接线**：`workload install <wl>`（无 `--from`）直接报错；`_fetchManifest` /
   `_extractArchive` / 下载-校验-原子换的机制已就绪（`z42 install` 在用），只差为 workload 复用。

不做的后果：`z42 workload install ios`（用户实际会跑的无 `--from` 形式）不可用；CI 一旦打 tag 发版，
平台 runtime 归档内容是错的。

## What Changes

1. **CI Archive 步**（release.yml）：平台 RID 产 **两个**归档——workload tooling `z42-<ver>-<wl>.tar.gz`
   （wl∈{ios,android,wasm}，由 rid 归类）+ runtime pack `z42-runtime-<ver>-<rid>.tar.gz`（来自真正的
   `z42-runtime-<ver>-<rid>/` 目录）。tooling 跨同平台多 RID 相同 → 只在 platform 的 primary RID
   （ios-arm64 / android-arm64 / browser-wasm）产一次，避免 merge-multiple 同名冲突。
2. **release-index.json**：加 `workloads.<wl> = { archive, sha256, runtimes: [<rid>…] }`（schema 既有草案
   见 runtime-workload-distribution.md）；修平台 `runtimes.<rid>.runtime` 指向真 runtime pack。
3. **launcher `workload install <wl>`（无 `--from`）**：resolve baseUrl（GitHub release tag，或 `--base-url`
   覆盖供镜像/本地验证）→ fetch `workloads.<wl>` → 下载+校验+解压 tooling 到 `runtimes/<ver>/workloads/<wl>/`
   → 遍历 `runtimes` 列表，按 `runtimes.<rid>.runtime` 下载+校验+解压每个 runtime pack 到
   `runtimes/<rid>/<ver>/` → 跑既有平台铺设分支（ios 改写 Package.swift / wasm symlink / android jniLibs+assets）。
   即「按需自动拉 runtime」（Decision 10）。
4. **`--base-url <url>` 选项**：`workload install` 加该选项，指向自托管/镜像/本地 manifest 根，缺省 GitHub。
   兼作本地端到端验证入口。

## Scope（允许改动的文件）

| 文件路径 | 变更 | 说明 |
|---------|------|------|
| `.github/workflows/release.yml` | MODIFY | Archive 步产 tooling+runtime 双归档（平台）；release-index.json 加 `workloads` 段 + 修平台 runtime 指向 |
| `src/toolchain/launcher/core/launcher_workload.z42` | MODIFY | `_cmdWorkloadInstall` 无 `--from` 分支 = 联网装；遍历 runtimes 拉 pack + 铺设 |
| `src/toolchain/launcher/core/launcher_network.z42` | MODIFY | 加 `_fetchWorkloadEntry(baseUrl, wl)` → `[archive, sha256, rid…]`；复用 `_extractArchive`/下载/校验 |
| `src/toolchain/launcher/core/launcher_cli.z42` | MODIFY | `workload install` ArgParser 加 `--base-url`；help 文案 |
| `docs/design/toolchain/runtime-workload-distribution.md` | MODIFY | manifest `workloads` 段定稿（移出 Deferred）；联网装流程落地 |
| `docs/spec/changes/add-workload-manifest-install/*` | NEW | 本变更规范 |

**只读引用**（理解上下文）：
- `scripts/xtask_package*.z42` — 产包目录命名（`z42-<ver>-<rid>-release/` tooling、`z42-runtime-<ver>-<rid>/` pack）
- `src/toolchain/launcher/core/launcher.z42` — `_runtimesDir()` / `_workloadsDir()`

## Out of Scope

- **B1 命令发现**（discovery-based dispatch）——独立 change。
- **manifest 签名 / GPG**（roadmap binary-package-signing 延后）。
- **真机多-slice xcframework 合并**——ios 仍按 ios-arm64 + iossim-arm64 两个独立 runtime pack 发，列入 `runtimes`。
- **host 平台校验**（ios 仅 macOS 装）——可作小增强，但本次 install 不强制 gate（记 design Deferred）。
- **workload update / 版本兼容矩阵**——后续。

## 验证策略（CI 不可本地跑 → 分半验证）

- **launcher 联网装**：**本地 mock manifest 服务器**端到端验证——xtask 产 wasm 包 → tar 成
  `z42-<v>-wasm.tar.gz` + `z42-runtime-<v>-browser-wasm.tar.gz` → 手写 release-index.json + sha256 →
  `python3 -m http.server` 起本地根 → `z42 workload install wasm --base-url http://localhost:PORT/<tag>/`
  → 验证下载+校验+解压+symlink 铺设 → `node import` 加载成功。wasm 最轻且包已在手。
- **CI release.yml**：**correct-by-construction**（无法本地跑真 release）。改动是 bash/jq 文本 + 既有
  Archive/index 模式的扩展，逐行对照现有 desktop 双归档逻辑核对；真验证落在下次打 tag。这一半的
  unvalidatable 性质显式记入 tasks.md（沿用 feedback_fix_validation_gap：不可本地验的部分追踪而非假装验过）。

## Open Questions

- [ ] `--base-url` 的归档 URL 布局：`<base>/<tag>/release-index.json` + `<base>/<tag>/<archive>`（对齐 GitHub
      release 资产布局）？— design.md 定。
- [ ] workload 无 `--version` 时默认版本来源：复用 `z42 install` 的 version→tag 映射（`v<ver>` / `nightly`）？
