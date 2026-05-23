//! Load-time token resolution for the introduce-method-token spec
//! (Phase 1, 2026-05-08). Walks every IR instruction in a freshly built
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
//! Population timing: called from `Vm::run` after `merge_modules` /
//! `build_type_registry` are done (so all intra-module lookups succeed)
//! and before any dispatch happens (so hot paths see fully-populated
//! `ResolvedTokens`).

use crate::metadata::tokens::UNRESOLVED;
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
    /// `StaticGet` / `StaticSet` sites: cached `StaticFieldId`.
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

/// Walk every Function in `module` and populate its `resolved`
/// `OnceLock<ResolvedTokens>`. Idempotent: once `OnceLock` is
/// initialised on a function, subsequent calls are no-ops (the
/// `let _ = ...` ignores the duplicate-set error).
///
/// `ctx` is needed for `StaticGet/Set` resolution: static field IDs are
/// allocated lazily through `VmContext::resolve_static_field_id` so
/// cross-zpkg static fields can be encountered in any load order.
pub fn resolve_module(module: &crate::metadata::Module, ctx: &crate::vm_context::VmContext) {
    for func in &module.functions {
        // Skip if already populated (idempotent).
        if func.resolved.get().is_some() {
            continue;
        }

        // ─── Pass 1: enumerate token-bearing sites ────────────────────────
        // Per-kind site lists. Each entry: the source-string at that site,
        // captured for pass-2 resolution. site_index[block][instr] = the
        // appropriate per-kind site_idx (or UNRESOLVED for non-token instructions).
        let mut method_site_names:   Vec<String> = Vec::new();
        let mut builtin_site_names:  Vec<String> = Vec::new();
        let mut type_site_names:     Vec<String> = Vec::new();
        let mut static_site_names:   Vec<String> = Vec::new();
        let mut vcall_site_count:    u32 = 0;
        let mut field_site_count:    u32 = 0;

        let mut site_index: Vec<Vec<u32>> = Vec::with_capacity(func.blocks.len());

        for block in &func.blocks {
            let mut block_sites = vec![UNRESOLVED; block.instructions.len()];
            for (instr_idx, instr) in block.instructions.iter().enumerate() {
                use crate::metadata::Instruction;
                let site_idx = match instr {
                    Instruction::Call { func: name, .. } => {
                        let s = method_site_names.len() as u32;
                        method_site_names.push(name.clone());
                        s
                    }
                    Instruction::Builtin { name, .. } => {
                        let s = builtin_site_names.len() as u32;
                        builtin_site_names.push(name.clone());
                        s
                    }
                    Instruction::ObjNew { class_name, .. } => {
                        let s = type_site_names.len() as u32;
                        type_site_names.push(class_name.clone());
                        s
                    }
                    Instruction::VCall { .. } => {
                        let s = vcall_site_count;
                        vcall_site_count += 1;
                        s
                    }
                    Instruction::FieldGet { .. } | Instruction::FieldSet { .. } => {
                        let s = field_site_count;
                        field_site_count += 1;
                        s
                    }
                    Instruction::StaticGet { field, .. } | Instruction::StaticSet { field, .. } => {
                        let s = static_site_names.len() as u32;
                        static_site_names.push(field.clone());
                        s
                    }
                    _ => UNRESOLVED, // non-token-bearing instruction
                };
                block_sites[instr_idx] = site_idx;
            }
            site_index.push(block_sites);
        }

        // ─── Pass 2: resolve names → tokens ───────────────────────────────
        let method_tokens: Vec<AtomicU32> = method_site_names.iter()
            .map(|name| AtomicU32::new(
                module.func_index.get(name).copied()
                    .map(|idx| idx as u32)
                    .unwrap_or(UNRESOLVED)
            ))
            .collect();

        let builtin_tokens: Vec<u32> = builtin_site_names.iter()
            .map(|name| {
                // Static `BUILTINS[]` first, then per-VM ext registry (populated by
                // `native::ext::load_all` at VM startup). add-z42-compression
                // (2026-05-22): facade `[Native(lib="z42_compression", entry=...)]`
                // names resolve through the ext path.
                crate::corelib::builtin_id_of(name)
                    .or_else(|| crate::corelib::ext_builtin_id_of(ctx, name))
                    .unwrap_or_else(|| panic!(
                        "unknown builtin `{}` (typo? not in BUILTINS table or any \
                         dlopened native extension?)",
                        name
                    ))
                    .0
            })
            .collect();

        let type_tokens: Vec<AtomicU32> = type_site_names.iter()
            .map(|name| AtomicU32::new(
                module.type_registry.get(name)
                    .map(|td| td.id.0)
                    .unwrap_or(UNRESOLVED)
            ))
            .collect();

        let vcall_ic: Vec<VCallIC> = (0..vcall_site_count).map(|_| VCallIC::default()).collect();
        let field_ic: Vec<FieldIC> = (0..field_site_count).map(|_| FieldIC::default()).collect();

        // Static fields: lazy allocate through the VmContext so cross-zpkg
        // ordering doesn't matter. Resolution is "always succeed" — if the
        // name was first seen in this module, this is the allocation site.
        let static_field_tokens: Vec<AtomicU32> = static_site_names.iter()
            .map(|name| AtomicU32::new(ctx.resolve_static_field_id(name).0))
            .collect();

        let resolved = ResolvedTokens {
            method_tokens,
            builtin_tokens,
            type_tokens,
            vcall_ic,
            field_ic,
            static_field_tokens,
            site_index,
        };

        // OnceLock idempotent set — Err means already set (race or repeat call).
        let _ = func.resolved.set(resolved);
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

}
