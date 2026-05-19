//! `VmContext` — runtime-mutable state for one VM instance.
//!
//! Single canonical owner of all per-VM mutable state. Replaces the historical
//! `thread_local!` constellation under `interp/` + `jit/` (consolidate-vm-state,
//! 2026-04-28). Fields:
//!
//! - **`static_fields`** — user-class static field storage
//! - **`pending_exception`** — JIT extern-C exception ABI bridge slot
//! - **`lazy_loader`** — on-demand zpkg loader registry
//! - **`exec_stack`** — interp/JIT frame.regs raw pointers (Phase 3f / 3f-2 GC roots)
//! - **`heap`** — `Box<dyn MagrGC>` GC subsystem (default `RcMagrGC`)
//! - **`native_types`** / **`native_libs`** — Tier 1 native interop registry (spec C2)
//! - **`pinned_owned_buffers`** — owned byte buffers backing `Value::PinnedView` (spec C4)
//!
//! The only remaining `thread_local!` in the runtime is `jit/frame.rs::FRAME_POOL`
//! (pure allocator cache, not state) and `native/exports.rs::CURRENT_VM` (FFI
//! callback bridge, scoped via `VmGuard` RAII).
//!
//! # Lifecycle
//!
//! ```ignore
//! let mut ctx = VmContext::new();
//! ctx.install_lazy_loader_with_deps(libs_dir, main_pool_len, declared, loaded);
//! Vm::new(module, mode).run(&mut ctx, hint)?;
//! ```
//!
//! # Threading
//!
//! `VmContext` is **not** `Send` / `Sync` (intentionally — `Rc<RefCell<...>>`
//! interiors throughout). One ctx serves one OS thread at a time; multi-threaded
//! VM is a roadmap follow-up.
//!
//! # JIT integration
//!
//! `JitModuleCtx::vm_ctx: *mut VmContext` carries the ctx pointer through the
//! `extern "C"` boundary. The pointer is set by `JitModule::run` for the
//! duration of one entry-point invocation and cleared on return. JIT helpers
//! access fields through `(*jit_ctx).vm_ctx` and call ctx methods.
//!
//! See `docs/design/runtime/vm-architecture.md` "VmContext —— 运行时状态归口" 段 for
//! the full state-collapse rationale.

use std::cell::RefCell;
use std::collections::HashMap;
use std::path::PathBuf;
use std::rc::Rc;
use std::sync::Arc;

use parking_lot::{Mutex, RwLock};

use crate::gc::{MagrGC, RcMagrGC};
use crate::metadata::lazy_loader::{LazyLoader, ZpkgCandidate};
use crate::metadata::{Function, TypeDesc, Value};

/// **`VmCore`** —— state shared across all threads sharing one VM instance
/// (add-multithreading-foundation, 2026-05-19, Phase 1 / spec
/// `2026-05-19-add-multithreading-foundation`).
///
/// Holds fields that are **process-globally singular**: static fields, type
/// registries, native interop registry, pinned buffer table, etc. Per-thread
/// state (call stack, pending exception, frame guards, func-ref cache slots)
/// stays directly on [`VmContext`], which references this struct through
/// `Arc<VmCore>`.
///
/// Phase 1 (this commit): only `static_fields` + `static_field_index` are
/// here. Subsequent phases move `lazy_loader` / `native_types` /
/// `native_libs` / `pinned_owned_buffers` / `processes` / `gc heap` in.
///
/// `VmCore` will become `Send + Sync` once `GcRef<T>` backing switches to
/// `Arc<...>` (Phase 3 of the spec). Until then, `Mutex` is wrapped around
/// fields that hold `Value` (which transitively contains `Rc<RefCell>` via
/// the current `GcRef` backing), so the API surface is already stable —
/// Send-safety completes at Phase 3 without further VmCore API changes.
pub struct VmCore {
    /// Static field storage indexed by `StaticFieldId.0` (introduce-method-token,
    /// 2026-05-08). Slot 0 reserved? No; ids start at 0 (allocation order).
    pub(crate) static_fields:      Mutex<Vec<Value>>,
    /// FQN → slot id map. Lazy-allocated on first access; cross-zpkg lazy
    /// fields can be encountered in any order.
    pub(crate) static_field_index: Mutex<HashMap<String, u32>>,
    /// On-demand zpkg loader. `None` until `install_lazy_loader[_with_deps]`
    /// is called (typically from `bootstrap.rs`). Shared across threads
    /// since zpkg resolution + module loading is a process-global operation.
    pub(crate) lazy_loader:        Mutex<Option<LazyLoader>>,
    /// Native interop Tier 1 — registered native types keyed by `(module, type)`.
    /// **`RwLock`** (Decision 6): read-mostly path — `CallNative` dispatch /
    /// `z42_resolve_type` are pure reads, writes only happen during module
    /// load / `z42_register_type`. Concurrent reads from multiple threads
    /// not serialized.
    #[cfg(feature = "native-interop")]
    pub(crate) native_types:       RwLock<HashMap<(String, String), Rc<crate::native::RegisteredType>>>,
    /// Loaded native libraries kept alive for VM lifetime so function
    /// pointers stored in `native_types` stay valid. Lock contention low
    /// (only `dlopen` adds entries) → plain Mutex.
    #[cfg(feature = "native-interop")]
    pub(crate) native_libs:        Mutex<Vec<libloading::Library>>,
    /// Spec C10 — owned byte buffers backing `Value::PinnedView` instances.
    /// Keyed by buffer data pointer so `UnpinPtr` can drop the entry.
    pub(crate) pinned_owned_buffers: Mutex<HashMap<u64, Box<[u8]>>>,
}

