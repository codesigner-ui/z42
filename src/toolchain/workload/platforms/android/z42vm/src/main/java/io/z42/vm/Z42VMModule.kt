package io.z42.vm

/**
 * Opaque handle for a loaded `.zbc` module. Lifetime tied to the
 * owning [Z42VM]; once the VM is closed, holding a [Z42VMModule]
 * is allowed but any operation through it surfaces as
 * [Z42VMException.NOT_INIT].
 */
class Z42VMModule internal constructor(
    @JvmField internal val nativeHandle: Long,
)
