# Design: Android Platform Scaffold

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│              Android App (Java/Kotlin)                      │
│                                                             │
│   import com.codesigner.z42.Z42Vm                          │
│   val vm = Z42Vm()                                          │
│   vm.setStdoutHandler { line -> textView.append(line) }    │
│   vm.loadZbc(assets.open("01_hello.zbc").readBytes())       │
│   vm.run("main")                                            │
└────────────────────┬────────────────────────────────────────┘
                     │ Kotlin → JNI
                     ▼
┌─────────────────────────────────────────────────────────────┐
│  z42-runtime AAR                                            │
│                                                             │
│  Z42Vm.kt (Kotlin facade)                                   │
│   ├─ external fun nativeNew(): Long                         │
│   ├─ external fun nativeLoad(handle: Long, data: ByteArray) │
│   └─ external fun nativeRun(handle: Long, entry: String)    │
│                                                             │
│  jniLibs/                                                   │
│  ├─ arm64-v8a/libz42android.so                              │
│  ├─ armeabi-v7a/libz42android.so                            │
│  └─ x86_64/libz42android.so       (emulator)                │
└────────────────────┬────────────────────────────────────────┘
                     │ JNI ABI
                     ▼
┌─────────────────────────────────────────────────────────────┐
│  z42-android JNI bridge crate (Rust, cdylib)                │
│                                                             │
│  src/lib.rs  #[no_mangle] Java_com_codesigner_z42_Z42Vm_*  │
│              wraps z42_runtime::interp::Interpreter         │
└────────────────────┬────────────────────────────────────────┘
                     │ depends on
                     ▼
┌─────────────────────────────────────────────────────────────┐
│  src/runtime/  (--features android = interp-only + aot)     │
└─────────────────────────────────────────────────────────────┘
```

## Decisions

### Decision 1: JNI 桥接方式 ——「jni 0.21 + 手写 wrapper」

**选项**：A. jni crate（事实标准）；B. robusta（macro 自动）；C. UniFFI（多语言 binding 框架）

**决定**：选 **A**。理由：
- jni crate API 稳定、文档好、社区案例多
- robusta 抽象层带来调试困难；本 spec 尺度小，手写 wrapper 可控
- UniFFI 是 iOS + Android 跨平台抽象，但需要写 IDL；P4.3 / P4.4 量级不值得

### Decision 2: Kotlin facade API（锁定）

[platform/android/z42-runtime/src/main/java/com/codesigner/z42/Z42Vm.kt](platform/android/z42-runtime/src/main/java/com/codesigner/z42/Z42Vm.kt)：

```kotlin
package com.codesigner.z42

import androidx.annotation.Keep
import java.io.Closeable

@Keep
class Z42Vm : Closeable {
    private var nativeHandle: Long = nativeNew()
    private var stdoutHandler: ((String) -> Unit)? = null
    private var stderrHandler: ((String) -> Unit)? = null

    fun loadZpkg(data: ByteArray) {
        check(nativeHandle != 0L) { "Z42Vm closed" }
        nativeLoadZpkg(nativeHandle, data)
    }

    fun loadZbc(data: ByteArray) {
        check(nativeHandle != 0L) { "Z42Vm closed" }
        nativeLoadZbc(nativeHandle, data)
    }

    fun setStdoutHandler(handler: (String) -> Unit) {
        stdoutHandler = handler
    }

    fun setStderrHandler(handler: (String) -> Unit) {
        stderrHandler = handler
    }

    fun run(entryPoint: String, args: Array<String> = emptyArray()) {
        check(nativeHandle != 0L) { "Z42Vm closed" }
        nativeRun(nativeHandle, entryPoint, args)
    }

    override fun close() {
        if (nativeHandle != 0L) {
            nativeFree(nativeHandle)
            nativeHandle = 0L
        }
    }

