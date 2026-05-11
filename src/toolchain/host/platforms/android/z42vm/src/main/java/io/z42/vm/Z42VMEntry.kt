package io.z42.vm

/**
 * Opaque handle for a resolved entry (function / static method).
 * Lifetime tied to the owning [Z42VM]; same close semantics as
 * [Z42VMModule].
 */
class Z42VMEntry internal constructor(
    @JvmField internal val nativeHandle: Long,
)
