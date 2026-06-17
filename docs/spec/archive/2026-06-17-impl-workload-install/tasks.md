# Tasks: impl-workload-install (B2) — 🟢 已完成（三平台本地 produce + install 端到端 ✅）

**变更说明：** runtime 与 workload tooling 分包（版本管理）+ `z42 workload install/list/remove`。
**锁：** `toolchain`（归档时释放）。设计/决策见 proposal.md + design.md。
**完成：** 2026-06-17。proposal Scope = "本次 local 产包 + 本地 install 验证"——iOS/wasm/android 三平台
全部 produce（runtime pack + workload tooling 分包）+ install（平台铺设：ios 改写 Package.swift /
wasm symlink / android jniLibs+assets，多 RID 增量）+ list/remove 端到端验证完成。下列「余下」均为
proposal 明确的 Out-of-Scope 后续 change，索引入 docs/roadmap.md Deferred Backlog Index。

## B2-1 iOS 端到端（✅ 完成 + 本地 swift build 验证）
- [x] 1.1 xtask_package_ios 拆分：`_buildRuntimePackageIos`（z42-runtime-<ver>-<rid>：xcframework+headers+libs）+ `_packageIos`（workload tooling：Sources+Package.swift+examples+manifest）
- [x] 1.2 Package.swift binaryTarget = `__Z42_RUNTIME_XCFRAMEWORK__` 占位符（install 解析）
- [x] 1.3 Z42VMC headers **自包含**（拷真实 runtime 头，非源码树转发桩——后者 `#include "../.."` 打包后失效）
- [x] 1.4 launcher_workload.z42：`workload install <wl> --from <tooling> [--runtime <pack>] [--version] [--rid]`（递归拷 tooling+runtime + 改写 Package.swift 为**相对路径**）/ `list` / `remove`
- [x] 1.5 launcher_cli.z42：workload router + dispatch（z42.launcher.z42.toml `*.z42` glob 自动纳入）
- [x] 1.6 GREEN：launcher+xtask 清编 exit 0；**端到端**：macos-slice runtime pack + tooling → `z42 workload install ios` → Package.swift 改写 `../../../macos-arm64/0.3.0/native/Z42VM.xcframework` → **`swift build` Build complete!** + `workload list` 列出 ios

## 关键实现发现
- **SwiftPM binaryTarget 路径必须相对 package root**（非绝对）→ install 写 `../../../<rid>/<ver>/native/Z42VM.xcframework`（布局固定）。
- **Z42VMC 转发头源码树耦合**（合并包当年也只在 repo 内 build）→ 包内放自包含真实头，SwiftPM 自动生成 module map。
- z42 stdlib **无 `Array.Sort`、无 `Directory.Copy`、`IndexOf` 无 startIndex 重载** → 自写递归拷贝、手动 quote 解析。

## B2-3 wasm（✅ 完成 + 本地 node 验证）
- [x] xtask_package_wasm 拆分：`_buildRuntimePackageWasm`（runtime pack：native + pkg-web + pkg-nodejs + libs）+ `_packageWasm`（tooling：js + package.json，**原样**）
- [x] launcher_workload install **wasm 分支**：runtime 有 `pkg-web/` → `File.SymLink` 把 pkg-web/pkg-nodejs 链进 tooling 根（package.json `exports` root-相对原样解析，免改写）
- [x] GREEN 端到端：`workload install wasm` → symlink 建立 → **`node import pkg-nodejs/z42_wasm.js` 加载成功**（exports: Z42VM/Z42VMEntry/...）
- install 平台分支：runtime 有 `native/Z42VM.xcframework` → ios(改写 Package.swift)；有 `pkg-web/` → wasm(symlink)

