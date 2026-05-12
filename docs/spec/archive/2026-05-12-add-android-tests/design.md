# Design: Android facade JUnit instrumented tests

## Architecture

```
host (macOS):
  ┌───────────────────────────────────────────────────────────────┐
  │ ./scripts/install-android-toolchain-local.sh                  │
  │   → artifacts/tools/android-sdk/      (cmdline-tools / SDK)   │
  │   → artifacts/tools/android-ndk/      (symlink → ndk/26.3.x)  │
  │   → artifacts/tools/gradle/gradle-8.7/                        │
  │   → AVD: z42_pixel6_api34 (Pixel 6, API 34, arm64-v8a)        │
  └───────────────────────────────────────────────────────────────┘
                          │
                          ▼
  ┌───────────────────────────────────────────────────────────────┐
  │ platforms/android/build.sh                                    │
  │   1. tooling check (cargo-ndk + 4 ABI + JDK 17+ + NDK_HOME)   │
  │   2. cp stdlib zpkgs + index.json → assets/stdlib/            │
  │   3. cd rust/ && cargo ndk -t × 4 → src/main/jniLibs/         │
  │   4. z42c examples/embedding/*.z42 → androidTest/assets/      │
  │      test-fixtures/{hello,multi_line}.zbc                     │
  │   5. ./gradlew :z42vm:assembleRelease  (AAR)                  │
  └───────────────────────────────────────────────────────────────┘
                          │
                          ▼
  ┌───────────────────────────────────────────────────────────────┐
  │ platforms/android/test.sh                                     │
  │   1. PATH = artifacts/tools/{node,gradle/.../bin,android-...} │
  │   2. emulator @z42_pixel6_api34 -no-window … &  (background)  │
  │   3. adb wait-for-device + sys.boot_completed loop (~60s)     │
  │   4. ./gradlew :z42vm:connectedAndroidTest                    │
  │   5. (on exit) adb emu kill                                   │
  └───────────────────────────────────────────────────────────────┘
                          │
                          ▼ adb installs test apk + Z42VM AAR
  ┌───────────────────────────────────────────────────────────────┐
  │ AVD running Pixel 6 emulator                                  │
  │   ↳ test apk runs Z42VMInstrumentedTest.testR{1..7}          │
  │       each invokes Z42VM Kotlin facade against bundled        │
  │       libz42_platform_android.so + libz42vm_jni.so + zpkgs    │
  └───────────────────────────────────────────────────────────────┘
```

## Decisions

### D1: Local toolchain via `install-android-toolchain-local.sh`

**问题：** Android 构建依赖庞大（SDK / NDK / emulator / system image / Gradle，合计 ~4 GB）。如何不污染开发机系统？

**决定：** **全 artifacts/tools/ 下安装**（同 wasm 走 install-node-local.sh 的模式）。SDK Manager 入口 `cmdline-tools` 也装在 `artifacts/tools/android-sdk/`；用户只需 `export ANDROID_HOME=$PWD/artifacts/tools/android-sdk` 临时使用。Gradle 同 artifacts/tools/gradle/gradle-8.7/。

**好处：**
- 跨开发机一致（pinned versions）
- 不动用户 ~/.gradle / Android Studio install
- artifacts/ 全 gitignore，clean 一键删

### D2: emulator headless + swiftshader GPU

**问题：** instrumented test 需要 emulator 跑起来。开 GUI 还是 headless？

**决定：** **Headless + swiftshader CPU 渲染**（`-no-window -no-audio -gpu swiftshader_indirect`）。

**理由：**
- 测试用例不需要 UI
- swiftshader 避免对 GPU 驱动的依赖（Apple silicon macOS 默认 Metal，swiftshader 走 CPU 软件渲染稳定）
- 节省 boot 时间（~30s vs GUI ~50s）

### D3: AVD `z42_pixel6_api34` 由 installer 预创建

**问题：** AVD 创建放 installer 还是 test.sh 每次重建？

**决定：** **installer 预创建 + test.sh 假设它存在**。`avdmanager create avd --name z42_pixel6_api34 --device pixel_6 --package "system-images;android-34;google_apis_playstore;arm64-v8a"`。test.sh 不存在 AVD 时报错 + 提示重跑 installer。

**理由：** AVD 是 ~一次性配置，重建慢（~30s）且无需求。installer 一次搞定。

### D4: test.sh 自管 emulator 生命周期

**问题：** emulator 是 long-running daemon。test.sh 启它 / 杀它 / 还是用户自管？

**决定：** **test.sh 自启自杀**（`trap 'adb emu kill' EXIT`）。

**理由：**
- 用户跑 `./test.sh` 期望"一条命令 + 全自动"
- 避免遗留 emulator 进程占内存
- 如果用户已经有 emulator 在跑，test.sh 检测到 `adb devices` 已有设备时跳过启 emulator（reuse pattern，与 add-wasm-tests playwright `reuseExistingServer` 同思路）

