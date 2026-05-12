/* Forwarding header — pulls in the canonical z42_host.h from
 * src/runtime/include/. The JNI bridge in z42vm_jni.c uses this for
 * Z42HostConfig + the z42_host_* function declarations.
 *
 * Spec: docs/spec/archive/2026-05-12-add-platform-android/
 */
#include "../../../../../../../../../runtime/include/z42_host.h"
