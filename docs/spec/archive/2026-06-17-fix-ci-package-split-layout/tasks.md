# Tasks: fix-ci-package-split-layout

**变更说明：** `.github/workflows/ci.yml` 的平台打包 Verify 步 + publish-nightly archive/index 仍假设
B2-3 之前的**扁平包**布局（native libs/headers/libs 在 tooling pkgDir、android kotlin/cpp 在根、wasm
pkg-* 在 tooling）。B2-1/B2-3 把这些拆进独立 runtime pack（`z42-runtime-<v>-<rid>/`），tooling 变成
gradle/SwiftPM/npm 工程 → CI 三个 Verify 步红、nightly manifest 错配且缺 `workloads` 段。
**原因：** B2-3 runtime/workload 分包只改了 release.yml（B2-4），漏改 ci.yml 的 Verify + nightly。
**文档影响：** 无（CI 对齐已落地行为；设计文档已在 runtime-workload-distribution.md）。

- [x] 1.1 ci.yml `Verify iOS packages`：tooling 验 SwiftPM（Sources/Z42VM + 占位 Package.swift + 自包含 Z42VMC 头）；runtime pack（`z42-runtime-*-<rid>`）验 native/libz42.a + Z42VM.xcframework + include + libs + lipo arm64
- [x] 1.2 ci.yml `Verify Android packages`：tooling 验 gradle 工程（gradlew + settings + z42vm/src/main/{java/io/z42/vm, cpp/z42vm_jni.c, cpp/include/z42_host.h}）；runtime pack 验 .so/.a/include/libs + ELF arch
- [x] 1.3 ci.yml `Verify wasm package`：tooling 验 js/ + package.json；runtime pack 验 z42_wasm_bg.wasm + libz42.a + pkg-web + pkg-nodejs + libs + wasm-tools validate
- [x] 1.4 ci.yml `publish-nightly` archive 平台分支：tar runtime pack（`z42-runtime-*-<rid>`）→ `z42-runtime-nightly-<rid>.tar.gz` + primary RID tar tooling → `z42-nightly-<wl>.tar.gz`
- [x] 1.5 ci.yml `publish-nightly` release-index：加 `workloads.{ios,android,wasm}.{archive,sha256,host,runtimes}`（nightly 归档名）
- [x] 1.6 本地验证：Android Verify 对真包（artifacts/packages/z42-*-android-*）跑通；nightly index jq 干跑；YAML 合法
- [x] 1.7 commit + push（CI 自验）+ 归档

## 备注
- ios/wasm Verify correct-by-construction（本地无 xcode/wasm-bindgen 产物）；android Verify 对在手真包本地验。
- 真验 = push 后 CI 自己跑（这正是修复目标——让 CI 绿）。
