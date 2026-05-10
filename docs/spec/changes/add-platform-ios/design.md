# Design: iOS Platform Scaffold

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│           iOS App (Swift / SwiftUI)                          │
│                                                              │
│   import Z42Runtime                                          │
│   let vm = try Z42Vm()                                       │
│   vm.setStdoutHandler { line in textArea.append(line) }     │
│   try vm.loadZbc(data)                                       │
│   try vm.run(entryPoint: "main")                             │
└─────────────────────────┬────────────────────────────────────┘
                          │ Swift → C ABI
                          ▼
┌──────────────────────────────────────────────────────────────┐
│      Z42Runtime SwiftPM Package                              │
│                                                              │
│  Sources/Z42Runtime/Z42Vm.swift   — Swift facade             │
│  Sources/Z42RuntimeC/             — C bridge module          │
│   ├─ include/z42_ios.h            — C ABI 头                  │
│   ├─ include/module.modulemap     — clang module             │
│   └─ z42_ios.c                    — thin C glue              │
│                                                              │
│  z42_runtime.xcframework          — 多 target 二进制          │
│   ├─ ios-arm64/                                              │
│   ├─ ios-arm64_x86_64-simulator/                             │
└─────────────────────────┬────────────────────────────────────┘
                          │ static link
                          ▼
┌──────────────────────────────────────────────────────────────┐
│  z42-ios crate (Rust, cdylib + staticlib)                    │
│                                                              │
│  src/lib.rs  #[no_mangle] pub extern "C" fn z42_*            │
│              wraps z42_runtime::interp::Interpreter          │
└─────────────────────────┬────────────────────────────────────┘
                          │ depends on
                          ▼
┌──────────────────────────────────────────────────────────────┐
│  src/runtime/  (--features ios = interp-only + aot)          │
└──────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: 包格式 ——「SwiftPM + xcframework」

**问题**：iOS 集成方式选哪种？

**选项**：A. SwiftPM + xcframework（官方主推）；B. CocoaPods；C. Carthage

**决定**：选 **A**。理由：
- SwiftPM 与 Xcode 14+ 深度集成
- xcframework 支持多 SDK / 多 architecture 单包
- 无 Ruby / Gemfile 依赖

### Decision 2: C ABI 头文件（锁定）

[platform/ios/Sources/Z42RuntimeC/include/z42_ios.h](platform/ios/Sources/Z42RuntimeC/include/z42_ios.h)：

```c
#ifndef Z42_IOS_H
#define Z42_IOS_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct Z42Vm Z42Vm;

// 创建 / 销毁
Z42Vm* z42_vm_new(void);
void z42_vm_free(Z42Vm* vm);

// 加载
int32_t z42_vm_load_zbc(Z42Vm* vm, const uint8_t* data, size_t len, char** out_err);
int32_t z42_vm_load_zpkg(Z42Vm* vm, const uint8_t* data, size_t len, char** out_err);

// 运行
int32_t z42_vm_run(Z42Vm* vm, const char* entry_point, char** out_err);

// stdout/stderr 回调（v0.1 接口；实际可能 v0.1 fallback 到 NSLog）
typedef void (*z42_output_callback_t)(const char* line, size_t len, void* ctx);
void z42_vm_set_stdout_handler(Z42Vm* vm, z42_output_callback_t cb, void* ctx);
void z42_vm_set_stderr_handler(Z42Vm* vm, z42_output_callback_t cb, void* ctx);

// 错误字符串释放（caller 负责释放 out_err）
void z42_string_free(char* s);

#ifdef __cplusplus
}
#endif

#endif // Z42_IOS_H
```

[platform/ios/Sources/Z42RuntimeC/include/module.modulemap](platform/ios/Sources/Z42RuntimeC/include/module.modulemap)：

```
module Z42RuntimeC {
    header "z42_ios.h"
    export *
}
```

### Decision 3: Rust crate（锁定）

[platform/ios/rust/Cargo.toml](platform/ios/rust/Cargo.toml)：

```toml
[package]
name = "z42-ios"
version = "0.1.0"
edition = "2021"

[lib]
name = "z42_ios"
crate-type = ["cdylib", "staticlib"]

[dependencies]
z42-runtime = { path = "../../../src/runtime", default-features = false, features = ["ios"] }
```

[platform/ios/rust/src/lib.rs](platform/ios/rust/src/lib.rs)：

