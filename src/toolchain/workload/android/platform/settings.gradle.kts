// SwiftPM ≅ this file for Gradle: declare what subprojects participate.
//
// Spec: docs/spec/archive/2026-05-12-add-platform-android/

pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "Z42VM"
include(":z42vm")
