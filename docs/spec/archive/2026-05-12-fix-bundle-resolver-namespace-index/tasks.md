# Tasks: BundleZpkgResolver / AssetZpkgResolver namespace index

> 状态：🟢 已完成 | 创建：2026-05-12 | 归档：2026-05-12 | 类型：fix（mobile facade 默认 resolver bug）

## 进度概览

- [ ] 阶段 1: index 生成 + build-stdlib.sh
- [ ] 阶段 2: iOS resolver fix
- [ ] 阶段 3: Android resolver fix
- [ ] 阶段 4: 验证 + GREEN
- [ ] 阶段 5: 文档 + 归档

## 阶段 1: index 生成

- [ ] 1.1 `scripts/build-stdlib.sh` 在 flat view 段尾追加 `index.json` heredoc 写入
- [ ] 1.2 跑 `./scripts/build-stdlib.sh`，确认 `artifacts/build/libs/release/index.json` 存在，键集完整

## 阶段 2: iOS resolver fix

- [ ] 2.1 `src/toolchain/host/platforms/ios/Sources/Z42VM/ZpkgResolver.swift` 加 `BundleZpkgResolver` 内部 `loadIndex` + `resolve` 改 "index 优先 + fallback"
- [ ] 2.2 `src/toolchain/host/platforms/ios/build.sh` 拷 `index.json` 到 `Resources/stdlib/` 和 `Tests/Z42VMTests/Resources/stdlib/`
- [ ] 2.3 跑 `./build.sh` 确认 index.json 两处都在
- [ ] 2.4 跑 `swift test`：7/7 全绿（R1/R6/R7 此前 RED 现 GREEN）

## 阶段 3: Android resolver fix

- [ ] 3.1 `src/toolchain/host/platforms/android/z42vm/src/main/java/io/z42/vm/ZpkgResolver.kt` 加 `AssetZpkgResolver` 内部 `loadIndex` + `resolve` 改"index 优先 + fallback"
- [ ] 3.2 `src/toolchain/host/platforms/android/build.sh` 拷 `index.json` 到 `z42vm/src/main/assets/stdlib/`
- [ ] 3.3 本机无 NDK，不跑 `./build.sh`；symmetry 比照 iOS 实现走 review

## 阶段 4: 验证 + GREEN

- [ ] 4.1 iOS `swift test`：7/7
- [ ] 4.2 `./scripts/test-all.sh`（workflow.md 新规则）—— 5 stage 全绿
- [ ] 4.3 工作树内 add-ios-tests 文件（Package.swift / build.sh / Tests / examples/embedding/）保留**未 commit**，下个 spec resume

## 阶段 5: 文档 + 归档

- [ ] 5.1 `docs/design/runtime/embedding.md` §11 补 namespace index 段
- [ ] 5.2 移 `changes/fix-bundle-resolver-namespace-index/` → `archive/YYYY-MM-DD-fix-bundle-resolver-namespace-index/`
- [ ] 5.3 commit + push（type=fix, scope=host/platforms）

## 备注

- 本 spec 仅 commit resolver fix + index 生成 + 平台 build.sh 改动；**iOS test target / Package.swift testTarget / examples/embedding/ / Tests/Z42VMTests/ / 工作树中 Z42VM.xcframework 的 macos slice 改动不进本 commit**（归 add-ios-tests 下个 spec）
- Android resolver fix 与 iOS 同 commit 但无 instrumented test 验证；下个 add-android-tests spec 会跑通
- wasm resolver fix 推迟到 wasm spec
- v1 hardcode mapping；auto-discovery 进 design.md Deferred 段