```rust
use std::ffi::{c_char, c_void, CStr, CString};
use std::ptr;

use z42_runtime::interp::Interpreter;

pub struct Z42Vm {
    interp: Interpreter,
    stdout_cb: Option<(extern "C" fn(*const c_char, usize, *mut c_void), *mut c_void)>,
    stderr_cb: Option<(extern "C" fn(*const c_char, usize, *mut c_void), *mut c_void)>,
}

#[no_mangle]
pub extern "C" fn z42_vm_new() -> *mut Z42Vm {
    let vm = Box::new(Z42Vm {
        interp: Interpreter::new(),
        stdout_cb: None,
        stderr_cb: None,
    });
    Box::into_raw(vm)
}

#[no_mangle]
pub extern "C" fn z42_vm_free(vm: *mut Z42Vm) {
    if !vm.is_null() {
        unsafe { drop(Box::from_raw(vm)); }
    }
}

#[no_mangle]
pub extern "C" fn z42_vm_load_zbc(
    vm: *mut Z42Vm, data: *const u8, len: usize, out_err: *mut *mut c_char,
) -> i32 {
    let vm = unsafe { &mut *vm };
    let bytes = unsafe { std::slice::from_raw_parts(data, len) };
    match vm.interp.load_zbc(bytes) {
        Ok(_) => 0,
        Err(e) => {
            unsafe { *out_err = CString::new(e.to_string()).unwrap().into_raw(); }
            -1
        }
    }
}

#[no_mangle]
pub extern "C" fn z42_vm_run(
    vm: *mut Z42Vm, entry: *const c_char, out_err: *mut *mut c_char,
) -> i32 {
    let vm = unsafe { &mut *vm };
    let entry_str = unsafe { CStr::from_ptr(entry).to_string_lossy().into_owned() };
    match vm.interp.call(&entry_str, &[]) {
        Ok(_) => 0,
        Err(e) => {
            unsafe { *out_err = CString::new(e.to_string()).unwrap().into_raw(); }
            -1
        }
    }
}

#[no_mangle]
pub extern "C" fn z42_string_free(s: *mut c_char) {
    if !s.is_null() {
        unsafe { drop(CString::from_raw(s)); }
    }
}

// ... set_stdout_handler 等省略
```

### Decision 4: Swift facade（锁定）

[platform/ios/Sources/Z42Runtime/Z42Vm.swift](platform/ios/Sources/Z42Runtime/Z42Vm.swift)：

```swift
import Foundation
import Z42RuntimeC

public final class Z42Vm {
    private var handle: OpaquePointer?
    private var stdoutHandler: ((String) -> Void)?
    private var stderrHandler: ((String) -> Void)?

    public init() throws {
        guard let h = z42_vm_new() else {
            throw Z42Error.initializationFailed
        }
        self.handle = OpaquePointer(h)
    }

    deinit {
        if let h = handle { z42_vm_free(UnsafeMutablePointer(h)) }
    }

    public func loadZbc(_ data: Data) throws {
        try data.withUnsafeBytes { ptr in
            var err: UnsafeMutablePointer<CChar>?
            let rc = z42_vm_load_zbc(
                UnsafeMutablePointer(handle!),
                ptr.baseAddress!.assumingMemoryBound(to: UInt8.self),
                data.count,
                &err
            )
            if rc != 0 {
                let msg = err.map { String(cString: $0) } ?? "unknown"
                if let err = err { z42_string_free(err) }
                throw Z42Error.loadFailed(msg)
            }
        }
    }

    public func loadZpkg(_ data: Data) throws { /* 类似 */ }

    public func setStdoutHandler(_ handler: @escaping (String) -> Void) {
        stdoutHandler = handler
    }

    public func setStderrHandler(_ handler: @escaping (String) -> Void) {
        stderrHandler = handler
    }

    public func run(entryPoint: String, args: [String] = []) throws {
        var err: UnsafeMutablePointer<CChar>?
        let rc = entryPoint.withCString { cstr in
            z42_vm_run(UnsafeMutablePointer(handle!), cstr, &err)
        }
        if rc != 0 {
            let msg = err.map { String(cString: $0) } ?? "unknown"
            if let err = err { z42_string_free(err) }
            throw Z42Error.runFailed(msg)
        }
    }
}
```

[platform/ios/Sources/Z42Runtime/Z42Error.swift](platform/ios/Sources/Z42Runtime/Z42Error.swift)：

