# hello_c — z42 C 嵌入示例（全平台）

z42 SDK 的 Tier 1 C ABI 最小用例。`main.c` 在**所有平台 byte-identical**（SHA-256 校）——
这正是 z42 跨平台 C ABI 契约的设计点：**同一份 C 代码，链不同 target 的 lib，就能跑在
不同平台**。本 README 给每个平台的编译 + 链接命令；本 package 实际是哪个平台，打开
`manifest.toml` 看 `[package].rid` 字段确认。

> 路径约定：以下命令假设你在 SDK package 根目录下的 `examples/hello_c/` 里，
> headers 在 `../../native/include`，静态库在 `../../native/`，stdlib zpkgs 在 `../../libs/`。

## 工作原理

`main.c` 调 Tier 1 C ABI (`native/include/z42_host.h`)：

1. `z42_host_initialize` — 起 VM
2. `z42_host_load_zbc` — 装 `hello.zbc`（编自 `examples/embedding/hello.z42`）
3. `z42_host_resolve_entry` — 找 `"Hello.Main"`
4. `z42_host_invoke` — 跑
5. `stdout_sink` 回调 — 把 `"hello, world\n"` 加上 `[host] ` 前缀打到 stdout
6. `z42_host_shutdown` — 拆

`libs/` 含 stdlib zpkgs（`z42.io.zpkg` 等）+ `index.json` namespace 映射；`main.c` 通过
`search_paths` 让 VM 自动扫文件系统加载（移动 / wasm 用 `zpkg_resolver` 回调注入）。

---

## host / desktop（macOS / Linux / Windows）

### macOS / Linux

```sh
cd examples/hello_c

cc -O2 -I ../../native/include \
   -o hello_c main.c \
   -L ../../native -lz42 \
   $(case "$(uname -s)" in \
       Darwin) echo "-liconv -lSystem -lc -lm" ;; \
       Linux)  echo "-lc -lm -lpthread -ldl -lrt -lgcc_s" ;; \
     esac)

./hello_c hello.zbc ../../libs/
# 期望: [host] hello, world
```

### Windows (MSVC)

```cmd
cd examples\hello_c
cl /I..\..\native\include main.c /link ..\..\native\z42.lib /OUT:hello_c.exe
hello_c.exe hello.zbc ..\..\libs\
```

---

## browser-wasm

| RID | Cargo target | 用途 |
|-----|--------------|------|
| `browser-wasm` | `wasm32-unknown-unknown` | 浏览器（V8 / SpiderMonkey / JSC）+ Node.js（V8）|

注：WASI（`wasm32-wasi`）当前**不在** Phase 1 支持范围。

### JS 路径（推荐）

最便利的消费路径是 wasm-bindgen 双 target —— `package.json` 已写好；
`npm install ./z42-<version>-browser-wasm-release` 之后：

```js
// Node.js
import { Z42VM } from "@z42/wasm/node";
// Browser
import { Z42VM } from "@z42/wasm/web";

const vm = await Z42VM.initialize();
await vm.loadZbc(await fetch("hello.zbc").then(r => r.arrayBuffer()));
vm.invoke("Hello.Main");
```

详见 `js/index.d.ts` 和 `pkg-web/` / `pkg-nodejs/` 的 README。在 wasm 上 stdout 是
host JS 通过 `z42_host_set_stdout` 注的回调；写 `console.log` 即可。

### C 路径（wasm-ld 链接）

不走 wasm-bindgen 时，用 `wasm-ld` 直接把 `main.c` + `libz42.a` 链成独立 `.wasm`
模块。需要 LLVM/clang 的 wasm32 后端（clang ≥ 14）：

```sh
cd examples/hello_c

# 1) 编 main.c → main.wasm.o
clang --target=wasm32-unknown-unknown \
    -I ../../native/include \
    -nostdlib -fvisibility=hidden -O2 \
    -c main.c -o main.wasm.o

# 2) wasm-ld 链 main.wasm.o + libz42.a → app.wasm
wasm-ld \
    --no-entry --export-dynamic --allow-undefined \
    main.wasm.o ../../native/libz42.a \
    -o app.wasm
```

- `--no-entry`：wasm 模块没有 main()，由 host (JS) 调入口
- `--allow-undefined`：libz42 引用 host 提供的 imports（z42_host imports）
- `--export-dynamic`：让 host 能 `instance.exports.<fn>` 访问入口
- C 标准库：wasm32-unknown-unknown 不带 libc；`puts` / `malloc` 等需自备 stub 或链 wasi-libc

⚠️ 当前还没有 `z42-aotcross-wasm` 工具，所以 **hello.zbc 不能直接 link 进 app.wasm**
（须通过 host JS 用 `loadZbc(bytes)` 喂进去）。等 aotcross-wasm 出来会更新本节。

---

## iOS

