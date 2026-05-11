// Android library module — packages the Kotlin facade + JNI bridge +
// per-ABI Rust .so files into a single `.aar`.
//
// Spec: docs/spec/archive/2026-05-12-add-platform-android/

plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "io.z42.vm"
    compileSdk = 34

    defaultConfig {
        minSdk = 23

        consumerProguardFiles("consumer-rules.pro")

        externalNativeBuild {
            cmake {
                cppFlags("")
                arguments("-DANDROID_STL=c++_static")
            }
        }

        ndk {
            // Mirror the cargo-ndk targets driven by build.sh.
            abiFilters += listOf("arm64-v8a", "armeabi-v7a", "x86_64", "x86")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlinOptions {
        jvmTarget = "17"
    }

    sourceSets {
        getByName("main") {
            // cargo-ndk drops libz42_platform_android.so per ABI here.
            // build.sh runs cargo ndk before ./gradlew.
            jniLibs.srcDirs("src/main/jniLibs")
            // Stdlib zpkg files copied in by build.sh.
            assets.srcDirs("src/main/assets")
        }
    }

    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.22.1"
        }
    }

    packaging {
        // Ship the cargo-ndk-built .so alongside libz42vm_jni.so. CMake
        // pulls it in as IMPORTED but Gradle still needs to copy it
        // into the AAR.
        jniLibs.useLegacyPackaging = false
    }
}

dependencies {
    // Pure Kotlin facade — no runtime AndroidX needed for v0.1.
    implementation("androidx.annotation:annotation:1.8.2")
}