    // 由 JNI 回调，通过 stdoutHandler 转发
    @Keep
    private fun onStdout(line: String) {
        stdoutHandler?.invoke(line)
    }

    @Keep
    private fun onStderr(line: String) {
        stderrHandler?.invoke(line)
    }

    companion object {
        init {
            System.loadLibrary("z42android")
        }

        @JvmStatic
        private external fun nativeNew(): Long

        @JvmStatic
        private external fun nativeFree(handle: Long)

        @JvmStatic
        private external fun nativeLoadZpkg(handle: Long, data: ByteArray)

        @JvmStatic
        private external fun nativeLoadZbc(handle: Long, data: ByteArray)

        @JvmStatic
        private external fun nativeRun(handle: Long, entry: String, args: Array<String>)
    }
}
```

[platform/android/z42-runtime/src/main/java/com/codesigner/z42/Z42Exception.kt](platform/android/z42-runtime/src/main/java/com/codesigner/z42/Z42Exception.kt)：

```kotlin
package com.codesigner.z42

class Z42Exception(message: String, val errorCode: Int = 0) : RuntimeException(message)
```

### Decision 3: Rust JNI bridge 入口（锁定）

[platform/android/z42-runtime/src/main/rust/Cargo.toml](platform/android/z42-runtime/src/main/rust/Cargo.toml)：

```toml
[package]
name = "z42-android"
version = "0.1.0"
edition = "2021"

[lib]
name = "z42android"
crate-type = ["cdylib"]

[dependencies]
z42-runtime = { path = "../../../../../../src/runtime", default-features = false, features = ["android"] }
jni = "0.21"
log = "0.4"
android_logger = "0.14"
```

[platform/android/z42-runtime/src/main/rust/src/lib.rs](platform/android/z42-runtime/src/main/rust/src/lib.rs)：

```rust
use jni::JNIEnv;
use jni::objects::{JClass, JByteArray, JString, JObjectArray, JObject};
use jni::sys::{jlong, jobjectArray};

use z42_runtime::interp::Interpreter;

struct VmHandle {
    interp: Interpreter,
    // weak ref to Java Z42Vm instance for stdout callback
}

#[no_mangle]
pub extern "system" fn Java_com_codesigner_z42_Z42Vm_nativeNew(
    _env: JNIEnv, _class: JClass,
) -> jlong {
    android_logger::init_once(android_logger::Config::default().with_tag("z42"));
    let handle = Box::new(VmHandle { interp: Interpreter::new() });
    Box::into_raw(handle) as jlong
}

#[no_mangle]
pub extern "system" fn Java_com_codesigner_z42_Z42Vm_nativeFree(
    _env: JNIEnv, _class: JClass, handle: jlong,
) {
    if handle != 0 {
        unsafe { drop(Box::from_raw(handle as *mut VmHandle)); }
    }
}

#[no_mangle]
pub extern "system" fn Java_com_codesigner_z42_Z42Vm_nativeLoadZbc<'a>(
    mut env: JNIEnv<'a>, _class: JClass<'a>, handle: jlong, data: JByteArray<'a>,
) {
    let bytes = env.convert_byte_array(&data).unwrap();
    let vm = unsafe { &mut *(handle as *mut VmHandle) };
    if let Err(e) = vm.interp.load_zbc(&bytes) {
        env.throw_new("com/codesigner/z42/Z42Exception", e.to_string()).ok();
    }
}