```swift
public enum Z42Error: Error, CustomStringConvertible {
    case initializationFailed
    case loadFailed(String)
    case runFailed(String)

    public var description: String {
        switch self {
        case .initializationFailed: return "Z42 VM initialization failed"
        case .loadFailed(let msg): return "Z42 load failed: \(msg)"
        case .runFailed(let msg): return "Z42 run failed: \(msg)"
        }
    }
}
```

### Decision 5: Package.swift（锁定）

[platform/ios/Package.swift](platform/ios/Package.swift)：

```swift
// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "Z42Runtime",
    platforms: [.iOS(.v14)],
    products: [
        .library(name: "Z42Runtime", targets: ["Z42Runtime"]),
    ],
    targets: [
        .target(
            name: "Z42Runtime",
            dependencies: ["Z42RuntimeC"]
        ),
        .target(
            name: "Z42RuntimeC",
            dependencies: ["z42_runtime"],
            publicHeadersPath: "include"
        ),
        .binaryTarget(
            name: "z42_runtime",
            path: "Sources/Z42RuntimeC/z42_runtime.xcframework"
        ),
        .testTarget(
            name: "Z42RuntimeTests",
            dependencies: ["Z42Runtime"],
            resources: [.process("Resources")]
        ),
    ]
)
```

### Decision 6: build.sh 接口

[platform/ios/build.sh](platform/ios/build.sh)：

```bash
#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

mode=${1:-release}
profile_flag=""
out_dir="debug"
[[ "$mode" == "release" ]] && profile_flag="--release" && out_dir="release"

# 1. cargo build × 3 target
TARGETS=(
    "aarch64-apple-ios"               # device
    "aarch64-apple-ios-sim"           # arm64 simulator (M系列)
    "x86_64-apple-ios"                # x86_64 simulator (Intel mac)
)

for target in "${TARGETS[@]}"; do
    rustup target add "$target" 2>/dev/null || true
    cargo build $profile_flag --target "$target" --manifest-path rust/Cargo.toml
done

# 2. lipo 合并 simulator (arm64 + x86_64) 为 fat binary
mkdir -p build/sim-fat
lipo -create \
    rust/target/aarch64-apple-ios-sim/$out_dir/libz42_ios.a \
    rust/target/x86_64-apple-ios/$out_dir/libz42_ios.a \
    -output build/sim-fat/libz42_ios.a

# 3. xcodebuild create-xcframework
rm -rf Sources/Z42RuntimeC/z42_runtime.xcframework
xcodebuild -create-xcframework \
    -library rust/target/aarch64-apple-ios/$out_dir/libz42_ios.a \
        -headers Sources/Z42RuntimeC/include \
    -library build/sim-fat/libz42_ios.a \
        -headers Sources/Z42RuntimeC/include \
    -output Sources/Z42RuntimeC/z42_runtime.xcframework

echo "✅ xcframework built: Sources/Z42RuntimeC/z42_runtime.xcframework"
```

### Decision 7: XCTest

[platform/ios/Tests/Z42RuntimeTests/Z42VmTests.swift](platform/ios/Tests/Z42RuntimeTests/Z42VmTests.swift)：

```swift
import XCTest
@testable import Z42Runtime

final class Z42VmTests: XCTestCase {
    func testHelloWorldRuns() throws {
        let url = Bundle.module.url(forResource: "01_hello", withExtension: "zbc")!
        let data = try Data(contentsOf: url)

        let vm = try Z42Vm()
        var captured = ""
        vm.setStdoutHandler { line in captured += line + "\n" }

        try vm.loadZbc(data)
        try vm.run(entryPoint: "main")

        XCTAssertEqual(captured, "Hello, World!\n")
    }
}
```

### Decision 8: iOSDemo Xcode 项目

[platform/ios/iOSDemo/iOSDemo/iOSDemoApp.swift](platform/ios/iOSDemo/iOSDemo/iOSDemoApp.swift)：

```swift
import SwiftUI

@main
struct iOSDemoApp: App {
    var body: some Scene {
        WindowGroup { ContentView() }
    }
}
```

[platform/ios/iOSDemo/iOSDemo/ContentView.swift](platform/ios/iOSDemo/iOSDemo/ContentView.swift)：

