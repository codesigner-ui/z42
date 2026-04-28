//! z42 Tier 2 ergonomic Rust API for native interop.
//!
//! Sits on top of [`z42_abi`] and exposes user-friendly types and traits that
//! Rust crate authors implement to expose native types to z42 user code.
//!
//! Status: C1 scaffold. Trait shapes and type aliases are stable enough for
//! C2..C5 to fill in implementation; no runtime behavior is provided yet.
//!
//! ```ignore
//! use z42_rs::prelude::*;
//!
//! struct MyType { /* ... */ }
//!
//! impl Z42Type for MyType {
//!     const MODULE: &'static str = "demo";
//!     const NAME:   &'static str = "MyType";
//!     fn descriptor() -> *const z42_abi::Z42TypeDescriptor_v1 {
//!         /* C3 derive macro fills this in */
//!         core::ptr::null()
//!     }
//! }
//! ```

#![cfg_attr(not(test), no_std)]

pub mod traits;
pub mod types;

/// One-stop import for users implementing native types.
pub mod prelude {
    pub use crate::traits::{Visitor, Z42Traceable, Z42Type};
    pub use crate::types::{Descriptor, Z42Args, Z42Error, Z42TypeRef, Z42Value};
}

pub use z42_abi;