| RID | 用途 | Cargo target | SDK |
|-----|------|--------------|-----|
| `ios-arm64`     | iPhone / iPad / Apple TV / Vision Pro 实机 | `aarch64-apple-ios`     | `iphoneos` |
| `iossim-arm64` | Apple silicon Mac 上 iOS 模拟器             | `aarch64-apple-ios-sim` | `iphonesimulator` |

### SwiftPM（推荐）

`Package.swift` 已写好，在你的应用 `Package.swift` 里：

```swift
let package = Package(
    name: "MyApp",
    targets: [
        .executableTarget(name: "MyApp", dependencies: [
            .product(name: "Z42VM", package: "Z42VM"),
        ]),
    ],
    dependencies: [
        .package(path: "/path/to/this/z42-<version>-ios-<slice>-release"),
    ]
)
```

Swift / Objective-C 用户用 `Z42VM` 模块（见 `Sources/Z42VM/Z42VM.swift`）。

### 手工 cc 链 raw C

```sh
cd examples/hello_c

# iOS device (ios-arm64)
xcrun -sdk iphoneos clang \
    -arch arm64 -mios-version-min=14.0 \
    -I ../../native/include \
    -o hello_c main.c ../../native/libz42.a

# iOS simulator on Apple silicon (iossim-arm64)
xcrun -sdk iphonesimulator clang \
    -arch arm64 -mios-simulator-version-min=14.0 \
    -I ../../native/include \
    -o hello_c main.c ../../native/libz42.a
```

注意：iOS app 通常不在终端直接跑 `hello_c` binary —— 需打包成 .app 或用 Xcode
启 simulator。本示例主要验证 C ABI 链接路径正确。

---

## Android

| RID | Android ABI | Cargo target | NDK 工具链前缀 |
|-----|-------------|--------------|----------------|
| `android-arm64` | `arm64-v8a`   | `aarch64-linux-android`   | `aarch64-linux-android<api>-` |
| `android-armv7` | `armeabi-v7a` | `armv7-linux-androideabi` | `armv7a-linux-androideabi<api>-` |
| `android-x64`   | `x86_64`      | `x86_64-linux-android`    | `x86_64-linux-android<api>-` |
| `android-x86`   | `x86`         | `i686-linux-android`      | `i686-linux-android<api>-` |

### Gradle externalNativeBuild + CMake（推荐）

`cpp/CMakeLists.txt` 已写好；拷进 Android Studio 工程的 `src/main/cpp/`，Kotlin 端用
`kotlin/io/z42/vm/Z42VM.kt`（`System.loadLibrary("z42_platform_android")` + JNI bridge 自动完成）：

```kotlin
val vm = io.z42.vm.Z42VM.instance
vm.loadZbc(assets.open("hello.zbc").readBytes())
vm.invoke("Hello.Main")
```

`build.gradle.kts`：

```kotlin
android {
    defaultConfig {
        externalNativeBuild { cmake { cppFlags += listOf("-std=c11") } }
        ndk { abiFilters += listOf("arm64-v8a") } // 你这个 SDK 包对应的 ABI
    }
    externalNativeBuild {
        cmake { path = file("src/main/cpp/CMakeLists.txt"); version = "3.22.1" }
    }
}
```

Kotlin 端把 `kotlin/io/z42/vm/*.kt` 拷进 `src/main/java/io/z42/vm/`，`.so` 拷进
`src/main/jniLibs/<abi>/`，build 即可。

### 手工 NDK clang 链 raw C

```sh
cd examples/hello_c

API=23                  # z42 manifest.toml [compat].android-min-sdk
NDK=$ANDROID_NDK_HOME
# $HOST ∈ darwin-x86_64 / linux-x86_64（按 NDK host 而定）

# android-arm64 (arm64-v8a)
$NDK/toolchains/llvm/prebuilt/$HOST/bin/aarch64-linux-android$API-clang \
    -I ../../native/include -o hello_c main.c \
    ../../native/libz42_platform_android.a -ldl -llog
# 其它 ABI 换 toolchain 前缀（armv7a-linux-androideabi / x86_64-linux-android / i686-linux-android）
```

也可链动态库 `libz42_platform_android.so` 代替 `.a`，自己 push 到 device 上。
注意：Android binary 通常不在终端直接跑 —— 需 `adb push` 到 `/data/local/tmp/` 再执行，
或打包进 .apk。本示例主要验证 C ABI 链接路径正确。

---

## 跨平台对比

`main.c` 跨平台 byte-identical；只是 link 方式不同：

| 平台 | link 工具 | 静态库 |
|------|-----------|--------|
| desktop | `cc -lz42` | `libz42.a` / `libz42.dylib` |
| iOS | `xcrun -sdk` clang | `libz42.a`（slice）|
| Android | NDK clang | `libz42_platform_android.{a,so}` |
| wasm | `wasm-ld` | `libz42.a`（wasm32 object archive）|

## 与 hello_rust 对照

[`../hello_rust/`](../hello_rust/) 是同一 hello-world 流程的 Tier 2 Rust 版本，宿主用 Rust
写时更 ergonomic。stdout 一致。
