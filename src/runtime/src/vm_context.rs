//! `VmContext` — runtime-mutable state for one VM instance.
//!
//! Consolidates state that previously lived in `thread_local!` slots scattered
//! across `interp/` and `jit/` modules:
//!
//! - **`static_fields`**: user-class static field storage (was
//!   `interp/dispatch.rs::STATIC_FIELDS` + `jit/helpers.rs::STATIC_FIELDS`)
//! - **`pending_exception`**: JIT extern-C exception ABI bridge slot (was
//!   `jit/helpers.rs::PENDING_EXCEPTION` + the now-deleted
//!   `interp/mod.rs::PENDING_EXCEPTION` death-row sentinel)
//! - **`lazy_loader`**: on-demand zpkg loader registry (was
//!   `metadata/lazy_loader.rs::STATE`)
//!
//! The remaining `thread_local!` in the runtime is `jit/frame.rs::FRAME_POOL`,
//! a pure allocator cache — not state — and intentionally stays per-thread.
//!
//! # Lifecycle
//!
//! ```ignore
//! let mut ctx = VmContext::new();
//! ctx.install_lazy_loader(libs_dir, main_pool_len);
//! Vm::new(module, mode).run(&mut ctx, hint)?;
//! ```
//!
//! # Threading
//!
//! `VmContext` is **not** `Send` / `Sync` (intentionally — `RefCell` interior).
//! One ctx serves one OS thread at a time; multi-threaded VM is a follow-up
//! after JIT ABI restructuring.
//!
//! # JIT integration
//!
//! `JitModuleCtx::vm_ctx: *mut VmContext` carries the ctx pointer through the
//! `extern "C"` boundary. The pointer is set by `JitModule::run` for the
//! duration of one entry-point invocation and cleared on return. JIT helpers
//! access fields through `(*jit_ctx).vm_ctx` and call ctx methods.
//!
//! See `docs/design/vm-architecture.md` for the full state-collapse rationale.

use std::cell::RefCell;
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Arc;

use crate::gc::{MagrGC, RcMagrGC};
use crate::metadata::lazy_loader::{LazyLoader, ZpkgCandidate};
use crate::metadata::{Function, TypeDesc, Value};

/// Runtime-mutable state shared across one VM instance's interp + JIT paths.
///
/// All `RefCell` fields take `&self` so JIT extern-C call sites (which reach
/// the receiver through `*mut VmContext`) can avoid producing `&mut`. The
/// `heap` field is `Box<dyn MagrGC>` without `RefCell` because it is set once
/// in `new()` and never replaced; trait methods take `&self` and the
/// implementation handles its own interior mutability.
pub struct VmContext {
    pub(crate) static_fields:     RefCell<HashMap<String, Value>>,
    pub(crate) pending_exception: RefCell<Option<Value>>,
    pub(crate) lazy_loader:       RefCell<Option<LazyLoader>>,
    pub(crate) heap:              Box<dyn MagrGC>,
}

impl Default for VmContext {
    fn default() -> Self { Self::new() }
}

impl VmContext {
    pub fn new() -> Self {
        Self {
            static_fields:     RefCell::new(HashMap::new()),
            pending_exception: RefCell::new(None),
            lazy_loader:       RefCell::new(None),
            heap:              Box::new(RcMagrGC::new()),
        }
    }

    // ── GC heap ───────────────────────────────────────────────────────────

    /// Borrow the GC heap as a trait object. All script-driven allocations go
    /// through this entry point; see `docs/design/vm-architecture.md` "GC 子系统".
    pub fn heap(&self) -> &dyn MagrGC {
        self.heap.as_ref()
    }

    // ── Static fields ─────────────────────────────────────────────────────

    /// Read a user-class static field. Unset fields read as `Value::Null`.
    pub fn static_get(&self, field: &str) -> Value {
        self.static_fields
            .borrow()
            .get(field)
            .cloned()
            .unwrap_or(Value::Null)
    }

    /// Write a user-class static field.
    pub fn static_set(&self, field: &str, val: Value) {
        self.static_fields.borrow_mut().insert(field.to_string(), val);
    }

    /// Drop all static fields (used by `run_with_static_init` to ensure a
    /// clean slate before each entry-point run).
    pub fn static_fields_clear(&self) {
        self.static_fields.borrow_mut().clear();
    }

    // ── JIT exception bridge ──────────────────────────────────────────────

    /// JIT helpers store a thrown user value here; the JIT entry sees the
    /// `extern "C"` return code = 1 and pulls the value via
    /// `take_exception()` to propagate as `ExecOutcome::Thrown`.
    pub fn set_exception(&self, val: Value) {
        *self.pending_exception.borrow_mut() = Some(val);
    }

    /// Pop the pending exception (called once per `extern "C"` failure).
    pub fn take_exception(&self) -> Option<Value> {
        self.pending_exception.borrow_mut().take()
    }

    // ── Lazy loader (delegates to LazyLoader struct) ─────────────────────

    /// Install with no declared dependencies — for tests / single-file
    /// scripts without stdlib references.
    pub fn install_lazy_loader(&self, libs_dir: Option<PathBuf>, main_pool_len: usize) {
        self.install_lazy_loader_with_deps(libs_dir, main_pool_len, Vec::new(), Vec::new());
    }

    /// Install with declared deps (see `LazyLoader::new` for parameter docs).
    pub fn install_lazy_loader_with_deps(
        &self,
        libs_dir: Option<PathBuf>,
        main_pool_len: usize,
        declared: Vec<(String, ZpkgCandidate)>,
        initially_loaded: Vec<String>,
    ) {
        *self.lazy_loader.borrow_mut() = Some(LazyLoader::new(
            libs_dir,
            main_pool_len,
            declared,
            initially_loaded,
        ));
    }

    /// Clear the lazy loader (used in tests).
    pub fn uninstall_lazy_loader(&self) {
        *self.lazy_loader.borrow_mut() = None;
    }

    /// Look up a function by FQ name; triggers lazy load if needed.
    pub fn try_lookup_function(&self, func_name: &str) -> Option<Arc<Function>> {
        let mut state = self.lazy_loader.borrow_mut();
        let loader = state.as_mut()?;
        loader.resolve_function(func_name)
    }

    /// Look up a class TypeDesc by FQ name; triggers lazy load if needed.
    pub fn try_lookup_type(&self, class_name: &str) -> Option<Arc<TypeDesc>> {
        let mut state = self.lazy_loader.borrow_mut();
        let loader = state.as_mut()?;
        loader.resolve_type(class_name)
    }

    /// Resolve an "overflow" ConstStr index past the main module's pool.
    pub fn try_lookup_string(&self, absolute_idx: usize) -> Option<String> {
        let state = self.lazy_loader.borrow();
        let loader = state.as_ref()?;
        loader.try_lookup_string(absolute_idx)
    }

    /// All namespaces declared by lazy-loadable zpkgs (for static-init scan).
    pub fn declared_namespaces(&self) -> Vec<String> {
        let state = self.lazy_loader.borrow();
        match state.as_ref() {
            Some(loader) => loader.declared_namespaces(),
            None         => Vec::new(),
        }
    }
}

#[cfg(test)]
#[path = "vm_context_tests.rs"]
mod vm_context_tests;