## B2-3 android（✅ 完成 + 本地 gradle AAR 端到端验证）
- [x] xtask_package_android 拆分：`_buildRuntimePackageAndroid`（runtime pack：per-ABI `libz42_platform_android.{so,a}` + `libz42_compression.{so,a}` + headers + libs）+ `_packageAndroid`（tooling = **gradle 工程**：gradlew + settings + z42vm 模块 Kotlin facade + JNI bridge）
- [x] JNI headers **自包含**：`z42vm/src/main/cpp/include/{z42_abi,z42_host}.h` 拷真实 runtime 头（非源码树 `#include "../../.."` 转发桩，打包后失效——与 iOS Z42VMC 同根因）
- [x] launcher_workload install **android 分支**：runtime 的 per-ABI `.so` → `jniLibs/<abi>/`（jniLibs.srcDirs + CMake 拾取）+ stdlib zpkgs → `assets/stdlib/`（assets.srcDirs 烘入 AAR）
- [x] **多 RID 单 workload**：`--from` 改为可选——给 `--from` 则（重）装 tooling；仅 `--runtime`/`--rid` 则**增量**装 runtime slice 进已装 workload，不抹掉前一个 RID 的 jniLibs/assets。android-arm64 + android-x64 两 slice 共存
- [x] GREEN 端到端：`xtask package release --rid android-{arm64,x64}` → runtime pack + gradle tooling；`workload install android --from <tooling> --runtime <arm64>` 再 `--runtime <x64>`（增量）→ `gradlew :z42vm:assembleDebug` **BUILD SUCCESSFUL → z42vm-debug.aar**（4.3M，含 jni/{arm64-v8a,x86_64}/libz42_{platform_android,vm_jni}.so + assets/stdlib/*.zpkg）

### 关键实现发现（android）
- **android tooling = gradle 工程**（≠ ios/wasm 的扁平/单文件可构建单元）：CMake 用 `jniLibs.srcDirs` 链 prebuilt `.so`，build.gradle `abiFilters` 列全部 ABI → **AAR 需所有 ABI 的 .so 同时在场** → 一 workload 多 runtime pack。
- **多 RID install 必须增量**：tooling 拷贝会 `Delete(wlDest, true)`，第二个 RID 若再带 `--from` 会抹掉第一个 RID 的 jniLibs slice → `--from` 改可选，runtime-only install 增量叠加。
- stdlib zpkgs **版本锁定于 runtime** → 入 runtime pack `libs/`，install 时铺进 `assets/stdlib/`（不随 tooling 走）。
- `_pkgSha256Check` 对缺失 `native/include/*.h` 容忍（split tooling 包头文件在 runtime pack，不在 tooling pkgDir）。

## （历史）B2-3 android 实装方案（已精确摸清；工具链本地已装齐）

**工具链可用**：`artifacts/tools/{android-ndk,android-sdk,gradle}` + cargo-ndk + `.so`（aarch64-linux-android/release）均已装/已建——经 `z42 xtask.zpkg deps install --os android`（本地/CI 同径）。**android 不 blocked**。

**真实结构差异（比 ios/wasm 更重）**：
- ios/wasm 的包**即可构建单元**（Package.swift / package.json）。
- android 当前包是**扁平组件**（`kotlin/` + `cpp/` + `native/`），给 `z42 export android` **组装**成 gradle 工程用。
- 可构建单元是 **gradle 工程**：`src/toolchain/workload/platforms/android/`（gradlew + settings + `z42vm/` 模块；`z42vm/build.gradle.kts` 用 `jniLibs.srcDirs("src/main/jniLibs")` 引 `.so`，CMake 链它建 JNI bridge）。

**拆分方案**：
- runtime pack `z42-runtime-<ver>-android-<abi>`：`libz42_platform_android.so`（per-ABI；arm64-v8a + x86_64，多 RID）+ headers。
- workload tooling：**gradle 工程**（z42vm 模块 build.gradle + src/main/{java,cpp} + gradlew + settings）——即可构建单元（≠ 现扁平包）。
- install android 分支：把 runtime 的 `.so` 放到 tooling 的 `z42vm/src/main/jniLibs/<abi>/libz42_platform_android.so`（gradle `jniLibs.srcDirs` 自动拾取）。
- 验证：`gradlew :z42vm:assembleRelease`（ANDROID_HOME/NDK env 见 memory `reference_cmake_android_ndk_env`）→ AAR 链接独立安装的 .so。**重 + env 敏感**（CMake/NDK/gradle）。

> **android = 比 ios/wasm 更大的重构**（包结构从扁平改为 gradle 工程 + 牵连 export-assembly）+ 重 env-敏感 gradle 验证。机制本身已由 ios+wasm 两平台两策略验证。建议作为专注单元实装。

## （历史）wasm 方案（已实现，下方留作设计记录）

**wasm**（本地可验：repo node + wasm-pack + pkg-* 产物已存在）：
- 拆：runtime pack `z42-runtime-<ver>-browser-wasm`（native/{libz42.a,z42_wasm_bg.wasm,include} + pkg-web/ + pkg-nodejs/ + libs/）；tooling（js/ + package.json，**原样不改**）。
- **install 用 symlink 而非改写**：wasm 的 `package.json` `exports` map + `js/index.js` 多处引 pkg-*（且 index.js 有 js-相对 quirk）；install 把 runtime 的 `pkg-web`/`pkg-nodejs` **symlink 进 tooling 根**（z42 有 `File.SymLink`），则 package.json exports（root-相对 `./pkg-nodejs/`）原样解析，免多文件改写。
- 验证：install → `node --input-type=module -e "await import('<workloads/wasm>/pkg-nodejs/z42_wasm.js')"` 加载即证。
- install 平台分支：runtime 有 `native/Z42VM.xcframework` → ios（改写 Package.swift）；有 `pkg-web/` → wasm（symlink）。

**android**（需 gradle，本地无 → 归 CI）：照 iOS 模式拆 runtime pack（per-ABI `libz42.so`：android-arm64 + android-x64，多 RID）+ tooling（z42vm AAR facade + gradle 模板）；gradle 引独立 .so。多-ABI 多 RID（≠ ios 单容器 xcframework）。

## 余下（后续 change）
- [x] ~~B2-3 wasm（本地验）+ android（CI 验）~~——三平台均本地端到端验证完成。
- [ ] B2-4 CI release.yml 上传 workload + runtime packs + `workload install` 走 manifest 联网。
- [ ] 真实 iOS device/sim 多-slice xcframework 合并（本次用 macos 单 slice 验机制；真机包归 CI）。
- [ ] B1 命令发现（workload 命令进 `z42 -h` 树）。
