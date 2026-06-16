# impl-workload-install — proposal（B2）

> 状态：**DRAFT**（2026-06-17）。build-workload-subsystem 的 B2：实现 workload 包格式 + `z42 workload install/list/remove` + 按需拉 runtime（[runtime-workload-distribution.md](../../../design/toolchain/runtime-workload-distribution.md) 的 workload 段）。
> 子系统锁：`toolchain`（+ 可能 CI/release）。

## Why

要"支持平台开发",用户得能 `z42 workload install ios` 把 ios 平台的能力束（facade + 工程模板 + target runtime）装进来，之后 `z42 export/publish ios` 即可用。现状：`workloads` manifest 段未实施；只有 `z42 install <ver> --rid ios-arm64`（add-export-command）能装单个 runtime pack，无 dotnet 式 workload UX、无能力束打包、无按需拉 runtime。

## B2 解决 B1 的排序（Z 重排的关键）

**B2 装的是 workload 的「数据」（facade / 模板 / target runtime），现 baked 的 `export/publish ios` 命令直接用这批数据**（它们已读 `~/.z42/runtimes/<rid>/<ver>/`）。所以 B2 在 B1 之前成立：B2 装数据、baked 命令用、B1 之后再把命令本身改成发现式。无空转。

## What

1. **workload 包格式**：`z42-<ver>-<wl>.tar.gz` = {target runtime pack} + {facade + 工程模板 + native glue}。
2. **produce（B2a）**：xtask package 产 workload 包（local 可测，CI 后续上传 release）。
3. **install（B2b）**：`z42 workload install <wl>` 读 manifest `workloads` 段 → 下载 + 校验 sha + 解压到 `runtimes/<ver>/workloads/<wl>/`；**target runtime 缺失则按需自动拉**（Decision 10）。
4. **`z42 workload list / remove`**；host 校验（`workloads.<wl>.host`，ios 仅 macOS）。

## ⚠️ Egg 问题（同 B1，要先对齐）

`z42 workload install`（B2b）需要 workload 包（B2a）存在才能装。所以 **B2a（产包）必须先于/同步 B2b**。本地验证：xtask 产一个 workload 包到本地 → `z42 workload install <wl> --from <local>` 装它（或 install 支持 file:// / 本地路径）。CI 上传 release = 后续（依赖 release.yml 改动）。

## Scope（待 design 决策定稿后细化）

| 文件 | 变更 | 说明 |
|------|------|------|
| `scripts/xtask_package_workload.z42`（或扩 xtask_package）| NEW/MODIFY | 产 `z42-<ver>-<wl>.tar.gz` |
| `src/toolchain/launcher/core/launcher_workload.z42` | NEW | `workload install/list/remove` |
| `src/toolchain/launcher/core/launcher_cli.z42` | MODIFY | workload router |
| `src/toolchain/launcher/core/launcher_install.z42`（现下载机制）| MODIFY/复用 | 复用 manifest-first + sha + tgz 流式解压 + 按需拉 runtime |
| runtime-workload-distribution.md | MODIFY | 落地包内容清单 + install 流程定稿 |
| 测试 | NEW | 产包 + 本地 install + list/remove 的 e2e |

## Out of Scope

- B1 命令发现（workload 命令进 `z42 -h` 树）——B2 装数据，命令仍 baked。
- CI release.yml 上传 workload 包（本 change 后续；本次 local 产包 + 本地 install 验证）。
- mobile publish/run-on-device 生命周期（B5）。

## Open Questions（design.md 决策，需 User 拍）

- [ ] workload 包**内容清单**（facade/模板/runtime 怎么组织进包）。
- [ ] install 与现有 `z42 install <ver> --rid`（add-export-command）的关系：吸收 / 并行 / 复用其下载层。
- [ ] 按需拉 runtime 的触发点（install workload 时一并拉 / 首次 export 用到时拉）。
- [ ] 本地 install 入口（`--from <path>` / `file://` / 约定目录）供无 release 验证。
