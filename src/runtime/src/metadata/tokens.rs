//! Runtime tokens for dispatch hot path.
//!
//! Each token is a `u32` newtype identifying a runtime entity (method / type /
//! builtin / field / static-field / vtable-slot). Tokens are **stable within
//! one VmContext lifetime, NOT persisted to zbc / cross-process**. The current
//! lifecycle:
//!
//!   1. Module load → `metadata::resolver::resolve_module` walks every IR
//!      instruction and resolves available `String` references to tokens
//!      (intra-module hits resolved eagerly; cross-zpkg lazy loaded targets
//!      stay `UNRESOLVED` and are filled on first dispatch).
//!   2. Interp / JIT dispatch hot path checks the cached token; on hit it
//!      indexes a flat `Vec<Function>` / `Vec<Value>` / etc directly,
//!      replacing per-call `HashMap<&str, _>::get()`.
//!
//! See `spec/changes/introduce-method-token/` (or its archived form) for the
//! full design rationale, including the Decision 6 flip that brought
//! Field/Static into Phase 1 alongside method dispatch.

use serde::{Deserialize, Serialize};

/// Sentinel value indicating an unresolved cache slot. Encoded as `u32::MAX`
/// because legitimate IDs are bounded by metadata size (≪ 2^32 entries in
/// practice); a sentinel cannot collide with a real id without overflowing.
pub const UNRESOLVED: u32 = u32::MAX;

macro_rules! define_token {
    ($(#[$meta:meta])* $name:ident) => {
        $(#[$meta])*
        #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
        #[repr(transparent)]
        pub struct $name(pub u32);

        impl $name {
            pub const UNRESOLVED: Self = Self(UNRESOLVED);

            #[inline]
            pub fn is_resolved(self) -> bool {
                self.0 != UNRESOLVED
            }
        }
    };
}

define_token!(
    /// Identifies one `Function` in `Module.functions: Vec<Function>` (per module).
    /// Resolved at load by `module.func_index[name]`.
    MethodId
);

define_token!(
    /// Identifies one `ClassDef` / `TypeDesc` in `Module.classes` (per module).
    /// Resolved at load by `module.type_registry[name]`.
    TypeId
);

define_token!(
    /// Identifies one builtin function in the global `BUILTINS` static table
    /// (cross-module / per-process). Resolved at load by
    /// `corelib::dispatch_table::builtin_id_of(name)`. Resolution is mandatory
    /// (closed set); a miss is a bug (unknown builtin name).
    BuiltinId
);

define_token!(
    /// Identifies one field slot in a specific `TypeDesc.fields: Vec<FieldSlot>`
    /// (per type). Stored inside `FieldIC` as the cached slot for a
    /// `FieldGet` / `FieldSet` site after the receiver-type IC fires.
    FieldId
);

define_token!(
    /// Identifies one static field slot in `VmContext.static_fields: Vec<Value>`
    /// (cross-module / per-VmContext). Allocated lazily via
    /// `VmContext::resolve_static_field_id(name)` so cross-zpkg static fields
    /// can be encountered in arbitrary load order.
    StaticFieldId
);

define_token!(
    /// Identifies one vtable slot in a specific `TypeDesc.vtable: Vec<...>`
    /// (per type). Stored inside `VCallIC` after the receiver-type IC fires.
    VTableSlot
);

#[cfg(test)]
mod tokens_tests {
    use super::*;

    #[test]
    fn unresolved_sentinel_is_u32_max() {
        assert_eq!(UNRESOLVED, u32::MAX);
        assert!(!MethodId::UNRESOLVED.is_resolved());
        assert!(!TypeId::UNRESOLVED.is_resolved());
        assert!(!BuiltinId::UNRESOLVED.is_resolved());
        assert!(!FieldId::UNRESOLVED.is_resolved());
        assert!(!StaticFieldId::UNRESOLVED.is_resolved());
        assert!(!VTableSlot::UNRESOLVED.is_resolved());
    }

    #[test]
    fn resolved_token_reports_resolved() {
        assert!(MethodId(0).is_resolved());
        assert!(MethodId(42).is_resolved());
        assert!(BuiltinId(7).is_resolved());
    }

    #[test]
    fn token_types_are_distinct() {
        // Compile-time check — these would fail to compile if MethodId == TypeId.
        let _m: MethodId = MethodId(1);
        let _t: TypeId = TypeId(1);
        // Cannot mix: `let _: MethodId = TypeId(1);` would not compile.
    }
}