#[no_mangle]
pub extern "system" fn Java_com_codesigner_z42_Z42Vm_nativeRun<'a>(
    mut env: JNIEnv<'a>, _class: JClass<'a>, handle: jlong,
    entry: JString<'a>, _args: JObjectArray<'a>,
) {
    let entry_str: String = env.get_string(&entry).unwrap().into();
    let vm = unsafe { &mut *(handle as *mut VmHandle) };
    if let Err(e) = vm.interp.call(&entry_str, &[]) {
        env.throw_new("com/codesigner/z42/Z42Exception", e.to_string()).ok();
    }
}
```

### Decision 4: Gradle 项目结构（锁定）

[platform/android/settings.gradle.kts](platform/android/settings.gradle.kts)：

```kotlin
pluginManagement {
    repositories {
        gradlePluginPortal()
        google()
        mavenCentral()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "z42-android"
include(":z42-runtime", ":demo-app")
```

[platform/android/z42-runtime/build.gradle.kts](platform/android/z42-runtime/build.gradle.kts)：

```kotlin
plugins {
    id("com.android.library")
    kotlin("android")
}

android {
    namespace = "com.codesigner.z42"
    compileSdk = 34

    defaultConfig {
        minSdk = 24
        ndk {
            abiFilters += listOf("arm64-v8a", "armeabi-v7a", "x86_64")
        }
    }

    sourceSets {
        getByName("main").jniLibs.srcDirs("src/main/jniLibs")
    }

    publishing {
        singleVariant("release") { withSourcesJar() }
    }
}

dependencies {
    implementation("androidx.annotation:annotation:1.7.1")
    androidTestImplementation("androidx.test:runner:1.5.2")
    androidTestImplementation("androidx.test.ext:junit:1.1.5")
}
```

### Decision 5: build.sh 接口

[platform/android/build.sh](platform/android/build.sh)：

```bash
#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"

mode=${1:-release}
ABIS=("arm64-v8a" "armeabi-v7a" "x86_64")

# 1. cargo-ndk 构建 .so（4 个 ABI）
cd z42-runtime/src/main/rust
for abi in "${ABIS[@]}"; do
    cargo ndk -t "$abi" -o ../jniLibs build $([[ "$mode" == "release" ]] && echo "--release")
done
cd ../../../..

# 2. Gradle 打 AAR
./gradlew :z42-runtime:assemble${mode^}

echo "✅ AAR built: z42-runtime/build/outputs/aar/"
```

### Decision 6: demo App

[platform/android/demo-app/src/main/java/com/codesigner/z42/demo/MainActivity.kt](platform/android/demo-app/src/main/java/com/codesigner/z42/demo/MainActivity.kt)：

```kotlin
package com.codesigner.z42.demo

import android.os.Bundle
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import com.codesigner.z42.Z42Vm

class MainActivity : AppCompatActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        val output = findViewById<TextView>(R.id.output)
        Z42Vm().use { vm ->
            vm.setStdoutHandler { line ->
                runOnUiThread { output.append("$line\n") }
            }
            val zbc = assets.open("01_hello.zbc").readBytes()
            vm.loadZbc(zbc)
            vm.run("main")
        }
    }
}
```

### Decision 7: instrumented test

[platform/android/z42-runtime/src/androidTest/java/com/codesigner/z42/Z42VmTest.kt](platform/android/z42-runtime/src/androidTest/java/com/codesigner/z42/Z42VmTest.kt)：

```kotlin
package com.codesigner.z42

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.assertEquals
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class Z42VmTest {
    @Test
    fun helloWorldRuns() {
        val ctx = InstrumentationRegistry.getInstrumentation().context
        val zbc = ctx.assets.open("01_hello.zbc").readBytes()
        val captured = StringBuilder()

        Z42Vm().use { vm ->
            vm.setStdoutHandler { line -> captured.appendLine(line) }
            vm.loadZbc(zbc)
            vm.run("main")
        }

        assertEquals("Hello, World!\n", captured.toString())
    }
}
```

### Decision 8: justfile 接入

[justfile](justfile) 在 P4.2 基础上扩展：

```just
platform name action *args:
    #!/usr/bin/env bash
    case "{{name}}" in
        wasm)    just platform-wasm-{{action}} {{args}} ;;
        android) just platform-android-{{action}} {{args}} ;;
        ios)     echo "P4.4 待实施" && exit 1 ;;
        *) echo "未知平台: {{name}}" && exit 1 ;;
    esac

