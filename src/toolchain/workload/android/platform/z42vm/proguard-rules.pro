# Library-side ProGuard rules. Keep the public Kotlin facade intact so
# minification doesn't rename classes / methods the JNI bridge expects.

-keep class io.z42.vm.** { *; }
-keepclassmembers class io.z42.vm.Z42VM {
    native <methods>;
}