/// Runtime-mutable state shared across one VM instance's interp + JIT paths.
///
/// All `RefCell` fields take `&self` so JIT extern-C call sites (which reach
/// the receiver through `*mut VmContext`) can avoid producing `&mut`. The
/// `heap` field is `Box<dyn MagrGC>` without `RefCell` because it is set once
/// in `new()` and never replaced; trait methods take `&self` and the
/// implementation handles its own interior mutability.
///
/// **Phase 3d.1 (2026-04-29)**: `static_fields` / `pending_exception` /
/// `lazy_loader` 改用 `Rc<RefCell<...>>` 包装，让 `RcMagrGC` 的 external
/// root scanner 闭包能 clone Rc 共享访问，从而 mark_reachable_set 把这些
/// 字段持有的 Value 也纳入 GC roots（修复 cycle collector 漏扫 static_fields
/// 导致误清的 bug）。
pub struct VmContext {
    /// Shared-across-threads state (add-multithreading-foundation, 2026-05-19).
    /// See [`VmCore`] for the field list and per-phase migration table.
    /// Static fields, type registries, native interop registry etc. accessed
    /// through `self.core.<field>.lock()` (or `.read()` for RwLock variants).
    pub(crate) core: Arc<VmCore>,
    pub(crate) pending_exception: Rc<RefCell<Option<Value>>>,
    // lazy_loader moved to VmCore (Phase 1.6)
    /// 2026-05-10 unify-frame-chain: single source of truth for active
    /// script frames. Each [`crate::exception::VmFrame`] carries the
    /// `(name, file, line, column)` trace metadata **and** raw pointers
    /// to `regs` / `env_arena` for GC root scanning, eliminating the
    /// risk of partial frames (only one of the previously-parallel
    /// `exec_stack` / `env_arena_stack` / `call_stack` getting pushed).
    ///
    /// Raw ptrs valid only while the owning Rust frame
    /// (`interp::Frame` / `JitFrame`) is alive — `FrameGuard` RAII for
    /// interp + paired `push_frame` / `pop_frame` in JIT helpers ensure
    /// the pop runs before the owner returns. GC collect is only invoked
    /// from inside script code, so any walk of this Vec sees pointers
    /// that are still in-bounds.
    pub(crate) call_stack:        Rc<RefCell<Vec<crate::exception::VmFrame>>>,
    /// 2026-05-02 add-method-group-conversion (D1b): module-level FuncRef cache
    /// slots. `LoadFnCached { slot_id }` 首次执行时把 `Value::FuncRef(name)`
    /// 写入 `func_ref_slots[slot_id]`；后续命中直接 load。Slot id 在
    /// `merge_modules` 阶段已 remap 到全局 index space。
    pub(crate) func_ref_slots:    Rc<RefCell<Vec<Value>>>,
    pub(crate) heap:              Box<dyn MagrGC>,