platform-android-build:
    ./platform/android/build.sh release

platform-android-build-debug:
    ./platform/android/build.sh debug

platform-android-test:
    cd platform/android && ./gradlew :z42-runtime:testDebugUnitTest

platform-android-test-emulator:
    cd platform/android && ./gradlew :z42-runtime:connectedDebugAndroidTest

platform-android-demo-install:
    cd platform/android && ./gradlew :demo-app:installDebug
```

### Decision 9: CI 接入

[.github/workflows/ci.yml](.github/workflows/ci.yml) 加 job：

```yaml
platform-android:
  runs-on: macos-latest   # macOS 提供 emulator HAXM 加速
  steps:
    - uses: actions/checkout@v4
    - uses: dtolnay/rust-toolchain@stable
      with:
        targets: aarch64-linux-android,armv7-linux-androideabi,x86_64-linux-android
    - name: Install cargo-ndk
      run: cargo install cargo-ndk --version 3.5
    - uses: actions/setup-java@v4
      with: { distribution: 'temurin', java-version: '17' }
    - uses: android-actions/setup-android@v3
    - name: Build AAR
      run: just platform android build
    - name: Run unit tests
      run: just platform android test
    - name: Run emulator tests
      uses: reactivecircus/android-emulator-runner@v2
      with:
        api-level: 29
        target: default
        arch: x86_64
        script: just platform android test-emulator
```

### Decision 10: examples/01_hello.zbc 同步到 demo assets

为了 demo 跑通，需要先用编译器生成 .zbc 并复制到 demo-app/src/main/assets/。

`build.sh` 加预处理步骤：

```bash
# 0. 编译示例（如有 z42 编译器可用）
if command -v dotnet &>/dev/null; then
    dotnet run --project ../../src/compiler/z42.Driver -- compile \
        ../../examples/01_hello.z42 -o demo-app/src/main/assets/01_hello.zbc
fi
```

## Implementation Notes

### stdout/stderr 桥接（v0.1 简化）

类似 P4.2 wasm，初版可接受"interp 直接 println! → Android logcat 通过 android_logger"，setStdoutHandler 暂时 fallback。完整桥接（JNI 回调 Z42Vm.onStdout）留 v0.2。

### Cargo workspace 的 deep nested path

`../../../../../../src/runtime` 路径很深；可考虑：
- 不纳入 workspace（独立项目，每次单独 cargo build）
- 或在 [src/runtime/Cargo.toml](src/runtime/Cargo.toml) workspace.members 加 `../platform/android/z42-runtime/src/main/rust`

倾向：纳入 workspace（统一版本管理）。

### .so 增量构建

cargo-ndk 默认每次都重新链接；CI 可缓存 `target/` 加速。

### Gradle wrapper 版本

固定 8.5+（与 AGP 8.x 兼容）。

### NDK 版本

固定 r26+（支持 16KB page size 的 Android 15+）。

## Testing Strategy

- ✅ `cargo build --target aarch64-linux-android -p z42-android` 通过（需 cargo-ndk）
- ✅ `./platform/android/build.sh release` 产出 AAR 在 `z42-runtime/build/outputs/aar/`
- ✅ AAR 含 4 个 ABI 的 .so（unzip 验证）
- ✅ `gradlew :z42-runtime:testDebugUnitTest` JVM 单测通过
- ✅ `gradlew :z42-runtime:connectedDebugAndroidTest` emulator instrumented test 通过（CI 上 reactivecircus/android-emulator-runner）
- ✅ demo App 安装到 emulator 后启动显示 "Hello, World!"
- ✅ vm_core 5 个用例的 .zbc 嵌入 demo assets 后跑出与 desktop 一致输出
- ✅ AAR 大小 ≤ 8 MB（含 4 ABI）
- ✅ CI platform-android job 全绿
