# Design: iOS facade XCTest 落地

## Architecture

```
host macOS:
  ┌─────────────────────────────────────────────────────────┐
  │ swift test                                              │
  │   ├─ Z42VMTests target (Tests/Z42VMTests/*.swift)       │
  │   │     ├─ Bundle.module/test-fixtures/hello.zbc       │
  │   │     ├─ Bundle.module/test-fixtures/multi_line.zbc  │
  │   │     └─ Bundle.module/stdlib/*.zpkg                  │
  │   └─ depends on Z42VM product                           │
  └────────────────┬────────────────────────────────────────┘
                   │
                   ▼ links macos-arm64 slice
  ┌─────────────────────────────────────────────────────────┐
  │ Z42VM.xcframework/macos-arm64/libz42_platform_ios.a    │
  │   ↳ 静态链接 libz42 + libffi                            │
  └─────────────────────────────────────────────────────────┘

build.sh:
  step 1  tooling check (含 +macOS rust targets)
  step 2  stdlib copy (artifacts/build/libs/release → Resources/stdlib)
  step 3  cargo build × 4 target (iOS device + iOS sim × 2 + macOS arm64)
  step 4  lipo simulator slices (现状) + macos slice (新, 单 arch 时无需 lipo)
  step 5  xcodebuild -create-xcframework (+ -library $MAC)
  step 6  z42c 编 fixture → Resources/test-fixtures/*.zbc (新)
```

## Decisions

### D1: 首版 macOS slice 单 arch（arm64-only）

**问题：** macOS slice 要不要双 arch（arm64 + x86_64 universal）？

**选项：**
- A. 单 arm64：1 个 cargo target，零 lipo，build.sh 改动最小
- B. arm64 + x86_64 universal：2 个 cargo target + lipo，复杂度翻倍

**决定：** A。开发机 macOS 主流是 Apple silicon；x86_64 host 需求出现时再补。**前置假设**：CI 跑 Apple silicon runner。如未来 CI 出现 x86_64 mac 需求，新建 spec 补一个 cargo target 即可，本 spec 不预先做。

**Trade-off：** x86_64 macOS host 暂时跑不了 `swift test`，只能跑 `xcodebuild test -destination 'platform=iOS Simulator'`（已经能跑，因为 sim slice 双 arch）。

### D2: 用 macOS host swift test 而非 iOS sim xcodebuild test

**问题：** iOS facade 该在 iOS sim 上测，还是 macOS host 上测？

**选项：**
- A. macOS host `swift test`：快，零 simulator 启动开销，但跑在 macos slice 上
- B. iOS sim `xcodebuild test`：跑在 ios-arm64-sim slice 上，与生产 iOS 设备同 ABI；但需要起 simulator

**决定：** A 作为日常 dev / 首版 CI 入口；B 推迟到 `add-ios-ci` spec。

**理由：** Facade 是 Swift → C ABI 薄胶水，跨 macos / ios sim / ios device 二进制差异限于 link 段；macos slice 通过则 ios slice 也通过的可能性极高。Tier 1 host_tests 已经在 macOS 上跑了 22 个，覆盖 C ABI 自身；本 spec 测的 R1–R7 都是 Swift 包装层。**用 macOS slice 测够了**。

### D3: 测试 fixture 放 Tests/Z42VMTests/Resources/，不复用 Resources/

**问题：** test-fixtures 放哪？
- A. `src/toolchain/host/platforms/ios/Resources/test-fixtures/` — 与 stdlib 同级，主产物 xcframework 也会带这俩 .zbc
- B. `src/toolchain/host/platforms/ios/Tests/Z42VMTests/Resources/test-fixtures/` — 仅 test target 看见，主 xcframework 不含

**决定：** **B**。test-fixtures 是测试代码的依赖，不该污染 Z42VM.xcframework 让产线 app 跟着拉一份没用的 hello.zbc / multi_line.zbc。Package.swift testTarget 用 `.process("Resources")` 把整个 Resources 子树进 `Bundle.module`。

**Implementation**：build.sh 把 fixture 产物写到 `Tests/Z42VMTests/Resources/test-fixtures/*.zbc`（而非 `Resources/test-fixtures/`）。

### D4: stdlib zpkg 双份持有 — `Resources/stdlib/` 给 xcframework，`Tests/Z42VMTests/Resources/stdlib/` 给 test bundle

