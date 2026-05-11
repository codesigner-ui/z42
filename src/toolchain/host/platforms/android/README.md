# Z42VM — Android facade

> 🟢 H4 落地（2026-05-12）。
>
> Spec：[`docs/spec/archive/2026-05-12-add-platform-android/`](../../../../docs/spec/archive/2026-05-12-add-platform-android/)
> 跨平台契约：[`../README.md`](../README.md)
> 实现原理：[`docs/design/runtime/embedding.md`](../../../../docs/design/runtime/embedding.md)
> 构建工作流：[`docs/workflow/building/android.md`](../../../../docs/workflow/building/android.md)

把 z42 VM 编进 Gradle AAR 模块，Kotlin / Compose app 引入后一行 `import io.z42.vm.Z42VM` 跑 `.zbc`。

## Quick Start

详细 step-by-step 见 [`docs/workflow/building/android.md`](../../../../docs/workflow/building/android.md)。最简略：

```bash
# 一次性
brew install --cask temurin                                # JDK 17
# Android Studio → SDK Manager → 装 SDK 34 + NDK r26
export ANDROID_NDK_HOME="$HOME/Library/Android/sdk/ndk/<version>"
rustup target add aarch64-linux-android armv7-linux-androideabi x86_64-linux-android i686-linux-android
cargo install cargo-ndk --locked
dotnet build src/compiler/z42.slnx                         # 编 stdlib

# 每次
cd src/toolchain/host/platforms/android
./build.sh
```

产物：`z42vm/build/outputs/aar/z42vm-release.aar` + `jniLibs/<abi>/libz42_platform_android.so` × 4 + `assets/stdlib/*.zpkg` × 6。

## API 速记

```kotlin
import io.z42.vm.Z42VM
import io.z42.vm.AssetZpkgResolver

Z42VM(zpkgResolver = AssetZpkgResolver(assets)).use { vm ->
    vm.stdoutHandler = { bytes -> textView.append(String(bytes)) }
    val m = vm.loadZbc(assets.open("hello.zbc").readBytes())
    val e = vm.resolveEntry(m, "App.Main")
    vm.invoke(e)
}
```

### `Z42VM(zpkgResolver, stdoutHandler?, stderrHandler?)`

- `zpkgResolver: ZpkgResolver` —— 默认 `AssetZpkgResolver(context.assets)` 读 `assets/stdlib/<ns>.zpkg`
- `stdoutHandler / stderrHandler: ((ByteArray) -> Unit)?` —— 每条 z42 输出触发一次，UTF-8 字节

### `Z42VMValue`

```kotlin
sealed class Z42VMValue {
    object Null : Z42VMValue()
    data class I64(val v: Long)    : Z42VMValue()
    data class F64(val v: Double)  : Z42VMValue()
    data class Bool(val v: Boolean): Z42VMValue()
}
```

H2 marshal 限 null + 三种原语；string / object / Array 推迟。

### `Z42VMException`

`RuntimeException` + `val status: Int` (1..99) + 标准 status 常量。映射详见 [`platforms/README.md`](../README.md) §错误码映射表。

### `ZpkgResolver` 接口

```kotlin
interface ZpkgResolver {
    fun resolve(namespace: String): ByteArray?
}
```

内置：

- `AssetZpkgResolver(assets, subdir = "stdlib")` —— 读 AAR `assets/stdlib/<ns>.zpkg`
- `MapZpkgResolver(initial = emptyMap())` —— 测试 / 自定义来源

## 架构

```
io.z42.vm.Z42VM  (Kotlin / public API)
        │
        ▼ JNI external fun nativeInitialize / nativeLoadZbc / ...
        │
libz42vm_jni.so  (C, CMake-built; src/main/cpp/z42vm_jni.c)
        │
        ▼ z42_host_*  (C ABI from z42_host.h)
        │
libz42_platform_android.so  (cargo-ndk-built; thin re-export of z42_host_*)
        │
        ▼  in-process
src/runtime/  (interp + aot feature; no JIT inside Android sandbox)
```

`libz42vm_jni.so` 和 `libz42_platform_android.so` 都打进 AAR 的 `jniLibs/<abi>/`，每 ABI 一份。

## 限制（v0.1）

- **仅 interp 模式**：JIT 与 Android ART 互斥
- **无 `native-interop`**：与 iOS 同步推迟。libffi-sys 在 cross-compile 时与 NDK 工具链不兼容；后续 spec 引入 vendored libffi 后开启
- **单实例**
- **同步 invoke**：UI 上请用 `Dispatchers.Default` 异步包装
- **Demo / JUnit / CI**：推迟到独立 spec（`add-platform-android-demo` / `-tests` / `-ci`）

## 故障排查

详细的 step-by-step 故障兜底见 [`docs/workflow/building/android.md`](../../../../docs/workflow/building/android.md) §Step 各栏的 ❗ 行。

## 与跨平台契约的对齐

类名 `Z42VM` / `Z42VMModule` / `Z42VMEntry` / `Z42VMValue` / `Z42VMException`、`ZpkgResolver` 接口、错误码 → status 数值映射，全部与 [`platforms/README.md`](../README.md) 一致。同一份 `.zbc` 在 iOS / Android / WASM 三平台行为应等价。