    // native_types / native_libs / pinned_owned_buffers moved to VmCore (Phase 1.7-1.9)

    /// add-std-process (2026-05-13) — live `Std.IO.Process` children
    /// spawned via `__process_spawn`. Keyed by a monotonic u64 slot id
    /// that z42 `ProcessHandle` carries. Slot is removed (`take_*`) on
    /// `wait` / `kill`+reap / explicit `drop`. Single counter never
    /// reused (u64 is effectively unbounded), so no generation field.
    pub(crate) processes:         Rc<RefCell<HashMap<u64, crate::corelib::process::ProcessSlot>>>,
    pub(crate) process_next_id:   std::cell::Cell<u64>,
}

impl Default for VmContext {
    fn default() -> Self { Self::new() }
}

impl VmContext {
    pub fn new() -> Self {
        let core: Arc<VmCore> = Arc::new(VmCore {
            static_fields:      Mutex::new(Vec::new()),
            static_field_index: Mutex::new(HashMap::new()),
            lazy_loader:        Mutex::new(None),
            #[cfg(feature = "native-interop")]
            native_types:       RwLock::new(HashMap::new()),
            #[cfg(feature = "native-interop")]
            native_libs:        Mutex::new(Vec::new()),
            pinned_owned_buffers: Mutex::new(HashMap::new()),
        });
        let pending_exception = Rc::new(RefCell::new(None));
        let func_ref_slots: Rc<RefCell<Vec<Value>>> = Rc::new(RefCell::new(Vec::new()));
        // 2026-05-10 unify-frame-chain: single Vec<VmFrame> replaces the
        // previous trio (exec_stack / env_arena_stack / call_stack).
        let call_stack: Rc<RefCell<Vec<crate::exception::VmFrame>>> = Rc::new(RefCell::new(Vec::new()));

        // External GC root scanner — invoked by the cycle collector during
        // mark phase. Walks all out-of-heap Value sources so cycles whose
        // only roots are static fields / pending exception / live frame
        // regs / stack closure envs / func-ref slots stay alive.
        let heap = RcMagrGC::new();
        {
            let core_clone = Arc::clone(&core);
            let pe  = pending_exception.clone();
            let cs  = call_stack.clone();
            let frs = func_ref_slots.clone();
            heap.set_external_root_scanner(Box::new(move |visit| {
                // 1. static_fields (via VmCore)
                for v in core_clone.static_fields.lock().iter() {
                    visit(v);
                }
                // 2. pending_exception
                if let Some(v) = pe.borrow().as_ref() {
                    visit(v);
                }
                // 3. live z42 frame state — unified per VmFrame entry.
                //
                // SAFETY: `regs` / `env_arena` raw ptrs are valid for the
                // lifetime of the owning interp `Frame` / `JitFrame`,
                // popped by RAII / paired helper exits before the owner
                // returns. GC collect is invoked from inside script code,
                // so every walk sees pointers still in-bounds.
                for frame in cs.borrow().iter() {
                    unsafe {
                        for v in (*frame.regs).iter() {
                            visit(v);
                        }
                        if !frame.env_arena.is_null() {
                            for env in (*frame.env_arena).iter() {
                                for v in env.iter() {
                                    visit(v);
                                }
                            }
                        }
                    }
                }
                // 4. method group conversion cache slots (D1b).
                for v in frs.borrow().iter() {
                    visit(v);
                }
            }));
        }

        Self {
            core,
            pending_exception,
            call_stack,
            func_ref_slots,
            heap: Box::new(heap),
            processes:       Rc::new(RefCell::new(HashMap::new())),
            process_next_id: std::cell::Cell::new(1),
        }
    }

    // ── Process slot table (add-std-process, 2026-05-13) ──────────────────

    /// Allocate a new slot id and store `slot` under it. Returns the id
    /// for the z42 `ProcessHandle` to carry. Counter is monotonic and
    /// never reused; u64 overflow is not a practical concern (10^19
    /// spawns).
    pub fn alloc_process_slot(&self, slot: crate::corelib::process::ProcessSlot) -> u64 {
        let id = self.process_next_id.get();
        self.process_next_id.set(id + 1);
        self.processes.borrow_mut().insert(id, slot);
        id
    }

