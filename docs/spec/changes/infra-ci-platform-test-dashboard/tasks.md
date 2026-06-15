# Tasks: infra-ci-platform-test-dashboard

> 状态：🟡 进行中 | 创建：2026-06-16 | 类型：infra (CI) + toolchain

**变更说明：** 三平台测试接入 CI(wasm/iOS-Simulator/Android-emulator),各产 JUnit →
GitHub Checks 聚合成 PR dashboard。**验证只能 push 后看 Actions 迭代。**
**并行：** 占 toolchain;User 多次授权并行。

## 阶段 1: iOS 真 Simulator(v1 由 CI job 驱动)
- [x] 1.1 Package.swift 已声明 `.iOS(.v16)` + xcframework 含 sim slice → 无需改,xcodebuild 可直接对 Simulator destination 跑 Z42VMTests
- [x] 1.2 v1 决策:simulator 编排放 CI job(`xcodebuild test -destination 'iOS Simulator'`),避免 z42 Process 做 shell pipe;`test platform ios build/assets` 仍走 backend。IosBackend 完整 simulator 化 = roadmap `ios-simulator-test` fast-follow
- [x] 1.3 xcresult → junit:CI 用 `xcbeautify --report junit`
- [x] 1.4 wasm backend 加 CI node 回退(local artifacts/tools/node 缺则用 PATH node/setup-node)

## 阶段 2: CI job
- [ ] 2.1 test-wasm job(ubuntu):bootstrap + node/wasm-pack + `test platform wasm` + upload junit
- [ ] 2.2 test-ios job(macos-15):bootstrap + ios targets + `test platform ios`(simulator) + upload junit
- [ ] 2.3 test-android job(ubuntu+KVM):emulator-runner action + build/assets + `gradlew connectedAndroidTest` + upload junit
- [ ] 2.4 report-aggregate:dorny/test-reporter 经 GitHub Checks 汇总三平台 junit

## 阶段 3: 验证(远程迭代)
- [ ] 3.1 push → 看 Actions:test-wasm 绿
- [ ] 3.2 test-ios(simulator)绿
- [ ] 3.3 test-android(emulator)绿
- [ ] 3.4 GitHub Checks 出现三平台 check runs(dashboard)
- [ ] 3.5 docs/design/testing/cross-platform-testing.md 同步
- [ ] 3.6 归档 + commit

## 备注
- iOS xcframework 已含 ios-sim slice(build.sh 产),simulator test 可链它
- Android emulator-runner 提供 emulator → 不走 backend 的 test.sh(避免双 emulator)
- 每次 CI 迭代单独小 commit,push 后观察
