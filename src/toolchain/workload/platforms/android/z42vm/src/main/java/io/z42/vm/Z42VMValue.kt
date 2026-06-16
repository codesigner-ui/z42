package io.z42.vm

/**
 * Primitive value crossing the z42 host ABI. v0.1 supports the same
 * shapes as the cross-platform contract — null / i64 / f64 / bool.
 * Strings / objects / arrays land in a later spec.
 */
sealed class Z42VMValue {
    object Null : Z42VMValue()
    data class I64(val v: Long)    : Z42VMValue()
    data class F64(val v: Double)  : Z42VMValue()
    data class Bool(val v: Boolean): Z42VMValue()

    /** Tag dictionary mirroring `z42_abi.h Z42_VALUE_TAG_*`. */
    internal val tag: Int get() = when (this) {
        is Null -> 0
        is I64  -> 1
        is F64  -> 2
        is Bool -> 3
    }

    /**
     * 64-bit raw payload as the C ABI sees it. Float / bool are bit-
     * packed; null gets 0.
     */
    internal val payload: Long get() = when (this) {
        is Null -> 0L
        is I64  -> v
        is F64  -> java.lang.Double.doubleToRawLongBits(v)
        is Bool -> if (v) 1L else 0L
    }

    companion object {
        internal fun fromRaw(tag: Int, payload: Long): Z42VMValue = when (tag) {
            0 -> Null
            1 -> I64(payload)
            2 -> F64(java.lang.Double.longBitsToDouble(payload))
            3 -> Bool(payload != 0L)
            else -> Null // unsupported tags surface as null in v0.1
        }
    }
}
