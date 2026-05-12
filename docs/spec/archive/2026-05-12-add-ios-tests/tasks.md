# Tasks: 给 iOS facade 加 XCTest

> 状态：🟢 已完成 | 创建：2026-05-12 | 归档：2026-05-12 | 类型：test（contract 实现）

## 进度概览

- [x] 阶段 1: fixture 源 + build.sh
- [x] 阶段 2: Package.swift testTarget + XCTest 代码
- [x] 阶段 3: 验证 + GREEN
- [x] 阶段 4: README + 归档

## 阶段 1: fixture 源 + build.sh

- [x] 1.1 新增 `examples/embedding/hello.z42`（单行 `Console.WriteLine("hello, world");`）+ `.toml` *（落地在 `fix-bundle-resolver-namespace-index` commit `3da1d4b8`，共享 infra）*
- [x] 1.2 新增 `examples/embedding/multi_line.z42`（3 行）+ `.toml` *（同上）*
- [x] 1.3 `build.sh` 修 `LIBS_DIR` 从 `artifacts/z42/libs` → `artifacts/build/libs/release` *（同 `3da1d4b8`）*
- [x] 1.4 `build.sh` tooling check 段加 `aarch64-apple-darwin` rust target 校验 *（同上）*
- [x] 1.5 `build.sh` step 3 加 `cargo build --target aarch64-apple-darwin` *（同上）*
- [x] 1.6 `build.sh` step 5 加 `-library $MAC_LIB` 入 xcframework *（同上）*
- [x] 1.7 `build.sh` 新增 step 6：编 fixture .z42 → `Tests/Z42VMTests/Resources/test-fixtures/*.zbc` *（同上）*
- [x] 1.8 `build.sh` 新增 step 7：拷 stdlib zpkg + index.json 到 `Tests/Z42VMTests/Resources/stdlib/` *（同上 + namespace-index 修复一并进了 step 2 / 7）*
- [x] 1.9 `./build.sh` 全产物 OK

## 阶段 2: Package.swift testTarget + XCTest 代码

- [x] 2.1 `Package.swift` 加 `.testTarget(name: "Z42VMTests", ..., resources: [.copy(...), .copy(...)])` + `.binaryTarget(name: "Z42VMBinary", path: "Z42VM.xcframework")`
- [x] 2.2 新增 `Tests/Z42VMTests/Z42VMTests.swift` 骨架（imports + Collector helper）
- [x] 2.3 实现 `testSmokeHelloWorld()` (R1) ✅
- [x] 2.4 实现 `testBadZbcThrowsBadZbc()` (R2) ✅
- [x] 2.5 实现 `testResolveUnknownEntryThrowsEntryNotFound()` (R3) ✅
- [x] 2.6 实现 `testInvokeWrongArgCountThrowsArgMismatch()` (R4) ✅
- [x] 2.7 实现 `testMapResolverWithoutCorelibSurfacesAtInvoke()` (R5) ✅
- [x] 2.8 实现 `testInitShutdownLifecycleRoundtrip()` (R6) ✅
- [x] 2.9 实现 `testMultiLineStdoutPreservesOrder()` (R7) ✅

## 阶段 3: 验证 + GREEN

- [x] 3.1 `swift test` 在 macOS arm64 host 跑通 7/7 测试
- [x] 3.2 `./scripts/test-all.sh` 全绿（workflow.md 新规则；6 stage）

## 阶段 4: README + 归档

- [x] 4.1 `README.md` 加 "Run tests" 段
- [x] 4.2 `README.md` Limitations 段把 "Demo / XCTest / CI: 推迟" 改为 "Demo / CI: 推迟"
- [x] 4.3 移 `changes/add-ios-tests/` → `archive/2026-05-12-add-ios-tests/`
- [x] 4.4 commit + push（type=test，scope=host/ios）

## 实施备注

- 实施期出现 **依赖前置变更**（workflow.md 6.5 中断条件 5）：R1 smoke 暴露 `BundleZpkgResolver` namespace 映射 bug，跳出本 spec Scope。按 workflow `feedback_problem_first_then_defer` 停下、汇报、拉独立 spec `fix-bundle-resolver-namespace-index` 先解决，归档后回到本 spec 把 R1/R6/R7 跑绿。
- 期间共享 infra（`examples/embedding/`、iOS `build.sh` 全套更新、namespace index 双份拷贝）落地在 resolver 修复 commit `3da1d4b8` 中；本 spec commit 只剩 Package.swift + Tests/ + README + spec 文档。
- macOS slice 走单 arch（arm64-only）；x86_64 不补（D1）。
- `swift test` 在 macOS host 上跑；`xcodebuild test -destination iOS Simulator` 留给 `add-ios-ci` spec（D2）。
- Resources 用 `.copy("Resources/test-fixtures")` + `.copy("Resources/stdlib")` 保子目录层级（实施时发现 `.process` 会拍平目录，与 design.md 假设的 `Bundle.module.url(..., subdirectory:)` 查询不兼容）。
