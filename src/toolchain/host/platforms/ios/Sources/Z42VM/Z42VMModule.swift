import Foundation

/// Opaque handle for a loaded `.zbc` module. Lifetime is tied to its
/// owning `Z42VM`; once the VM is `deinit`'d, holding a `Z42VMModule`
/// is allowed but any operation through it returns `.notInit`.
public final class Z42VMModule {
    let handle: OpaquePointer

    init(handle: OpaquePointer) {
        self.handle = handle
    }
}
