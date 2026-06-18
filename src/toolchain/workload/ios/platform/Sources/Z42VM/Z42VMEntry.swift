import Foundation

/// Opaque handle for a resolved entry (function / static method).
/// Lifetime tied to the owning `Z42VM`; same `.notInit` rules as
/// `Z42VMModule`.
public final class Z42VMEntry {
    let handle: OpaquePointer

    init(handle: OpaquePointer) {
        self.handle = handle
    }
}
