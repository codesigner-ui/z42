# Tasks: split-release-runtime-package

> 状态：🟢 已完成 | 创建：2026-06-14 | 归档：2026-06-14

**变更说明：** release.yml 追加发布独立 runtime 包（`z42-runtime-<ver>-<rid>.*`）；现有 SDK 包当 launcher；release-index.json 升级到 `runtimes.<rid>.runtime` + `.launcher` 新格式，平台 RID 只有 `.runtime`。
**原因：** `z42 install <ver>` 和 `z42 install <ver> --rid ios-arm64` 需要有真正独立的 runtime 包可下载；SDK 包不再被当 runtime 包使用。
**文档影响：** `docs/design/toolchain/runtime-workload-distribution.md`（标记 manifest schema 已实施）

## 进度

- [x] 1.1 ACTIVE.md 锁 toolchain
- [x] 1.2 创建 tasks.md
- [x] 2.1 `release.yml` Archive 步骤：desktop RID 追加打包 `z42-runtime-*`；platform RID cp 主包为 `z42-runtime-*` 别名
- [x] 2.2 `release.yml` release-index.json 步骤：升级为 `runtime` + `launcher` 子键格式，含全部 9 RID
- [x] 3.1 `docs/design/toolchain/runtime-workload-distribution.md` — manifest schema 标记已实施
