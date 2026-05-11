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

/// Default iOS resolver — reads `<namespace>.zpkg` from the app
/// `Bundle.main`. Stdlib zpkgs are dropped into the bundle by the
/// SwiftPM `Z42VMResources` resource (see `Package.swift`) or by the
/// host app's own Copy Bundle Resources phase.
public struct BundleZpkgResolver: ZpkgResolver {
    public let bundle: Bundle
    public let subdirectory: String?

    public init(bundle: Bundle = .main, subdirectory: String? = "stdlib") {
        self.bundle = bundle
        self.subdirectory = subdirectory
    }

    public func resolve(namespace: String) -> Data? {
        guard let url = bundle.url(
            forResource: namespace,
            withExtension: "zpkg",
            subdirectory: subdirectory
        ) else {
            return nil
        }
        return try? Data(contentsOf: url)
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
