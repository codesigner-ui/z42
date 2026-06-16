# Consumer-side ProGuard rules applied to apps that depend on this AAR.
# Keep all native-method-bearing classes intact so JNI lookups succeed.

-keep class io.z42.vm.** { *; }
