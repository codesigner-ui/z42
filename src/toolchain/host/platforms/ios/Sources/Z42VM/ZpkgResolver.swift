import Foundation

/// Resolve a namespace (e.g. `"z42.core"`, `"Std.IO"`) to its zpkg
/// bytes. Returning `nil` signals "this resolver doesn't know about
/// that namespace"; the runtime then falls back to the next configured
/// resolver (typically a search-path scan).
///
/// Spec: docs/design/runtime/embedding.md §11.
public protocol ZpkgResolver {
    func resolve(namespace: String) -> Data?
}

/// Default iOS resolver. Resolution order:
///   1. **Namespace index** (`<subdirectory>/index.json`) — map
///      `"Std.IO" → "z42.io.zpkg"` etc. Index is produced by host
///      `scripts/build-stdlib.sh` and shipped via `build.sh`.
///   2. **Filename fallback** — namespace-as-filename
///      (`<subdirectory>/<namespace>.zpkg`); useful for custom resolvers
///      that publish single-namespace-per-file packages without an index.
///
/// Index makes the resolver correct for stdlib where one zpkg provides
/// multiple namespaces (e.g. `z42.core.zpkg` ships `Std` + `Std.Exceptions`).
/// Fallback preserves the simple "one namespace per file" convention for
/// hosts that don't ship an index.
///
/// Spec: docs/spec/archive/2026-05-12-fix-bundle-resolver-namespace-index/
public struct BundleZpkgResolver: ZpkgResolver {
    public let bundle: Bundle
    public let subdirectory: String?
    private let index: [String: String]

    public init(bundle: Bundle = .main, subdirectory: String? = "stdlib") {
        self.bundle = bundle
        self.subdirectory = subdirectory
        self.index = Self.loadIndex(bundle: bundle, subdirectory: subdirectory)
    }

    public func resolve(namespace: String) -> Data? {
        // 1. Index-driven lookup.
        if let filename = index[namespace],
           let data = readResource(filename: filename) {
            return data
        }
        // 2. Filename fallback (namespace-as-basename).
        return readResource(filename: "\(namespace).zpkg")
    }

    /// Load `<subdirectory>/<filename>` (e.g. `stdlib/z42.io.zpkg`) and
    /// return its bytes; nil on any error (file missing / unreadable).
    private func readResource(filename: String) -> Data? {
        let nsFilename = filename as NSString
        let basename = nsFilename.deletingPathExtension
        let ext = nsFilename.pathExtension
        guard !basename.isEmpty, !ext.isEmpty,
              let url = bundle.url(
                  forResource: basename,
                  withExtension: ext,
                  subdirectory: subdirectory
              )
        else { return nil }
        return try? Data(contentsOf: url)
    }

    private static func loadIndex(bundle: Bundle, subdirectory: String?) -> [String: String] {
        guard let url = bundle.url(
            forResource: "index",
            withExtension: "json",
            subdirectory: subdirectory
        ),
        let data = try? Data(contentsOf: url),
        let parsed = try? JSONSerialization.jsonObject(with: data, options: []),
        let map = parsed as? [String: String]
        else { return [:] }
        return map
    }
}

/// HashMap-backed resolver useful for tests and for hosts that build
/// the zpkg dictionary at startup (e.g. fetched over network).
public final class MapZpkgResolver: ZpkgResolver {
    private var map: [String: Data]

    public init(_ initial: [String: Data] = [:]) {
        self.map = initial
    }

    public func set(_ namespace: String, _ bytes: Data) {
        map[namespace] = bytes
    }

    public func resolve(namespace: String) -> Data? {
        map[namespace]
    }
}
