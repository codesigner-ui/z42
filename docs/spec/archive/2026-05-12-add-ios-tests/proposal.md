# Proposal: 给 iOS facade 加 XCTest（add-ios-tests）

## Why

`platforms/ios/` 落地 (`2026-05-12-add-platform-ios`) 时把 demo / XCTest / CI 推迟到后续 spec。本 spec 落地 XCTest 部分，按照 [`platform-test-contract`](../../archive/2026-05-12-define-platform-test-contract/specs/platform-test-contract/spec.md) R1–R7 实现 Swift facade 的最小自动化测试集。

落地后效果：开发机执行 `swift test` 即跑通 7 个 facade 契约 scenario，CI 接入只需一个 `swift test` step。

## What Changes

1. **Package.swift 加 `.testTarget`** + macOS 平台支持，使 `swift test` 在 host macOS 上能 link `Z42VM.xcframework` 的 macos slice。
2. **build.sh** 修 stale 路径（`artifacts/z42/libs/` → `artifacts/build/libs/release/`）、新增 macOS slice cargo 编译 + lipo + xcframework 入口、新增 test fixture 编译步骤（用 host `z42c` 编 `examples/embedding/*.z42` → `Resources/test-fixtures/*.zbc`）。
3. **新增 fixture 源**：`examples/embedding/hello.z42`（单行 `Console.WriteLine`）和 `examples/embedding/multi_line.z42`（三行）。
4. **新增 Tests/Z42VMTests/Z42VMTests.swift**：R1–R7 各一个 XCTest 方法，按 contract spec 标号命名。
5. **README** 加 "Run tests" 段。

## Scope（允许改动的文件）

| 文件路径 | 变更类型 | 说明 |
|---------|---------|------|
| `src/toolchain/host/platforms/ios/Package.swift`                            | MODIFY | 加 `.testTarget(name: "Z42VMTests", ...)` + `.macOS(.v13)` 已就位；resources 规则 |
| `src/toolchain/host/platforms/ios/build.sh`                                 | MODIFY | 修 stale `artifacts/z42/libs` 路径；加 macOS x2 target + lipo macos-universal；加 `-library $MAC_UNIVERSAL` 到 xcodebuild；加 fixture compile 段（z42c → `.zbc`）|
| `src/toolchain/host/platforms/ios/Tests/Z42VMTests/Z42VMTests.swift`        | NEW    | R1–R7 XCTest 实现 |
| `src/toolchain/host/platforms/ios/Tests/Z42VMTests/Resources/`              | NEW (dir) | `test-fixtures/hello.zbc` / `test-fixtures/multi_line.zbc`（build.sh 产物，从 `Resources/test-fixtures/` 复制进来）|
| `src/toolchain/host/platforms/ios/README.md`                                | MODIFY | 加 "Run tests" 段；Limitations 段移除 "Demo / XCTest / CI: 推迟" |
| `examples/embedding/hello.z42`                                              | NEW    | 单行 `Console.WriteLine("hello, world");` |
| `examples/embedding/hello.z42.toml`                                         | NEW    | minimal manifest（如需）|
| `examples/embedding/multi_line.z42`                                         | NEW    | 三行 `Console.WriteLine("a"); ...("b"); ...("c");` |
| `examples/embedding/multi_line.z42.toml`                                    | NEW    | 同上 |
| `docs/spec/changes/add-ios-tests/{proposal,design,tasks}.md`                | NEW    | 本 spec 文档集 |
| `docs/spec/changes/add-ios-tests/specs/ios-tests/spec.md`                   | NEW    | scenario 平台映射（XCTest 方法名）|

**只读引用：**

- `src/toolchain/host/platforms/ios/Sources/Z42VM/*.swift` — Facade API 形态
- `src/toolchain/host/platforms/ios/Sources/Z42VMC/include/*.h` — C ABI 头
- `docs/spec/archive/2026-05-12-define-platform-test-contract/specs/platform-test-contract/spec.md` — 契约 R1–R7

## Out of Scope

- iOS simulator 上 `xcodebuild test` 运行（本 spec 用 macOS host `swift test`，足以覆盖 facade glue；simulator 端跑作为 CI 后续 spec）
- Demo app（独立 `add-ios-demo` spec）
- CI 接入（独立 `add-ios-ci` spec）
- R8 threading 测试（contract Deferred）
- Android / wasm 测试（兄弟 spec）

## Open Questions

- [ ] **macOS slice 工作量**：要加 `aarch64-apple-darwin` + `x86_64-apple-darwin` 两个 cargo target、lipo macos-universal、xcframework 入口；总体 ~30 行 build.sh 改动 + 验证 4 个 target 都编通。OK 吗？还是先 `swift test` 单 host arch（macOS arm64-only）跑通再补 x86_64？我倾向**先单 arch**（arm64-only macOS）落地，等出现 x86_64 host 需求再补。
- [ ] **fixture 源放哪**：建议 `examples/embedding/` 子目录（与 `examples/hello.z42` 隔离）。或者直接 `examples/hello_embed.z42` 平铺？我倾向子目录，将来 multi_line / large_fixture / stress 都进 `examples/embedding/`。
