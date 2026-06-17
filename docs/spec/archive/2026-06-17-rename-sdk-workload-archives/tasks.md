# Tasks: rename-sdk-workload-archives  🟢 已完成

**变更说明：** 发布归档命名加 kind 前缀，消除 SDK 与 workload 同为 `z42-<v>-X` 的视觉歧义：
- SDK：`z42-<v>-<rid>.tar.gz/.zip` → `z42-sdk-<v>-<rid>.tar.gz/.zip`
- workload：`z42-<v>-<wl>.tar.gz` → `z42-workload-<v>-<wl>.tar.gz`
- runtime（`z42-runtime-`）/ launcher（`z42-launcher-`）不动（已带前缀）。

**原因：** 用户 review 发布资产列表时 SDK 与 workload 不可区分（manifest key 已消歧，但人工浏览不便）。
**文档影响：** runtime-workload-distribution.md manifest schema 示例。

**消费端不破**：install-z42.sh 主路径 + launcher 联网装从 manifest 读 archive 名 → 自动跟随；仅
manifest-absent fallback 硬编码（install-z42.sh:252 + launcher_network.z42:51,119）需同步。download-bootstrap
用上一 nightly（self-heal 不变量）→ 改名无瞬时破坏。

- [x] 1.1 release.yml：Archive SDK dst → `z42-sdk-<v>-<rid>`、workload dst → `z42-workload-<v>-<wl>`；release-index `get_sha` + `sdk.archive` + `workloads.<wl>.archive` 改名
- [x] 1.2 ci.yml publish-nightly：Archive SDK → `z42-sdk-nightly-<rid>`、workload → `z42-workload-nightly-<wl>`；release-index 改名；notes 表改名；顶部 download URL 注释改名
- [x] 1.3 launcher_network.z42：`_cmdInstall`(51) + `_cmdSelfUpdate`(119) 的 SDK fallback → `z42-sdk-`
- [x] 1.4 install-z42.sh:252 + **install-z42.bat:70**（windows）fallback → `z42-sdk-`
- [x] 1.5 docs runtime-workload-distribution.md：schema 示例 sdk/workload archive 名 + 命令面
- [x] 1.6 验证：launcher 清编；release.yml + ci.yml CI-sim（archive 名 + 必需-sha 门 + index）；YAML 合法
- [x] 1.7 commit + push（CI 自验 + 下次 nightly 用新名）+ 归档

## 备注
- 不改 xtask 包**目录**名（`z42-<v>-<rid>-release` / `z42-runtime-<v>-<rid>`，内部产物，非发布）；仅改发布**归档**名。Verify 步 glob 用目录名 → 不受影响。

## 余下 doc-sync（独立，非本变更范围）
- docs/design/runtime/embedding.md §11.9「分发 package 形态」表（574-578）仍是**分包前**模型（每 RID 一个 SDK 包，含 mobile）——已被 runtime/SDK/launcher/workload 四分包整体淘汰，非仅命名问题；留独立 doc-sync 重写，不在本命名变更内半修。
- docs/design/toolchain/runtime-workload-distribution.md:7「现状（起点）」+ 各 archive/spec（docs/spec/archive/*）= 历史快照，不改。

## 验证结果
- launcher 清编 ✓；release.yml+ci.yml YAML 合法 ✓；release.yml CI-sim：16 归档全带 sdk-/runtime-/workload- 前缀 + 必需-sha 门 exit 0 + 0 悬挂 ✓；ci.yml 无残留旧名 ✓。docs：runtime-workload-distribution.md schema + launcher.md asset 名同步。
