package io.z42.vm

import android.content.res.AssetManager
import java.io.IOException

/**
 * Resolve a namespace (e.g. `"z42.core"`, `"Std.IO"`) to its zpkg
 * bytes. Returning `null` signals "this resolver doesn't know about
 * that namespace"; the runtime then falls back to other resolvers
 * (typically a search-path scan, which Android doesn't use).
 *
 * Spec: docs/design/runtime/embedding.md §11.
 */
interface ZpkgResolver {
    fun resolve(namespace: String): ByteArray?
}

/**
 * Default Android resolver — reads `<subdir>/<namespace>.zpkg` from
 * the app's `AssetManager`. Stdlib zpkgs are bundled into the AAR's
 * `assets/stdlib/` directory by `build.sh`.
 */
class AssetZpkgResolver(
    private val assets: AssetManager,
    private val subdir: String = "stdlib",
) : ZpkgResolver {
    override fun resolve(namespace: String): ByteArray? =
        try {
            assets.open("$subdir/$namespace.zpkg").use { it.readBytes() }
        } catch (_: IOException) {
            null
        }
}

/**
 * HashMap-backed resolver useful for tests and for hosts that build
 * the zpkg dictionary at startup (e.g. fetched over network).
 */
class MapZpkgResolver(
    initial: Map<String, ByteArray> = emptyMap(),
) : ZpkgResolver {
    private val map = HashMap(initial)

    fun set(namespace: String, bytes: ByteArray) {
        map[namespace] = bytes
    }

    override fun resolve(namespace: String): ByteArray? = map[namespace]
}