```swift
import SwiftUI
import Z42Runtime

struct ContentView: View {
    @State private var output: String = "Loading..."

    var body: some View {
        ScrollView {
            Text(output)
                .font(.system(.body, design: .monospaced))
                .padding()
        }
        .onAppear { runHello() }
    }

    private func runHello() {
        guard let url = Bundle.main.url(forResource: "01_hello", withExtension: "zbc"),
              let data = try? Data(contentsOf: url) else {
            output = "Error: 01_hello.zbc not found"; return
        }
        do {
            let vm = try Z42Vm()
            var captured = ""
            vm.setStdoutHandler { line in captured += line + "\n" }
            try vm.loadZbc(data)
            try vm.run(entryPoint: "main")
            output = captured
        } catch {
            output = "Error: \(error)"
        }
    }
}
```

### Decision 9: justfile 接入

[justfile](justfile) 在 P4.3 基础上扩展：

```just
platform name action *args:
    #!/usr/bin/env bash
    case "{{name}}" in
        wasm)    just platform-wasm-{{action}} {{args}} ;;
        android) just platform-android-{{action}} {{args}} ;;
        ios)     just platform-ios-{{action}} {{args}} ;;
        *) echo "未知平台: {{name}}" && exit 1 ;;
    esac

platform-ios-build:
    ./platform/ios/build.sh release

platform-ios-build-debug:
    ./platform/ios/build.sh debug

platform-ios-test:
    just platform-ios-build-debug
    cd platform/ios && xcodebuild test \
        -scheme Z42Runtime \
        -destination 'platform=iOS Simulator,name=iPhone 15,OS=latest'

platform-ios-demo-run:
    cd platform/ios/iOSDemo && xcodebuild build \
        -scheme iOSDemo \
        -destination 'platform=iOS Simulator,name=iPhone 15,OS=latest'
```

### Decision 10: CI 接入

[.github/workflows/ci.yml](.github/workflows/ci.yml) 加 job：

```yaml
platform-ios:
  runs-on: macos-14   # M1
  steps:
    - uses: actions/checkout@v4
    - uses: dtolnay/rust-toolchain@stable
      with:
        targets: aarch64-apple-ios,aarch64-apple-ios-sim,x86_64-apple-ios
    - name: Build xcframework
      run: just platform ios build
    - name: Test on simulator
      run: just platform ios test
```

## Implementation Notes

### .a 与 .dylib 区别

- iOS App Store 不允许 .dylib（dynamic library）；只能 .framework / .a
- 本 spec 用 staticlib (.a) + xcframework
- cdylib 仍保留（开发调试可能用）

### stdout/stderr 桥接（v0.1 简化）

类似 P4.2 / P4.3，初版 setStdoutHandler API 保留但内部 fallback 到 NSLog；完整桥接 v0.2。

### Cargo workspace

[src/runtime/Cargo.toml](src/runtime/Cargo.toml) `[workspace] members` 加 `../platform/ios/rust`（注意路径深度）。

### z42 examples → demo 资源

build.sh 加可选预处理：

```bash
# 可选：编译示例
if command -v dotnet &>/dev/null; then
    dotnet run --project ../../src/compiler/z42.Driver -- compile \
        ../../examples/01_hello.z42 -o iOSDemo/iOSDemo/Resources/01_hello.zbc
    cp iOSDemo/iOSDemo/Resources/01_hello.zbc Tests/Z42RuntimeTests/Resources/
fi
```

### simulator x86_64 在 M1 上不可用？

GitHub Actions macos-14 是 M1 ARM；arm64-sim 即可。x86_64-sim 仅为 Intel mac 开发者本地用。CI 只测 arm64-sim。

### Xcode 版本

要求 Xcode 15+（含 Swift 5.9）。CI macos-14 默认 Xcode 15。

## Testing Strategy

- ✅ `cargo build --target aarch64-apple-ios -p z42-ios` 通过
- ✅ `cargo build --target aarch64-apple-ios-sim -p z42-ios` 通过
- ✅ `cargo build --target x86_64-apple-ios -p z42-ios` 通过
- ✅ `./platform/ios/build.sh release` 产出 xcframework
- ✅ xcframework 含 `ios-arm64/` 与 `ios-arm64_x86_64-simulator/` 两个 slice
- ✅ `xcodebuild test -scheme Z42Runtime -destination 'platform=iOS Simulator,name=iPhone 15'` Z42VmTests 通过
- ✅ iOSDemo 在 simulator 上启动显示 "Hello, World!"
- ✅ vm_core 5 个用例的 .zbc 嵌入 demo 后跑出与 desktop 一致输出
- ✅ xcframework 总大小 ≤ 15 MB
- ✅ CI platform-ios job 全绿
