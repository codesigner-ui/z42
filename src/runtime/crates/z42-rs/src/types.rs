//! Re-exports of Tier 1 ABI types under user-facing names.
//!
//! The Rust mirror in [`z42_abi`] is layout-canonical; this module simply
//! re-exports those types so user code does not have to import the lower-tier
//! crate directly. Ergonomic conversion methods land in C2/C3.

pub use z42_abi::{Z42Args, Z42Error, Z42TypeRef, Z42Value};

/// Alias for the v1 native-type descriptor.
///
/// User code generally does not construct this directly; the C3 derive macro
/// produces a `static` of this type for each `#[derive(Z42Type)]`.
pub type Descriptor = z42_abi::Z42TypeDescriptor_v1;