### D5: 不接进 `./scripts/test-all.sh`

**问题：** add-ios-tests / add-wasm-tests 都不接 test-all.sh 默认；Android 是否例外？

**决定：** **不接**。

**理由：**
- emulator boot ~60s + test 跑 ~20s = 总 ~80s，比 cargo / dotnet test 慢 10×
- 依赖 ~4GB Android toolchain；GREEN bar 抬高过分
- 已落地 wasm / iOS 的同样不接，保持一致
- 后续 CI spec 单独排（手动 + `--with-android` flag 是 v2 候选）

## Implementation Notes

### Z42VMInstrumentedTest.kt 结构 sketch

```kotlin
package io.z42.vm

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class Z42VMInstrumentedTest {
    private val ctx by lazy {
        InstrumentationRegistry.getInstrumentation().context
    }

    private fun fixture(name: String): ByteArray =
        ctx.assets.open("test-fixtures/$name.zbc").use { it.readBytes() }

    private fun makeVMWithSink(): Pair<Z42VM, Collector> {
        val coll = Collector()
        val vm = Z42VM(
            zpkgResolver = AssetZpkgResolver(ctx.assets, "stdlib"),
            stdoutHandler = { bytes -> coll.append(bytes) },
        )
        return vm to coll
    }

    @Test
    fun testSmokeHelloWorld() {
        makeVMWithSink().let { (vm, coll) ->
            vm.use {
                val mod = vm.loadZbc(fixture("hello"))
                val entry = vm.resolveEntry(mod, "Hello.Main")
                vm.invoke(entry)
            }
            assertEquals("hello, world\n", coll.text)
        }
    }

    // ... R2 .. R7
}

class Collector {
    private val buf = ByteArrayOutputStream()
    fun append(bytes: ByteArray) { synchronized(this) { buf.write(bytes) } }
    val text get() = synchronized(this) { buf.toByteArray() }.toString(Charsets.UTF_8)
}
```

注意：`Z42VMException` 而非 Swift 的 `Z42VMError`；status 字段类型 `Int`。

### z42vm/build.gradle.kts 改动 sketch

```kotlin
android {
    defaultConfig {
        // ...existing...
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }
}

dependencies {
    implementation("androidx.annotation:annotation:1.8.2")

    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.test:runner:1.6.1")
    androidTestImplementation("androidx.test:core:1.6.1")
}
```

### test.sh 结构 sketch

```bash
#!/usr/bin/env bash
set -euo pipefail
ROOT="..."
ANDROID_HOME="$ROOT/artifacts/tools/android-sdk"
GRADLE_USER_HOME="$ROOT/artifacts/tools/gradle-user-home"
JAVA_HOME=$(/usr/libexec/java_home -v 17+)
export ANDROID_HOME ANDROID_NDK_HOME GRADLE_USER_HOME JAVA_HOME

# 1. Reuse running emulator if adb already sees a device.
if ! "$ANDROID_HOME/platform-tools/adb" devices | grep -q "emulator-"; then
    "$ANDROID_HOME/emulator/emulator" @z42_pixel6_api34 \
        -no-window -no-audio -gpu swiftshader_indirect \
        > "$ROOT/artifacts/tools/emulator.log" 2>&1 &
    EMU_PID=$!
    trap '"$ANDROID_HOME/platform-tools/adb" emu kill 2>/dev/null || kill $EMU_PID' EXIT
fi

# 2. Wait for boot
"$ANDROID_HOME/platform-tools/adb" wait-for-device
until [[ "$("$ANDROID_HOME/platform-tools/adb" shell getprop sys.boot_completed 2>/dev/null)" =~ ^1 ]]; do
    sleep 2
done

# 3. Run tests
./gradlew :z42vm:connectedAndroidTest
```

## Risk

- **AGP NDK 版本 vs installer NDK 版本**：AGP 8.6 默认 NDK 26.1；installer 装的是 26.3。Gradle 自动下载 26.1 到 sdk/ndk/26.1.10909125/。多占空间 ~1.2GB 但不影响功能。后续可在 build.gradle.kts 加 `ndkVersion = "26.3.11579264"` 显式 pin。
- **emulator 启动失败**：常见原因 — AVD 配置错 / system image 缺；installer 已经创建好。若用户跑 `--force` 重装则 AVD 重建。
- **Apple silicon emulator 性能**：API 34 arm64 system image 直接跑（无 x86_64 → arm64 翻译），快。x86_64 host 上会慢 5-10×。
- **adb wait-for-boot 超时**：典型 ~60s；test.sh 不设 hard timeout，让用户 Ctrl-C。CI spec 时再加超时机制。
