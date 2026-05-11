// Root project — only declares Android Gradle Plugin + Kotlin classpath
// so the `z42vm` subproject can apply them.
//
// Spec: docs/spec/archive/2026-05-12-add-platform-android/

plugins {
    id("com.android.library") version "8.6.0" apply false
    id("org.jetbrains.kotlin.android") version "1.9.25" apply false
}
