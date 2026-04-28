//! User-facing traits for native types.
//!
//! The trait shapes are stable as of C1; their associated items / default
//! impls may grow during C2..C5 to support invocation, RC integration, and
//! cycle traversal. New required methods are a breaking change and require a
//! crate-version bump.

use core::ffi::c_void;

use crate::types::Descriptor;

/// Marks a Rust type as exposable to z42.
///
/// Implementors provide a static descriptor pointer that the VM uses to
/// allocate, dispatch, and manage instances. Hand-written implementations are
/// supported but tedious; the [`Z42Type`](z42_macros::Z42Type) derive in
/// `z42-macros` is the intended path.
pub trait Z42Type: Sized {
    /// Module path the type lives under (e.g. `"numz42"`).
    const MODULE: &'static str;

    /// Type name as seen from z42 user code (e.g. `"Tensor"`).
    const NAME: &'static str;

    /// Pointer to a `'static` [`Descriptor`] describing this type's layout
    /// and methods. Must remain valid for the lifetime of the process.
    fn descriptor() -> *const Descriptor;
}

/// Visitor passed to [`Z42Traceable::trace`] when the cycle collector walks
/// references reachable from a native instance.
///
/// The exact methods are minimal in C1; richer traversal hooks (weak refs,
/// visited-set tracking) land alongside the cycle collector in L3.
pub trait Visitor {
    /// Report a strong reference to another GC-managed value.
    fn visit_ref(&mut self, ptr: *const c_void);
}

/// Optional trait for native types that hold references to other GC-managed
/// values; required for participation in cycle collection.
///
/// Set the `Z42_TYPE_FLAG_TRACEABLE` flag on the descriptor when a type
/// implements this trait, or omit the flag to keep the type opaque to the
/// collector (only safe for leaves and types whose graphs are statically
/// non-cyclic).
pub trait Z42Traceable {
    /// Walk every strong reference held by `self`, calling `visitor` on each.
    fn trace(&self, visitor: &mut dyn Visitor);
}
