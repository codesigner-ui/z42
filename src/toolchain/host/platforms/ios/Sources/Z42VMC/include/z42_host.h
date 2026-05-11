/* Forwarding header — pulls in the canonical z42_host.h from
 * src/runtime/include/. Swift's clang module sees the same types as
 * the runtime crate.
 *
 * Spec: docs/spec/archive/2026-05-12-add-platform-ios/
 */
#include "../../../../../../../runtime/include/z42_host.h"