    /// Remove and return the slot. Used by `wait` / `kill`+reap / `drop`
    /// which take ownership of `child` etc.
    pub fn take_process_slot(&self, slot_id: u64) -> Option<crate::corelib::process::ProcessSlot> {
        self.processes.borrow_mut().remove(&slot_id)
    }

    /// Peek at the slot in-place. Returns `None` if the slot id is
    /// unknown. Callback runs while the outer `RefCell` is borrowed —
    /// callers must not invoke other slot methods inside.
    pub fn with_process_slot<T>(
        &self,
        slot_id: u64,
        f: impl FnOnce(&mut crate::corelib::process::ProcessSlot) -> T,
    ) -> Option<T> {
        let mut map = self.processes.borrow_mut();
        map.get_mut(&slot_id).map(f)
    }

    /// Number of currently allocated process slots. Used by tests to
    /// verify cleanup paths drop entries.
    pub fn process_slot_count(&self) -> usize {
        self.processes.borrow().len()
    }

    // ── Native interop (Tier 1, spec C2) ──────────────────────────────────
    //
    // 2026-05-12 add-platform-wasm Stage 0: entire interop API gated on
    // `native-interop` feature. wasm builds drop these methods (and the
    // backing fields) entirely.

    /// Register a native type with this VM. Returns `false` (with [`crate::native::error::set`]
    /// already populated) on duplicate `(module, name)`. Internally invoked
    /// from `z42_register_type`; tests may also call this directly with a
    /// pre-built [`RegisteredType`].
    #[cfg(feature = "native-interop")]
    pub fn register_native_type(
        &self,
        ty: Rc<crate::native::RegisteredType>,
    ) -> bool {
        let key = (ty.module().to_string(), ty.type_name().to_string());
        let mut map = self.core.native_types.write();
        if map.contains_key(&key) {
            return false;
        }
        map.insert(key, ty);
        true
    }

    /// Look up a previously registered native type. Returns `None` when the
    /// `(module, name)` pair is unknown.
    #[cfg(feature = "native-interop")]
    pub fn resolve_native_type(
        &self,
        module: &str,
        name: &str,
    ) -> Option<Rc<crate::native::RegisteredType>> {
        let key = (module.to_string(), name.to_string());
        self.core.native_types.read().get(&key).cloned()
    }

    /// Total number of registered native types — primarily for tests.
    #[cfg(feature = "native-interop")]
    pub fn native_type_count(&self) -> usize {
        self.core.native_types.read().len()
    }

    // ── Pinned owned buffers (spec C10 — Array<u8> pin) ──────────────────

    /// Register an owned byte buffer (the snapshot of an `Array<u8>` taken
    /// during `PinPtr`). Returns the buffer's data pointer for storage in
    /// the `Value::PinnedView`. The buffer remains alive until
    /// [`release_owned_buffer`] is called from a matching `UnpinPtr`.
    pub fn pin_owned_buffer(&self, buf: Box<[u8]>) -> u64 {
        let ptr = buf.as_ptr() as u64;
        self.core.pinned_owned_buffers.lock().insert(ptr, buf);
        ptr
    }

    /// Drop an owned buffer previously registered via [`pin_owned_buffer`].
    /// Idempotent: silently no-ops if `ptr` isn't registered (e.g. `Str`
    /// pins which never enter the table).
    pub fn release_owned_buffer(&self, ptr: u64) {
        let _ = self.core.pinned_owned_buffers.lock().remove(&ptr);
    }

    /// Total number of currently-pinned owned buffers — exposed for
    /// tests asserting that UnpinPtr cleaned up.
    pub fn pinned_owned_buffer_count(&self) -> usize {
        self.core.pinned_owned_buffers.lock().len()
    }

    /// Load a native library and invoke its `<basename>_register` entry point.
    /// The library handle is stored on `self` until VM drop. Errors are
    /// returned as `anyhow::Error` and mirrored into the thread-local
    /// last-error slot so C callers see the same diagnostic via
    /// [`z42_last_error`](z42_abi::z42_last_error).
    #[cfg(feature = "native-interop")]
    pub fn load_native_library(
        &self,
        path: impl AsRef<std::path::Path>,
    ) -> anyhow::Result<()> {
        crate::native::loader::load_library(self, path.as_ref())
    }

