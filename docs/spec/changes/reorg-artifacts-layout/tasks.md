# Tasks: reorg-artifacts-layout
**变更说明：** `artifacts/` 归整为 build / packages / deps 三个顶层桶（约定）；launcher 产物 + dev home 归入 `build/toolchain/launcher/`（镜像 `src/toolchain/launcher/`），不再落在 `src/.../core/dist/`。
**原因：** `launcher-home` 游离顶层、launcher.zpkg 构建产物落在 `src/` 内（唯一违反“产物进 artifacts”的点）。
**文档影响：** 新增 `docs/design/compiler/build-artifacts-layout.md`（唯一权威布局图）。
**类型：** refactor（无对外行为变化；纯产物路径 + 脚本）。

## 阶段 1: launcher 归位 + 布局文档（✅ 完成）
- [x] 1.1 launcher.zpkg 出 src：`z42.launcher.z42.toml` out_dir → `artifacts/build/toolchain/launcher`（相对 ../../../../，已验证 zpkg 落新路径）
- [x] 1.2 launcher-home → `artifacts/build/toolchain/launcher/home`（launcher-env.sh，已端到端验证 `z42 which`）
- [x] 1.3 package_desktop.sh step 2c：launcher.zpkg cp 源同步新路径
- [x] 1.4 `docs/design/compiler/build-artifacts-layout.md`（三桶 + build 镜像 src + 为何 build/libs 暂不改名）
- [x] 1.5 修 CI：launcher-env.sh 处理 Windows `.exe`（trampoline + z42vm），修复 regen-golden (Windows) 步 `no z42vm found` 失败
- [x] 1.6 清理旧 `artifacts/launcher-home`（gitignored，本地 rm）

## 阶段 2: build/libs → build/libraries/_flat（延后 — 见备注）
- [ ] 2.x 暂不做：`build/libs` 是 z42vm 烘焙的 `Z42_LIBS` 默认 fallback 路径
      （main.rs / config.rs / host_tests.rs），改名属运行时默认变更，需改 VM +
      重编 + 改 host 测试，超出本次“目录组织”refactor。拆为独立
      `reorg-artifacts-future-libs-flat`（roadmap Deferred 索引）。

## deps 桶
- 当前无填充物（cargo/nuget 缓存在 ~/.cargo /~/.nuget）；约定 + 布局文档说明即可。
  `artifacts/` 整体 gitignore，故 deps/ 不放 tracked README。

## 备注
- run/ 第四桶评估后砍掉：唯一临时态 launcher-home 折叠进 build/toolchain/launcher/home。
- CI 另一红：`Z42NetHttpDigestMd5Tests.test_digest_unsupported_qop_auth_int_throws`
  失败属并行 session 的 `add-digest-auth-int-and-stale`（他们 ship 了 auth-int 支持
  832cb06d 但留了断言“auth-int 不支持→抛”的旧测试）；非本变更范围，交该 session 修。
