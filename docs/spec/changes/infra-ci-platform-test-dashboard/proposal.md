# Proposal: 平台测试 CI + 跨平台 dashboard（wasm + iOS Simulator + Android emulator）

## Why

`add-platform-test-pipeline` 落地了 `z42 xtask.zpkg test platform <p>` 三阶段框架（wasm 本地
7/7 验证）。但 CI 目前只 build package、不跑平台测试 → PR 可能悄悄打破移动/wasm 而无人发现。
把三平台测试接入 CI,各产 JUnit,经 GitHub Checks 聚合成 PR check runs = 跨平台测试 dashboard
（GitHub 即远程同步层,无需自建服务）。User 2026-06-16 要求,iOS 要真 Simulator。

## What Changes

- **CI 三 job**：test-wasm(ubuntu+Playwright)、test-ios(macos+iOS Simulator via `xcodebuild test`)、test-android(ubuntu+`reactivecircus/android-emulator-runner`+`gradlew connectedAndroidTest`)
- **iOS 真 Simulator**：扩 `IosBackend`（+ Package.swift iOS test target + scheme）支持 `xcodebuild test -destination 'platform=iOS Simulator'`；xcresult → JUnit
- **JUnit 聚合**：各 job 上传 junit → `dorny/test-reporter` 经 GitHub Checks 汇成 dashboard
- **Android CI 运行路径**：emulator-runner action 提供 emulator,CI 直接 `gradlew connectedAndroidTest`（绕过 backend 的 test.sh 自建 emulator）

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `.github/workflows/ci.yml` | MODIFY | 加 test-wasm / test-ios / test-android / report-aggregate 4 job |
| `scripts/xtask_test_ios.z42` | MODIFY | RunTests 支持 simulator 模式（`xcodebuild test -destination`）+ xcresult→junit |
| `src/toolchain/host/platforms/ios/Package.swift` | MODIFY | iOS test target / 平台声明（支持 Simulator 构建测试）|
| `docs/design/testing/cross-platform-testing.md` | MODIFY | CI dashboard 接线 + iOS simulator 实现原理 |
| `docs/spec/changes/ACTIVE.md` | MODIFY | 登记/释放 toolchain |

**只读引用：**
- `.github/workflows/ci.yml` 现有 build-and-test bootstrap + package-ios/android/wasm job（复用 setup 步骤）
- `scripts/xtask_test_{platform,wasm,android}.z42`、`src/toolchain/host/platforms/*`

## Out of Scope
- 真机(BrowserStack/Firebase Test Lab)——v1 simulator/emulator 即可
- 退役 platforms/*/{build,test}.sh（CI-proven 后另开 change）

## 验证模型（诚实）
CI 改动**只能 push 后看 GitHub Actions 迭代验证**,本地无法完整跑。采用 push → 观察
→ 修 的远程循环。wasm 本地已验证;iOS/Android 靠 CI runner。

## 并行
占 `toolchain`（ci.yml + IosBackend + Package.swift）。与 migrate-scripts-to-z42 等并行 —
User 多次授权;主动文件不与 packaging 脚本重叠。