**问题：** `BundleZpkgResolver(bundle: .module, subdirectory: "stdlib")` 在 test 里要从 `Bundle.module` 找 stdlib zpkg。`Bundle.module` 是 SwiftPM 给 test target 用的资源 bundle，不会含主 Z42VM target 的 Resources。

**决定：** build.sh 把 stdlib 拷两份：一份到 `Resources/stdlib/`（主产物，xcframework 用），一份到 `Tests/Z42VMTests/Resources/stdlib/`（test target 用）。两份内容相同，是同一 source-of-truth (`artifacts/build/libs/release/`) 的 copy。

**理由：** Swift Package 资源 bundle 隔离机制不允许 testTarget 引用别 target 的 resources（除非显式 copy）。双份 ~600KB（6 个 zpkg），可接受。

## Implementation Notes

### Package.swift 改动 sketch

```swift
let package = Package(
    name: "Z42VM",
    platforms: [
        .iOS(.v14),
        .macOS(.v13),
    ],
    products: [
        .library(name: "Z42VM", targets: ["Z42VM"]),
    ],
    targets: [
        .target(name: "Z42VM", dependencies: ["Z42VMC"], path: "Sources/Z42VM"),
        .target(name: "Z42VMC", path: "Sources/Z42VMC", sources: ["dummy.c"], publicHeadersPath: "include"),
        .testTarget(                                            // 新
            name: "Z42VMTests",
            dependencies: ["Z42VM"],
            path: "Tests/Z42VMTests",
            resources: [.process("Resources")],
        ),
    ]
)
```

### build.sh 改动 sketch（新增段）

```bash
# (3 续) Build macOS slice
for t in aarch64-apple-darwin; do                    # D1: 单 arch
    echo "cargo build --release --target $t"
    cargo build --release --manifest-path "$RUST_MANIFEST" --target "$t"
done
MAC_LIB="$RUST_TARGET/aarch64-apple-darwin/release/$LIB_NAME"

# (5 续) -library MAC_LIB
xcodebuild -create-xcframework \
    -library "$DEV_LIB" \
    -library "$SIM_UNIVERSAL" \
    -library "$MAC_LIB" \
    -output "$XCF"

# (6 新) Compile test fixtures
Z42C="$ROOT/artifacts/build/compiler/z42.Driver/bin/z42c.dll"
TEST_FIX="$HERE/Tests/Z42VMTests/Resources/test-fixtures"
mkdir -p "$TEST_FIX"
for src in hello multi_line; do
    dotnet "$Z42C" "$ROOT/examples/embedding/$src.z42" -o "$TEST_FIX/$src.zbc"
done

# (7 新) stdlib 双份
TEST_STDLIB="$HERE/Tests/Z42VMTests/Resources/stdlib"
mkdir -p "$TEST_STDLIB"
cp "$LIBS_DIR"/*.zpkg "$TEST_STDLIB/" 2>/dev/null || true
```

### Z42VMTests.swift 结构 sketch

```swift
import XCTest
@testable import Z42VM

final class Z42VMTests: XCTestCase {
    // ─ Fixtures ─
    private func helloZbc() throws -> Data { /* Bundle.module load */ }
    private func multiLineZbc() throws -> Data { /* ... */ }
    private func bundleResolver() -> BundleZpkgResolver { .init(bundle: .module, subdirectory: "stdlib") }

    // ─ R1 ─
    func testSmokeHelloWorld() throws { /* load + invoke + assert sink == "hello, world\n" */ }
    // ─ R2 ─
    func testBadZbcThrowsBadZbc() throws { /* ... */ }
    // ... R3 ... R7
}
```

## Testing Strategy

- 单元测试：本 spec **就是**单元测试的实现
- 验证：`swift test` 在 macOS arm64 host 上跑通；`./build.sh` 产物存在
- Spec coverage：每个 XCTest 方法都覆盖 contract spec 的对应 Requirement R#

## Risk

- **build.sh ROOT 路径**：build.sh 用 `$ROOT="$HERE/../../../../.."`（5 层），需要确认它指向 repo root；编 fixture 时 `$Z42C` 路径需要 host stdlib + compiler 已编（前置 `dotnet build src/compiler/z42.slnx + scripts/build-stdlib.sh`）
- **macOS slice cross-compile 依赖**：本机已 `rustup target add aarch64-apple-darwin`？多数 Apple silicon 默认就装了。build.sh tooling check 段需要加这个 target
- **`Bundle.module` 生成时机**：SwiftPM 给 testTarget 自动产 `Bundle.module`，前提是 `resources:` 数组非空。需要保证 build.sh 产 fixtures 后再跑 swift test
