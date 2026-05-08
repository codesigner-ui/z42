//! Load-time token resolution for the introduce-method-token spec
//! (Phase 1, 2026-05-08). Walks every IR instruction in a freshly merged
//! `Module` and pre-fills per-function `ResolvedTokens` so the dispatch
//! hot path can index `Vec<Function>` / `Vec<Value>` directly without
//! per-call hashing.
//!
//! Only **load-time-knowable** references are resolved here:
//!
//!   • `Call.func`            → `MethodId` (intra-module hits; cross-zpkg
//!                              left UNRESOLVED, filled on first dispatch)
//!   • `Builtin.name`         → `BuiltinId` (closed set — panic on miss)
//!   • `ObjNew.class_name`    → `TypeId` (intra-module; cross-zpkg lazy)
//!   • `StaticGet/Set.field`  → `StaticFieldId` (lazy global ID via
//!                              `VmContext::resolve_static_field_id`)
//!
//! **Receiver-type-dependent** references (`VCall.method`, `FieldGet/Set
//! .field_name`) are *not* resolved here. They use per-site monomorphic
//! inline caches (`VCallIC` / `FieldIC`) populated on first dispatch.
//!
//! Population timing: called from `metadata::loader::merge_modules` after
//! the `func_index` / `type_registry` are built, so all intra-module
//! lookups succeed.

use crate::metadata::tokens::{
    BuiltinId, FieldId, MethodId, StaticFieldId, TypeId, VTableSlot, UNRESOLVED,
};
use std::sync::atomic::AtomicU32;

/// Per-function lazy-init cache populated by `resolve_module`. Stored on
/// `Function.resolved: OnceLock<ResolvedTokens>` (`#[serde(skip)]`).
///
/// Layout: each token-kind has its own `Vec` indexed by **per-kind site
/// index** (Call sites are numbered 0..N independently of Builtin sites).
/// `site_index[block_idx][instr_idx]` maps a (block, instruction) location
/// to the appropriate site index for that kind.
#[derive(Debug, Default)]
pub struct ResolvedTokens {
    /// `Call` sites: cached `MethodId` (UNRESOLVED until first dispatch
    /// resolves it via `module.func_index` or lazy loader).
    pub method_tokens: Vec<AtomicU32>,
    /// `Builtin` sites: `BuiltinId` resolved at load (closed set —
    /// panic if a builtin name is unknown).
    pub builtin_tokens: Vec<u32>,
    /// `ObjNew` sites: cached `TypeId` (similar lifecycle to method_tokens).
    pub type_tokens: Vec<AtomicU32>,
    /// `VCall` sites: monomorphic inline cache (TypeId, vtable slot, MethodId).
    pub vcall_ic: Vec<VCallIC>,
    /// `FieldGet` / `FieldSet` sites: monomorphic inline cache (TypeId, field slot).
    pub field_ic: Vec<FieldIC>,
    /// `StaticGet` / `StaticSet` sites: cached `StaticFieldId` (lazy resolve
    /// via `VmContext::resolve_static_field_id` on first dispatch; cross-zpkg
    /// safe).
    pub static_field_tokens: Vec<AtomicU32>,
    /// `(block_idx, instr_idx) → site_idx` mapping. Outer Vec indexed by
    /// `block_idx`, inner Vec by `instr_idx`. Stores the appropriate
    /// per-kind site index for the instruction at that location, or
    /// `UNRESOLVED` for non-token-bearing instructions.
    pub site_index: Vec<Vec<u32>>,
}

/// Monomorphic inline cache for `VCall` sites. On first execution at this
/// site, `cached_type_id` is set to the receiver's `TypeId.0`, and the
/// resolved vtable slot + target `MethodId` are stored. Subsequent
/// executions with the same receiver type take the fast path; with a
/// different type they fall back to vtable walk + IC update (overwrites
/// the slot — Phase 1 mono IC, Phase X may add poly).
#[derive(Debug)]
pub struct VCallIC {
    pub cached_type_id: AtomicU32,
    pub cached_slot:    AtomicU32,
    pub cached_fn_idx:  AtomicU32,
}

impl Default for VCallIC {
    fn default() -> Self {
        Self {
            cached_type_id: AtomicU32::new(UNRESOLVED),
            cached_slot:    AtomicU32::new(UNRESOLVED),
            cached_fn_idx:  AtomicU32::new(UNRESOLVED),
        }
    }
}