    // ── Interp exec stack（Phase 3f） ─────────────────────────────────────

    /// Push current frame's regs pointer onto exec_stack, used by GC root
    /// scanning. Caller must guarantee pointer stays valid until matching
    /// `pop_frame_regs()` (typically via `FrameGuard` RAII).
    /// 2026-05-02 add-method-group-conversion (D1b): ensure VmContext has at
    /// least `n` FuncRef cache slots allocated. Idempotent — only grows.
    pub fn alloc_func_ref_slots(&self, n: u32) {
        let mut s = self.func_ref_slots.borrow_mut();
        if s.len() < n as usize {
            s.resize(n as usize, Value::Null);
        }
    }

    /// LoadFnCached read: returns slot value, or `Value::Null` if uninitialised
    /// (caller's responsibility to fill on first miss). Bounds-checked.
    pub(crate) fn func_ref_slot(&self, idx: u32) -> Value {
        self.func_ref_slots
            .borrow()
            .get(idx as usize)
            .cloned()
            .unwrap_or(Value::Null)
    }

    /// LoadFnCached write: store a `Value::FuncRef` into the slot for future hits.
    pub(crate) fn set_func_ref_slot(&self, idx: u32, value: Value) {
        let mut s = self.func_ref_slots.borrow_mut();
        if (idx as usize) >= s.len() {
            s.resize((idx as usize) + 1, Value::Null);
        }
        s[idx as usize] = value;
    }

    // ── Frame chain (2026-05-10 unify-frame-chain) ────────────────────────
    //
    // Single push_frame / pop_frame replaces the previously-separate
    // (push_frame_state / pop_frame_regs) + (push_call_frame / pop_call_frame)
    // pairs. Atomic push of one VmFrame holds GC roots + trace metadata
    // together — caller cannot "forget half".

    /// Push one [`crate::exception::VmFrame`] onto the active script frame
    /// chain. Pop is the caller's responsibility (typically via the
    /// interp `FrameGuard` RAII or the explicit pair in JIT helpers).
    pub(crate) fn push_frame(&self, frame: crate::exception::VmFrame) {
        self.call_stack.borrow_mut().push(frame);
    }

    /// Pop the most recently pushed frame. No-op when empty (defensive).
    pub(crate) fn pop_frame(&self) {
        self.call_stack.borrow_mut().pop();
    }

    /// Update the *top* (currently executing) frame's source position.
    /// Called by callers right before they invoke a callee, so the snapshot
    /// at a downstream `throw` shows the call site, not 0.
    ///
    /// `column = 0` means unknown — the snapshot formats as `(file:line)`
    /// rather than `(file:line:col)`.
    pub(crate) fn update_top_frame_pos(&self, line: u32, column: u32) {
        if let Some(top) = self.call_stack.borrow().last() {
            top.line.set(line);
            top.column.set(column);
        }
    }

    /// Snapshot the entire call stack for stack-trace formatting at a
    /// `throw` site. Cheap clone (small-string + u32 per frame); only
    /// invoked on the throw path so per-instruction overhead is zero.
    pub(crate) fn snapshot_call_stack(&self) -> Vec<crate::exception::FrameSnapshot> {
        self.call_stack.borrow().iter().map(|f| f.snapshot()).collect()
    }

    /// Current depth of the call stack — debugging / tests.
    #[cfg(test)]
    pub(crate) fn call_stack_depth(&self) -> usize {
        self.call_stack.borrow().len()
    }

    /// Spec impl-ref-out-in-runtime (Decision R1): index into the frame
    /// chain and return a raw pointer to that frame's `regs` Vec.
    /// Used by `Value::Ref { kind: RefKind::Stack { frame_idx, .. } }`
    /// transparent deref in `Frame::get/set`.
    ///
    /// # Safety
    /// Caller must:
    ///   1. Use the returned pointer only while the corresponding frame is
    ///      still alive (guaranteed by spec design Decision 9: refs never
    ///      escape the call stack — popped frames cannot be referenced).
    ///   2. Not race with concurrent push/pop on the same VmContext (single
    ///      RefCell borrow boundary; deref is synchronous within a frame).
    pub(crate) fn frame_state_at(&self, idx: usize) -> Option<*const Vec<Value>> {
        let stack = self.call_stack.borrow();
        stack.get(idx).map(|f| f.regs)
    }

