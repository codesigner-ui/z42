# iOS facade — build & run

> 🟢 已落地 · facade [`platforms/ios/`](../../../src/toolchain/host/platforms/ios/) · spec [`2026-05-12-add-platform-ios/`](../../spec/archive/2026-05-12-add-platform-ios/)

把 z42 VM 编进 `Z42VM.xcframework`，让 Swift / SwiftUI app `import Z42VM` 跑 `.zbc`。iOS app 只能在 **macOS 主机**上编。**从零开始按下面 4 步走**。

## Step 1 — Install toolchain（一次性）

```bash
# Xcode（一次性大下载；如已装跳过）
# 从 App Store 装最新 Xcode，启动一次接受 license
sudo xcodebuild -license accept
xcode-select --install
xcode-select -p     # 应输出 .../Xcode.app/Contents/Developer

# Rust iOS targets
rustup target add aarch64-apple-ios aarch64-apple-ios-sim x86_64-apple-ios
```

❗ `xcrun: command not found` → Xcode 未装或 `xcode-select -s /Applications/Xcode.app/Contents/Developer` 没指对。

## Step 2 — Build compiler + stdlib（一次性 / 改 stdlib 后重跑）

```bash
dotnet build src/compiler/z42.slnx
z42 xtask.zpkg build stdlib
```

✅ 产出 `artifacts/build/compiler/z42.Driver/bin/z42c.dll`。stdlib zpkg 由 `z42 xtask.zpkg build stdlib` 产到 `artifacts/build/libraries/dist/release/*.zpkg`。

❗ `dotnet: command not found` → 装 .NET 10+：https://dotnet.microsoft.com/download

## Step 3 — Build the iOS facade

```bash
cd src/toolchain/host/platforms/ios
./build.sh
```

`build.sh` 内部串接：cargo build × 3 target + `lipo -create` 合并 simulator + `xcodebuild -create-xcframework`。

✅ 产物：`Z42VM.xcframework/` 含 `ios-arm64/` + `ios-arm64_x86_64-simulator/`；`Resources/stdlib/*.zpkg`（22 个，从 `artifacts/build/libraries/dist/release/` 拷入）。

❗ `linker not found for aarch64-apple-ios` → Step 1 rustup target 漏装。
❗ `libffi-sys` cross-compile 失败 → 检查 `runtime/Cargo.toml` 的 `libffi` 是否为 5.1+（旧 3.2/libffi-sys 2.3 的 bundled `sysv.S` 在 iOS arm64 触发 CFI advance_loc 错误）；当前默认已是 bundled 模式，无需手工切换。

## Step 4 — Consume in your Xcode project

`Package.swift`：

```swift
.package(path: "/abs/path/to/z42/src/toolchain/host/platforms/ios"),
// 或 release：
// .package(url: "https://github.com/codesigner-ui/z42-ios.git", from: "0.1.0"),
```

Target deps：

```swift
.product(name: "Z42VM", package: "Z42VM"),
```

Swift 用法：

```swift
import Z42VM

let vm = try Z42VM(zpkgResolver: BundleZpkgResolver())
vm.stdoutHandler = { bytes in textArea.append(String(decoding: bytes, as: UTF8.self)) }

let m = try vm.loadZbc(Data(contentsOf: zbcURL))
let e = try vm.resolveEntry(m, fqn: "App.Main")
_ = try vm.invoke(e)
```

❗ `dyld: Library not loaded` → xcframework slice 选错（真机用了 simulator slice 或反之）。

---

**See also**

- **本地打 per-slice SDK package**（自包含 `Package.swift` + `Z42VM.xcframework`）：[`../packaging.md`](../packaging.md) — `z42 xtask.zpkg package release --rid ios-arm64 / iossim-arm64`
- Swift API + 错误码（spec 落地后补）：`platforms/ios/README.md`
- 跨平台契约：[`platforms/README.md`](../../../src/toolchain/host/platforms/README.md)
- 设计 + 决策：[spec](../../spec/archive/2026-05-12-add-platform-ios/)
- Demo / XCTest / CI 推迟到独立 spec（`add-platform-ios-demo` / `-tests` / `-ci`）。