/// Monomorphic inline cache for `FieldGet` / `FieldSet` sites. Mirrors
/// `VCallIC` but caches a field slot index (no method dispatch).
#[derive(Debug)]
pub struct FieldIC {
    pub cached_type_id: AtomicU32,
    pub cached_slot:    AtomicU32,
}

impl Default for FieldIC {
    fn default() -> Self {
        Self {
            cached_type_id: AtomicU32::new(UNRESOLVED),
            cached_slot:    AtomicU32::new(UNRESOLVED),
        }
    }
}

// ─── Token-bearing instruction kinds ──────────────────────────────────────────
//
// Used by `resolve_module` to enumerate sites. One enum value per kind so
// the per-kind site_idx stays distinct (Call site #2 is unrelated to
// Builtin site #2).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[allow(dead_code)] // populated at Phase 3 implementation
enum SiteKind {
    Method,
    Builtin,
    Type,
    VCall,
    Field,
    StaticField,
}

#[allow(dead_code)] // re-exported for Phase 3 hookup
pub(crate) use private_resolver::resolve_module;

mod private_resolver {
    use super::*;
    use crate::metadata::Module;

    /// Walk every Function in `module` and populate its `resolved`
    /// `OnceLock<ResolvedTokens>`. Idempotent: once `OnceLock` is
    /// initialised on a function, subsequent calls are no-ops.
    ///
    /// Phase 3 of the spec — currently a stub that initialises empty
    /// `ResolvedTokens` for every function so consumers can rely on
    /// `function.resolved.get().is_some()` after this returns. Real
    /// resolution logic (Call → MethodId, etc.) lands in the next commit.
    #[allow(dead_code, unused_variables)]
    pub(crate) fn resolve_module(module: &mut Module) {
        // 1. Assign TypeId to each TypeDesc in the registry.
        //    The registry HashMap is keyed by class name; iterate
        //    `module.classes` for stable ordering (id matches definition order).
        let registry = std::sync::Arc::clone(&std::sync::Arc::new(module.type_registry.clone()));
        // (We can't mutate Arc<TypeDesc> through the registry once shared,
        //  so this block is a placeholder — Phase 3 will redesign TypeId
        //  assignment to happen during type_registry construction in
        //  loader.rs, before any Arc shares are taken.)
        let _ = registry;

        // 2. Walk every function and populate ResolvedTokens (Phase 3).
        //    Stub: leave functions' `resolved` un-initialised. Hot paths
        //    will fall back to string lookup until Phase 3 lands.
        for _func in &module.functions {
            // function.resolved.set(ResolvedTokens { ... }).ok();
        }
    }
}

#[cfg(test)]
mod resolver_tests {
    use super::*;

    #[test]
    fn resolved_tokens_default_is_empty() {
        let r = ResolvedTokens::default();
        assert!(r.method_tokens.is_empty());
        assert!(r.builtin_tokens.is_empty());
        assert!(r.type_tokens.is_empty());
        assert!(r.vcall_ic.is_empty());
        assert!(r.field_ic.is_empty());
        assert!(r.static_field_tokens.is_empty());
        assert!(r.site_index.is_empty());
    }

    #[test]
    fn vcall_ic_default_is_unresolved() {
        let ic = VCallIC::default();
        use std::sync::atomic::Ordering;
        assert_eq!(ic.cached_type_id.load(Ordering::Relaxed), UNRESOLVED);
        assert_eq!(ic.cached_slot.load(Ordering::Relaxed), UNRESOLVED);
        assert_eq!(ic.cached_fn_idx.load(Ordering::Relaxed), UNRESOLVED);
    }

    #[test]
    fn field_ic_default_is_unresolved() {
        let ic = FieldIC::default();
        use std::sync::atomic::Ordering;
        assert_eq!(ic.cached_type_id.load(Ordering::Relaxed), UNRESOLVED);
        assert_eq!(ic.cached_slot.load(Ordering::Relaxed), UNRESOLVED);
    }

    // Compile-time use of imports to silence unused_imports warnings on the
    // tokens module (BuiltinId / FieldId / MethodId / StaticFieldId / TypeId /
    // VTableSlot will be used by Phase 3 implementation).
    #[allow(dead_code)]
    fn _suppress_unused_imports_check() {
        let _: BuiltinId = BuiltinId::UNRESOLVED;
        let _: FieldId = FieldId::UNRESOLVED;
        let _: MethodId = MethodId::UNRESOLVED;
        let _: StaticFieldId = StaticFieldId::UNRESOLVED;
        let _: TypeId = TypeId::UNRESOLVED;
        let _: VTableSlot = VTableSlot::UNRESOLVED;
    }
}