    /// Current depth of the frame chain. `frame_state_at(depth - 1)` is
    /// the most recent frame. Used by codegen-generated `LoadLocalAddr`
    /// to produce a `RefKind::Stack { frame_idx }` referencing the
    /// current frame at emission time.
    pub(crate) fn frame_stack_depth(&self) -> usize {
        self.call_stack.borrow().len()
    }

    // ── GC heap ───────────────────────────────────────────────────────────

    /// Borrow the GC heap as a trait object. All script-driven allocations go
    /// through this entry point; see `docs/design/runtime/vm-architecture.md` "GC 子系统".
    pub fn heap(&self) -> &dyn MagrGC {
        self.heap.as_ref()
    }

    // ── Static fields ─────────────────────────────────────────────────────
    //
    // Layout (introduce-method-token, 2026-05-08):
    //   `static_fields: Vec<Value>`           — slot storage by StaticFieldId.0
    //   `static_field_index: HashMap<&str, u32>` — name → id (lazy-allocated)
    //
    // The legacy by-name `static_get` / `static_set` API lazy-allocates an
    // ID on first write and looks up by name on read (returning Null if no
    // ID is yet allocated). The dispatch hot path uses `static_get_by_id`
    // / `static_set_by_id` after `metadata::resolver` has populated
    // per-instruction `StaticFieldId` cache slots.

    /// Resolve (or lazy-allocate) a `StaticFieldId` for the given full-qualified
    /// static-field name. Idempotent. Called by `metadata::resolver` at module
    /// load and by hot paths on cache miss (cross-zpkg lazy fields).
    pub fn resolve_static_field_id(&self, name: &str) -> crate::metadata::tokens::StaticFieldId {
        let mut idx = self.core.static_field_index.lock();
        if let Some(&id) = idx.get(name) {
            return crate::metadata::tokens::StaticFieldId(id);
        }
        let id = idx.len() as u32;
        idx.insert(name.to_string(), id);
        // Extend backing Vec to match index.
        let mut sf = self.core.static_fields.lock();
        if (id as usize) >= sf.len() {
            sf.resize_with((id + 1) as usize, || Value::Null);
        }
        crate::metadata::tokens::StaticFieldId(id)
    }

    /// Read a user-class static field by name. Unset fields read as
    /// `Value::Null`. Lazy fallback for cross-zpkg paths and JIT helpers
    /// not yet threading `StaticFieldId`.
    pub fn static_get(&self, field: &str) -> Value {
        let idx = self.core.static_field_index.lock();
        match idx.get(field) {
            Some(&id) => self
                .core
                .static_fields
                .lock()
                .get(id as usize)
                .cloned()
                .unwrap_or(Value::Null),
            None => Value::Null,
        }
    }

    /// Write a user-class static field by name. Lazy-allocates the id on
    /// first write.
    pub fn static_set(&self, field: &str, val: Value) {
        let id = self.resolve_static_field_id(field);
        self.static_set_by_id(id, val);
    }

    /// Hot-path read by id (no hash). Caller must have a resolved id.
    /// Returns `Value::Null` if id ≥ Vec length (unallocated slot).
    #[inline]
    pub fn static_get_by_id(&self, id: crate::metadata::tokens::StaticFieldId) -> Value {
        self.core
            .static_fields
            .lock()
            .get(id.0 as usize)
            .cloned()
            .unwrap_or(Value::Null)
    }

    /// Hot-path write by id. Caller must have a resolved id; the slot
    /// is auto-extended if id ≥ current Vec length.
    #[inline]
    pub fn static_set_by_id(&self, id: crate::metadata::tokens::StaticFieldId, val: Value) {
        let mut sf = self.core.static_fields.lock();
        if (id.0 as usize) >= sf.len() {
            sf.resize_with((id.0 + 1) as usize, || Value::Null);
        }
        sf[id.0 as usize] = val;
    }

