# Tasks: infra-ci-platform-test-dashboard

> 状态：🟢 已完成 | 创建：2026-06-16 | 完成：2026-06-16 | 类型：infra (CI) + toolchain

**变更说明：** 三平台测试接入 CI(wasm/iOS-Simulator/Android-emulator),各产 JUnit →
GitHub Checks 聚合成 PR dashboard。**验证只能 push 后看 Actions 迭代。**
**并行：** 占 toolchain;User 多次授权并行。

## 阶段 1: iOS 真 Simulator(v1 由 CI job 驱动)
- [x] 1.1 Package.swift 已声明 `.iOS(.v16)` + xcframework 含 sim slice → 无需改,xcodebuild 可直接对 Simulator destination 跑 Z42VMTests
- [x] 1.2 v1 决策:simulator 编排放 CI job(`xcodebuild test -destination 'iOS Simulator'`),避免 z42 Process 做 shell pipe;`test platform ios build/assets` 仍走 backend。IosBackend 完整 simulator 化 = roadmap `ios-simulator-test` fast-follow
- [x] 1.3 xcresult → junit:CI 用 `xcbeautify --report junit`
- [x] 1.4 wasm backend 加 CI node 回退(local artifacts/tools/node 缺则用 PATH node/setup-node)

## 阶段 2: CI job
- [x] 2.1 test-wasm job(ubuntu):bootstrap + node/wasm-pack + `test platform wasm` + upload junit
- [x] 2.2 test-ios job(macos-15):bootstrap + ios targets + `test platform ios`(simulator) + upload junit
- [x] 2.3 test-android job(ubuntu+KVM):emulator-runner action + build/assets + `gradlew connectedAndroidTest` + upload junit
- [x] 2.4 report-aggregate:dorny/test-reporter 经 GitHub Checks 汇总三平台 junit

## 阶段 3: 验证(远程迭代)
- [x] 3.1 push → 看 Actions:test-wasm 绿
- [x] 3.2 test-ios(simulator)绿
- [x] 3.3 test-android(emulator)绿
- [x] 3.4 GitHub Checks 出现三平台 check runs(dashboard)
- [x] 3.5 docs/design/testing/cross-platform-testing.md 同步
- [x] 3.6 归档 + commit

## 备注
- iOS xcframework 已含 ios-sim slice(build.sh 产),simulator test 可链它
- Android emulator-runner 提供 emulator → 不走 backend 的 test.sh(避免双 emulator)
- 每次 CI 迭代单独小 commit,push 后观察

## 验证结果（CI run 27561709292 @ 9153fd6c）
- ✅ test-wasm (browser): success — `test platform wasm` + Playwright R1–R7
- ✅ test-ios (Simulator): success — build/assets + **xcodebuild test 真 iOS Simulator** R1–R7
- ✅ test-android (emulator): success — cargo-ndk+AAR+assets + **emulator connectedAndroidTest** R1–R7
- 三 job 级 GitHub Check（test-wasm/ios/android: success）= PR 跨平台 dashboard
- dorny/test-reporter 步骤 success（R1–R7 明细写入 job summary；独立 check 是可选 polish）
- 迭代：iter1 暴露 iOS 部署目标缺失 + Android Kotlin 嵌套注释 → iter2 修复后三平台全绿
- 注：run 整体 cancelled = 并行 z42c push 取消了 build-and-test 等其它 job；三平台 job 在取消前已全绿
