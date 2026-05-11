/* Forwarding header — pulls in the canonical z42_abi.h from
 * src/runtime/include/. Both the JNI bridge and the cargo-ndk-built
 * libz42_platform_android.so see the same types.
 *
 * Spec: docs/spec/archive/2026-05-12-add-platform-android/
 */
#include "../../../../../../../../runtime/include/z42_abi.h"
