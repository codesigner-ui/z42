# workflow/testing/platform-tests.md

本地从零跑 **wasm / iOS / Android** 平台测试（R1–R7 嵌入契约）。框架设计见
[`docs/design/testing/cross-platform-testing.md`](../../design/testing/cross-platform-testing.md)；
CI 接线见 [`../ci.md`](../ci.md)。

统一入口（接口驱动框架，add-platform-test-pipeline）：

```bash
z42 xtask.zpkg test platform <wasm|ios|android|all> [build|assets|run]
```

三阶段，省略 step = 全跑 `build → assets → run`：

| step | 做什么 |
|------|--------|
| `build` | ① 构建平台原生工程（wasm-pack / xcframework / AAR）|
| `assets` | ② 编 R1–R7 fixtures→`.zbc` + 收 stdlib zpkg 进平台 bundle（+wasm `files.json`）|
| `run` | ③ 跑测试（Playwright / `swift test` / emulator）|

---

## Step 0 — 基础工具链（一次性，所有平台共用）

平台测试依赖**已构建的 z42 工具链**（编译器 + VM + stdlib + xtask）。从零：

```bash
# 1. C# 编译器 + Rust VM（release）
dotnet build src/compiler/z42.slnx
cargo build --release --manifest-path src/runtime/Cargo.toml

# 2. stdlib dist + xtask.zpkg
#    有 z42 launcher 在 PATH：
z42 xtask.zpkg build stdlib
#    没有 launcher：见下方「不带 launcher 怎么跑」一节先把 stdlib + xtask 备好
```

> 基础 build 配方详见 [`../building/`](../building/)。

### 不带 launcher 怎么跑（`z42` 不在 PATH）

直接用构建出的 `z42vm` 执行 `xtask.zpkg`，并指好 stdlib：

```bash
vm="$PWD/artifacts/build/runtime/release/z42vm"
libs="$PWD/artifacts/build/libraries/dist/release"
run() { Z42_PORTABLE_VM="$vm" Z42_LIBS="$libs" "$vm" artifacts/xtask/xtask.zpkg -- "$@"; }

# 首次需先备 stdlib + xtask.zpkg（primer）：
Z42_LIBS="$libs" dotnet run --project src/compiler/z42.Driver -- build scripts/xtask.z42.toml --release
run build stdlib

# 之后即可：
run test platform wasm
```

下文命令凡写 `z42 xtask.zpkg <…>` 的，无 launcher 时等价于 `run <…>`。

---

## wasm（最易，无需模拟器）

**额外前置**：`wasm-pack` + wasm32 target + 本地 node（Playwright 用）。一次性：

```bash
z42 xtask.zpkg deps install --os wasm   # wasm-pack + wasm32-unknown-unknown
z42 xtask.zpkg deps install node        # 本地 Node LTS → artifacts/tools/node（原 install-node-local.sh）
```

跑：

```bash
z42 xtask.zpkg test platform wasm
# ① wasm-pack build web+nodejs → ② fixtures+stdlib+files.json
# → ③ npm install + playwright install chromium（首次下载 ~280MB）+ R1–R7
```

> 没装本地 node 也行：框架会回退到 PATH 上的 `node`（`actions/setup-node` 风格）。
> JUnit 输出：`artifacts/test-reports/wasm/junit.xml`。

---

## iOS（需 macOS + Xcode）

**额外前置**：Xcode（`xcodebuild`，自行安装）+ Rust iOS + host slice target。

```bash
z42 xtask.zpkg deps install --os ios    # aarch64-apple-ios{,-sim} + aarch64-apple-darwin
```

跑（本地默认 ③ = `swift test`，跑在 macOS host slice）：

```bash
z42 xtask.zpkg test platform ios
# ① cargo×targets + xcframework（含 ios-device/sim/macos slice）
# → ② fixtures+stdlib 进 Tests bundle → ③ swift test（R1–R7）
```

**真 iOS Simulator**（CI 用法，本地复刻）——`build`/`assets` 走 xtask，`run` 用 xcodebuild：

```bash
z42 xtask.zpkg test platform ios build
z42 xtask.zpkg test platform ios assets
cd src/toolchain/host/platforms/ios
xcodebuild test -scheme Z42VM \
  -destination 'platform=iOS Simulator,name=iPhone 16'
```

> iOS cargo build 自动带 `IPHONEOS_DEPLOYMENT_TARGET=platform.ios.min_ios`
> （否则 zlib-ng C 部署目标与 rlib 不匹配 → `___chkstk_darwin` 链接失败）。

---

## Android（需 NDK + emulator）

**额外前置**：`cargo-ndk` + NDK + android target + JDK 17 + emulator AVD。一次性：

```bash
z42 xtask.zpkg deps install --os android       # cargo-ndk + NDK + rust android targets
z42 xtask.zpkg deps install android-emulator   # emulator + system-image + AVD + Gradle（~4 GB / 10-15 min；原 install-android-toolchain-local.sh）
```

跑（③ 桥接 `platforms/android/test.sh`，自动起 emulator）：

```bash
eval "$(z42 xtask.zpkg deps install --os android --print-env)"   # 设 ANDROID_NDK_HOME / ANDROID_HOME 等
z42 xtask.zpkg test platform android
# ① cargo-ndk×ABIs + gradle AAR → ② fixtures+stdlib 进 assets
# → ③ test.sh：起 emulator + gradlew :z42vm:connectedAndroidTest（R1–R7）
```

---

## 三平台一把跑

```bash
z42 xtask.zpkg test platform all     # wasm → ios → android（首失败即停）
```

> 仅当本机三套工具链齐备时有意义；缺哪个平台先单独跑。

## 与主 GREEN gate 的关系

平台测试**不在** `z42 xtask.zpkg test`（host 6 stages）内——它们需各自的重型工具链，
按需单独跑。CI 各平台独立 job 跑（见 [`../ci.md`](../ci.md)），结果以 GitHub Check 呈现。
