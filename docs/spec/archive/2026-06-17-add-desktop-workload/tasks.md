# Tasks: add-desktop-workload

> 状态：🟢 已完成 | 创建：2026-06-17 | 锁：`toolchain`
> proposal/design/spec 见同目录。Decision 9 完全门控。本机 macos-arm64 全程可验。

## 阶段 1：xtask 产 desktop workload + SDK 去 apphost
- [x] 1.1 xtask_package_desktop.z42：`_packageDesktop` 移除 SDK `bin/apphost`(`.exe`) 拷贝
- [x] 1.2 xtask_package_desktop.z42：NEW `_buildDesktopWorkload(root, rid, version, profile)` → `z42-workload-<v>-desktop-<rid>/`：拷 cargo apphost 二进制 + 写 manifest.toml（kind=workload-tooling, rid, host=[rid], runtimes=[], apphost="apphost"）
- [x] 1.3 xtask_package.z42：`_buildPackageCore` desktop 分支追加 `_buildDesktopWorkload`

## 阶段 2：launcher install desktop + publish 门控
- [x] 2.1 launcher_network.z42：per-host manifest 解析（`workloads.desktop.hosts.<rid>` → archive/sha256；新 helper 或扩 `_fetchWorkloadEntry`）
- [x] 2.2 launcher_workload.z42：install desktop 分支——本地 `--from` 拷 tooling；联网按 host 取 archive 下载+解压到 `workloads/desktop/`；跳过 runtime loop / 平台铺设
- [x] 2.3 apphost.z42：`_templatePath` 新查找序（env override → `runtimes/<v>/workloads/desktop/apphost`；去 baked launcher-dir/z42vm 旁）
- [x] 2.4 launcher_export.z42：`_cmdPublishDesktop` 门控——stub 缺失则报「run z42 workload install desktop」非零退出
- [x] 2.5 编译 launcher 清编

## 阶段 3：CI
- [x] 3.1 release.yml：桌面 RID 产 `z42-workload-<v>-desktop-<rid>.tar.gz`；release-index `workloads.desktop.hosts.<rid>`
- [x] 3.2 ci.yml publish-nightly：同上（nightly 名）；jq per-host schema 干跑

## 阶段 4：验证 + docs + 归档
- [x] 4.1 本地 e2e（macos-arm64，权威）：xtask 产 SDK(无 apphost)+desktop workload → `workload install desktop --from` → `publish desktop` 产 apphost exe → **跑 exe 验证** → 未装路径报门控错误
- [x] 4.2 联网 mock：`workload install desktop --base-url <mock per-host>` 放行 + host 不支持拒
- [x] 4.3 docs/design/toolchain/runtime-workload-distribution.md：workloads.desktop per-host schema + 门控流程；apphost-as-config 状态从「baked」改「workload 交付」
- [x] 4.4 GREEN（launcher 清编 + 本地 e2e）+ 归档 + 释放锁 + commit

## 备注
- CI correct-by-construction（同既往，真验下次 tag/nightly）。
- auto-pull / 跨 RID publish / export desktop / B1 → Out of Scope（入 design Deferred / roadmap）。

## 验证结果（macos-arm64 本地全程 + CI-sim）
- xtask：SDK `bin/` 无 apphost ✓；desktop workload 包含 apphost+manifest ✓
- 门控：未装 desktop workload → `publish desktop` 报「run z42 workload install desktop」非零退出 ✓
- install：本地 `--from` + 联网 per-host manifest（mock）→ `workloads/desktop/apphost` 就位 ✓；wrong-host neg「not available for this host」✓；runtimes:[] 无 runtime loop ✓
- publish：装后 `publish desktop` 找到 workload stub → 产 Mach-O apphost exe → **跑出 "hello, world"** ✓（含 publish_dir 自动 mkdir 修复）
- CI：release.yml sim 产 4 个 desktop workload 归档 + 必需-sha 门 exit 0 + `workloads.desktop.hosts`(4 RID)/`runtimes:[]` ✓；host Verify 加断言（SDK 无 apphost + desktop workload 有 apphost）；YAML 合法