    /// Drop all static fields (used by `run_with_static_init` to ensure a
    /// clean slate before each entry-point run). Resets values to Null but
    /// **keeps the index** so previously-allocated `StaticFieldId`s stay
    /// stable across runs (resolver-populated IDs in `Function.resolved`
    /// remain valid after re-init).
    pub fn static_fields_clear(&self) {
        let mut sf = self.core.static_fields.lock();
        for slot in sf.iter_mut() {
            *slot = Value::Null;
        }
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

    /// Peek at the pending exception without removing it. Used by JIT catch-type
    /// dispatch (catch-by-generic-type, 2026-05-06): the throw helper has set the
    /// exception, the dispatch helper inspects its class to decide which catch
    /// handler to jump to, and a later `take_exception` (via `jit_install_catch`)
    /// hands the value to the chosen catch register.
    pub fn peek_exception(&self) -> Option<Value> {
        self.pending_exception.borrow().clone()
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
        *self.core.lazy_loader.lock() = Some(LazyLoader::new(
            libs_dir,
            main_pool_len,
            declared,
            initially_loaded,
        ));
    }

    /// Clear the lazy loader (used in tests).
    pub fn uninstall_lazy_loader(&self) {
        *self.core.lazy_loader.lock() = None;
    }

    /// fix-cross-pkg-subclass-fields (2026-05-14): seed the lazy loader's
    /// `type_registry` with TypeDescs from eagerly-loaded modules (e.g. the
    /// merged main module). Used by both the z42vm CLI and the in-process
    /// test runner immediately after `install_lazy_loader_with_deps` so the
    /// fixup pass can find eagerly-loaded base classes when lazy-loading a
    /// subclass.
    pub fn seed_lazy_loader_types(&self, types: &HashMap<String, Arc<TypeDesc>>) {
        let mut state = self.core.lazy_loader.lock();
        if let Some(loader) = state.as_mut() {
            loader.seed_types_for_lookup(types);
        }
    }

    /// Look up a function by FQ name; triggers lazy load if needed.
    pub fn try_lookup_function(&self, func_name: &str) -> Option<Arc<Function>> {
        let mut state = self.core.lazy_loader.lock();
        let loader = state.as_mut()?;
        loader.resolve_function(func_name)
    }

    /// Look up a class TypeDesc by FQ name; triggers lazy load if needed.
    pub fn try_lookup_type(&self, class_name: &str) -> Option<Arc<TypeDesc>> {
        let mut state = self.core.lazy_loader.lock();
        let loader = state.as_mut()?;
        loader.resolve_type(class_name)
    }

    /// Resolve an "overflow" ConstStr index past the main module's pool.
    pub fn try_lookup_string(&self, absolute_idx: usize) -> Option<String> {
        let state = self.core.lazy_loader.lock();
        let loader = state.as_ref()?;
        loader.try_lookup_string(absolute_idx)
    }

    /// All namespaces declared by lazy-loadable zpkgs (for static-init scan).
    pub fn declared_namespaces(&self) -> Vec<String> {
        let state = self.core.lazy_loader.lock();
        match state.as_ref() {
            Some(loader) => loader.declared_namespaces(),
            None         => Vec::new(),
        }
    }

    /// Force-load every declared zpkg, then return a sorted list of all
    /// `*.__static_init__` function names across loaded zpkgs.
    ///
    /// fix-multi-file-static-init (2026-05-15): the compiler now emits
    /// `<ns>.<source-stem>.__static_init__` (one per CU). A single
    /// per-namespace lookup can't find them all, so the runtime force-loads
    /// each declared zpkg and enumerates the loader's function table for
    /// the suffix. Sorted for determinism.
    pub fn collect_lazy_static_init_names(&self) -> Vec<String> {
        let mut state = self.core.lazy_loader.lock();
        let Some(loader) = state.as_mut() else { return Vec::new(); };
        loader.force_load_all_declared();
        let mut names: Vec<String> = loader.iter_function_names()
            .filter(|n| n.ends_with(".__static_init__"))
            .cloned()
            .collect();
        names.sort();
        names
    }
}

#[cfg(test)]
#[path = "vm_context_tests.rs"]
mod vm_context_tests;
