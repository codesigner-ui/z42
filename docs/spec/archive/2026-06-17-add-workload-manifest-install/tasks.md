# Tasks: add-workload-manifest-install（B2-4）

> 状态：🟢 已完成 | 创建：2026-06-17 | 锁：`toolchain`
> proposal/design/spec 见同目录。验证分半：launcher 本地 mock e2e（权威）/ CI correct-by-construction（unvalidatable）。

## 进度概览
- [x] 阶段 1：launcher 联网装（核心新逻辑，本地可验）
- [x] 阶段 2：CI release.yml（correct-by-construction）
- [x] 阶段 3：docs 同步 + 验证 + 归档

## 阶段 1：launcher 联网装
- [x] 1.1 launcher_network.z42：抽 `byte[] _downloadVerified(string baseUrl, string asset, string sha)`（下载+HTTP 检查+sha 校验；`_cmdInstall` 顺带复用去重）
- [x] 1.2 launcher_network.z42：加 `string[] _fetchWorkloadEntry(string baseUrl, string wl)` → `[archive, sha256, rid…]`（解析 `workloads.<wl>`）
- [x] 1.3 launcher_workload.z42：把 B2 inline 的平台铺设逻辑提取成 `_bedRuntimeIntoWorkload(wlDest, rtDest, rid, ver)`（ios 改写 Package.swift / wasm symlink / android jniLibs+assets），`--from` 与联网两路共用
- [x] 1.4 launcher_workload.z42：`_cmdWorkloadInstall` 无 `--from` 分支 = 联网装（resolve baseUrl → fetch workloads.<wl> → 下载+解压 tooling 到 `runtimes/<ver>/workloads/<wl>/`（staging+原子 move）→ 遍历 runtimes：`_fetchManifest(rid,"runtime")` → 下载+解压 → `runtimes/<rid>/<ver>/` → `_bedRuntimeIntoWorkload`）
- [x] 1.5 launcher_cli.z42：`workload install` ArgParser 加 `--base-url`；usage/help 文案更新（`--from` 可选已在 B2）
- [x] 1.6 编译 launcher 清编 exit 0

## 阶段 2：CI release.yml
- [x] 2.1 Archive 步：加 `_wl_of(rid)`（ios-*/iossim-*→ios，android-*→android，browser-wasm→wasm）+ `_is_primary(rid)`（ios-arm64/android-arm64/browser-wasm）；平台 RID 产 `z42-runtime-<v>-<rid>.tar.gz`（来自 `z42-runtime-<v>-<rid>/`）+ primary 额外产 `z42-<v>-<wl>.tar.gz`（来自 `z42-<v>-<rid>-release/`）
- [x] 2.2 release-index.json：加顶层 `workloads.{ios,android,wasm}.{archive,sha256,runtimes:[…]}`；平台 `runtimes.<rid>.runtime` 指向真 pack（归档名不变，字节修对）；`get_sha` 取 workload 归档 sha
- [x] 2.3 `jq -n` 本地干跑校验 JSON 合法（mock 变量）

## 阶段 3：验证 + docs + 归档
- [x] 3.1 **本地 mock e2e（android，权威）**：用 **android**（本地 wasm 工具链不全：无 node/wasm-bindgen；且 android 包在手 + 验**多-RID** 联网路径更强）。xtask 产的 tooling + 两 runtime pack → `tar --format gnutar`（匹配 CI 的 Linux GNU tar；macOS bsdtar 默认 PAX 头 z42 Tar.ExtractStream 不解析）→ 手写 release-index.json（workloads.android.runtimes=[android-arm64,android-x64]）+ sha256 → `python3 -m http.server` → `z42 workload install android --base-url http://localhost:PORT/v0.3.0 --version 0.3.0`。**结果**：tooling + 两 pack 下载+sha校验+解压+铺设（jniLibs/{arm64-v8a,x86_64} + 22 zpkg/各）；`gradlew :z42vm:assembleDebug` → AAR ✓。反例：① 缺 workload（install ios）→ "not found in manifest" 非零退出；② 篡改 tarball → checksum mismatch 中止、不留半装态
- [x] 3.2 回归：`--from`/`--runtime` 本地装路径不触网、行为同 B2（dispatch 按 `--from`/`--runtime` 在场分流）
- [x] 3.3 docs/design/toolchain/runtime-workload-distribution.md：manifest `workloads` 段定稿（移出 Deferred）、联网装流程落地、命令面 `workload install [--base-url]`
- [x] 3.4 GREEN：launcher 清编；本地 e2e 全绿
- [x] 3.5 归档 + 释放 toolchain 锁 + commit

## 备注 / 已知 unvalidatable
- **CI release.yml 不可本地跑真 release**（feedback_fix_validation_gap）：阶段 2 correct-by-construction，逐行对照
  既有 desktop 双归档 + jq 干跑；真验证 = 下次打 tag。本 change 不因此阻塞——核心新逻辑（launcher 下载-校验-解压-铺设）
  由阶段 3.1 本地 mock 完整覆盖。
