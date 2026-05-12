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
 * Default Android resolver. Resolution order:
 *   1. **Namespace index** (`<subdir>/index.json`) — map
 *      `"Std.IO" → "z42.io.zpkg"` etc. Index is produced by host
 *      `scripts/build-stdlib.sh` and shipped via `build.sh`.
 *   2. **Filename fallback** — namespace-as-filename
 *      (`<subdir>/<namespace>.zpkg`); preserves the simple
 *      "one namespace per file" convention for hosts that don't ship
 *      an index.
 *
 * Index makes the resolver correct for stdlib where one zpkg provides
 * multiple namespaces (e.g. `z42.core.zpkg` ships `Std` + `Std.Exceptions`).
 *
 * Spec: docs/spec/archive/2026-05-12-fix-bundle-resolver-namespace-index/
 */
class AssetZpkgResolver(
    private val assets: AssetManager,
    private val subdir: String = "stdlib",
) : ZpkgResolver {
    private val index: Map<String, String> = loadIndex()

    override fun resolve(namespace: String): ByteArray? {
        // 1. Index-driven lookup.
        index[namespace]?.let { filename ->
            try {
                return assets.open("$subdir/$filename").use { it.readBytes() }
            } catch (_: IOException) {
                /* fall through to filename fallback */
            }
        }
        // 2. Filename fallback (namespace-as-basename).
        return try {
            assets.open("$subdir/$namespace.zpkg").use { it.readBytes() }
        } catch (_: IOException) {
            null
        }
    }

    private fun loadIndex(): Map<String, String> = try {
        val text = assets.open("$subdir/index.json").use {
            it.readBytes().toString(Charsets.UTF_8)
        }
        val obj = org.json.JSONObject(text)
        val result = HashMap<String, String>(obj.length())
        val keys = obj.keys()
        while (keys.hasNext()) {
            val k = keys.next()
            result[k] = obj.getString(k)
        }
        result
    } catch (_: Exception) {
        emptyMap()
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
