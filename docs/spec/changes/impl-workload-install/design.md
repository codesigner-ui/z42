# impl-workload-install — design（B2）

## Architecture

```
z42 workload install ios
  1. 读 manifest workloads.ios（host 校验：ios 仅 macOS）
  2. 下载 z42-<ver>-ios.tar.gz（tooling = facade+模板+glue）→ runtimes/<ver>/workloads/ios/
  3. 按需拉 target runtime pack（ios-arm64 / iossim-arm64）→ runtimes/<rid>/<ver>/（Decision 10，复用现下载层）
  4. done → 现 baked `export/publish ios` 读这些目录即可用
```

复用：launcher_network 的下载/解压 + 现 `z42 install` 的 manifest-first（release-index.json）+ sha + tgz 流式解压（add-launcher-install）。

## Decisions（需 User 拍）

### D1：workload 包 = tooling；runtime 是组合进的 component
`z42-<ver>-<wl>.tar.gz` 装 **facade + 工程模板 + native glue**；**target runtime pack 不在包内**，install 时按需单独拉。
**理由**：避免重复（ios 有 device+sim 两个 runtime）；desktop workload 无 runtime（用 host）；对齐 runtime-workload-distribution.md "workload = 组合 {target runtime pack} + {tooling}"。✅ 推荐采纳。

### D2：`workload install` 吸收现 `install --rid`，host-only install 不变
现 `z42 install <ver> --rid ios-arm64`（add-export-command）与 Decision 8（install 只装 host）冲突。
**推荐**：`workload install` 成用户面唯一拉 target runtime 的路径，内部复用 `--rid` 下载逻辑；`z42 install <ver>` 保持只装 host。`--rid` 降为内部组件下载（用户面收敛，pre-1.0 直接重整）。

### D3：按需拉 runtime = eager（install workload 时一并拉）
**推荐**：install workload 即把它依赖的 target runtime pack 一并拉齐（对标 `dotnet workload install ios` 一并带 ios runtime）。install 完即 ready，无首次-use 延迟。

### D4：本地验证入口 `--from <path>`（解 egg 问题）
**推荐**：`z42 workload install <wl> --from <tarball|dir>` 跳过 manifest 从本地装。配合 xtask 本地产 workload 包 → `--from` 装 → 验 `export ios` 能找到 SDK。让 B2 不依赖 release 即可 e2e；CI 上传 release 走后续。

## Implementation Notes
- 产包复用现 `xtask_package_{ios,android,wasm}` 的 facade/SDK 打包逻辑（封成 `z42-<ver>-<wl>.tar.gz`）。
- 下载/manifest 复用 launcher_network + 现 install；workload 安装目录 `runtimes/<ver>/workloads/<wl>/`。
- 扫描/注册循环按 [common-pitfalls.md §1](../../../../.claude/rules/common-pitfalls.md) **显式 sort**。

## Testing Strategy
- e2e：xtask 产 workload 包（local）→ `z42 workload install <wl> --from <pkg>` → 断言 `runtimes/<ver>/workloads/<wl>/` 内容 + `export ios` 能解析到 SDK。
- `workload list` / `remove` 的 [Test]/e2e。

## 分期（B2 内部）
- **B2-1**：workload 包格式 + xtask 产包（本地可产可查）。
- **B2-2**：`z42 workload install --from`（本地装）+ list/remove + 按需拉 runtime（复用下载层）。
- **B2-3**：CI release.yml 上传 workload 包 + `workload install` 走 manifest（联网）。
> B2-1/B2-2 本地闭环可验；B2-3 依赖 release，可后续。
