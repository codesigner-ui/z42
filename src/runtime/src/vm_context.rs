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
//! See `docs/design/vm-architecture.md` "VmContext —— 运行时状态归口" 段 for
//! the full state-collapse rationale.

use std::cell::RefCell;
use std::collections::HashMap;
use std::path::PathBuf;
use std::rc::Rc;
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
///
/// **Phase 3d.1 (2026-04-29)**: `static_fields` / `pending_exception` /
/// `lazy_loader` 改用 `Rc<RefCell<...>>` 包装，让 `RcMagrGC` 的 external
/// root scanner 闭包能 clone Rc 共享访问，从而 mark_reachable_set 把这些
/// 字段持有的 Value 也纳入 GC roots（修复 cycle collector 漏扫 static_fields
/// 导致误清的 bug）。
pub struct VmContext {
    pub(crate) static_fields:     Rc<RefCell<HashMap<String, Value>>>,
    pub(crate) pending_exception: Rc<RefCell<Option<Value>>>,
    pub(crate) lazy_loader:       Rc<RefCell<Option<LazyLoader>>>,
    /// Phase 3f：interp `exec_function` 入口把当前 frame 的 `regs` Vec 指针推入；
    /// external root scanner 遍历这些指针把 frame regs 内的 Value 喂给 cycle
    /// collector mark 阶段。Vec 内的指针是 raw ptr 而非 Rc/Arc —— 因为 frame
    /// 本身在 Rust 栈，pointer 仅在对应 `exec_function` 调用栈期间有效，由
    /// `FrameGuard` RAII 保证 push/pop 严格配对。
    pub(crate) exec_stack:        Rc<RefCell<Vec<*const Vec<Value>>>>,
    /// 2026-05-02 impl-closure-l3-escape-stack: 与 exec_stack 平行的 stack，
    /// 持每个活动 frame 的 env_arena pointer，让 GC root scanner 把 stack closure
    /// env 中的 Value 一并 mark。push/pop 与 exec_stack 严格成对（同一 frame 同时 push
    /// regs + env_arena）。SAFETY 与 exec_stack 一致：raw ptr 由 FrameGuard RAII 保证
    /// 在 frame 还活时有效。frame 不持 stack closure 时这里 push null pointer。
    pub(crate) env_arena_stack:   Rc<RefCell<Vec<*const Vec<Vec<Value>>>>>,
    /// 2026-05-02 add-method-group-conversion (D1b): module-level FuncRef cache
    /// slots. `LoadFnCached { slot_id }` 首次执行时把 `Value::FuncRef(name)`
    /// 写入 `func_ref_slots[slot_id]`；后续命中直接 load。Slot id 在
    /// `merge_modules` 阶段已 remap 到全局 index space。
    pub(crate) func_ref_slots:    Rc<RefCell<Vec<Value>>>,
    pub(crate) heap:              Box<dyn MagrGC>,

    /// Native interop Tier 1 — registered native types keyed by
    /// `(module_name, type_name)`. Filled by `z42_register_type`; queried by
    /// `CallNative` IR dispatch and `z42_resolve_type`. Per-VM isolated so
    /// multi-VM tests stay independent. See spec C2 (`impl-tier1-c-abi`).
    pub(crate) native_types:      Rc<RefCell<HashMap<(String, String), Rc<crate::native::RegisteredType>>>>,
    /// Loaded native libraries kept alive for the VM's lifetime so that
    /// function pointers stored in `native_types` stay valid until VM drop.
    pub(crate) native_libs:       Rc<RefCell<Vec<libloading::Library>>>,

    /// Spec C10 — owned byte buffers backing `Value::PinnedView` instances
    /// produced by pinning a `Value::Array<u8>`. Keyed by the buffer's data
    /// pointer (`Box::as_ptr() as u64`) so `UnpinPtr` can find and drop
    /// the right entry. Empty for `Str`-source pins (those borrow from the
    /// source `String` and need no extra storage).
    pub(crate) pinned_owned_buffers: Rc<RefCell<HashMap<u64, Box<[u8]>>>>,
}

impl Default for VmContext {
    fn default() -> Self { Self::new() }
}

impl VmContext {
    pub fn new() -> Self {
        let static_fields     = Rc::new(RefCell::new(HashMap::new()));
        let pending_exception = Rc::new(RefCell::new(None));
        let lazy_loader       = Rc::new(RefCell::new(None));
        let exec_stack: Rc<RefCell<Vec<*const Vec<Value>>>> = Rc::new(RefCell::new(Vec::new()));
        let env_arena_stack: Rc<RefCell<Vec<*const Vec<Vec<Value>>>>> = Rc::new(RefCell::new(Vec::new()));
        let func_ref_slots: Rc<RefCell<Vec<Value>>> = Rc::new(RefCell::new(Vec::new()));

        // Phase 3d.1 + 3f: 注入 external root scanner 闭包，让 cycle collector mark
        // 阶段把 static_fields + pending_exception + interp exec_stack frame regs
        // 持的 Value 也作为 roots 扫描。闭包通过 Rc clone 与 VmContext 字段共享
        // ownership（无 lifetime 牵扯）。
        let heap = RcMagrGC::new();
        {
            let sf = static_fields.clone();
            let pe = pending_exception.clone();
            let es = exec_stack.clone();
            let eas = env_arena_stack.clone();
            let frs = func_ref_slots.clone();
            heap.set_external_root_scanner(Box::new(move |visit| {
                // 1. static_fields
                for v in sf.borrow().values() {
                    visit(v);
                }
                // 2. pending_exception
                if let Some(v) = pe.borrow().as_ref() {
                    visit(v);
                }
                // 3. interp exec_stack frame regs（Phase 3f）
                //
                // SAFETY: exec_stack 中每个 raw ptr 都对应一个仍在 Rust 栈
                // 的 `Frame.regs` Vec。push 在 `interp::exec_function` 入口
                // 由 `FrameGuard` RAII 保证 pop 在函数返回（含 panic 展开 / `?`
                // early return）时配对完成。GC 在 z42 脚本调用 collect 时触发，
                // 必然在某个 exec_function 内（脚本帧仍活），所有 ptr 有效。
                for &regs_ptr in es.borrow().iter() {
                    unsafe {
                        for v in (*regs_ptr).iter() {
                            visit(v);
                        }
                    }
                }
                // 4. interp/JIT env_arena —— stack closure 的 captured env Value
                // （impl-closure-l3-escape-stack）。null 指针表示该 frame 没有
                // 任何 stack closure，跳过。
                for &arena_ptr in eas.borrow().iter() {
                    if arena_ptr.is_null() { continue; }
                    unsafe {
                        for env in (*arena_ptr).iter() {
                            for v in env.iter() {
                                visit(v);
                            }
                        }
                    }
                }
                // 5. method group conversion cache slots（D1b）
                for v in frs.borrow().iter() {
                    visit(v);
                }
            }));
        }

        Self {
            static_fields,
            pending_exception,
            lazy_loader,
            exec_stack,
            env_arena_stack,
            func_ref_slots,
            heap: Box::new(heap),
            native_types: Rc::new(RefCell::new(HashMap::new())),
            native_libs:  Rc::new(RefCell::new(Vec::new())),
            pinned_owned_buffers: Rc::new(RefCell::new(HashMap::new())),
        }
    }

    // ── Native interop (Tier 1, spec C2) ──────────────────────────────────

    /// Register a native type with this VM. Returns `false` (with [`crate::native::error::set`]
    /// already populated) on duplicate `(module, name)`. Internally invoked
    /// from `z42_register_type`; tests may also call this directly with a
    /// pre-built [`RegisteredType`].
    pub fn register_native_type(
        &self,
        ty: Rc<crate::native::RegisteredType>,
    ) -> bool {
        let key = (ty.module().to_string(), ty.type_name().to_string());
        let mut map = self.native_types.borrow_mut();
        if map.contains_key(&key) {
            return false;
        }
        map.insert(key, ty);
        true
    }

    /// Look up a previously registered native type. Returns `None` when the
    /// `(module, name)` pair is unknown.
    pub fn resolve_native_type(
        &self,
        module: &str,
        name: &str,
    ) -> Option<Rc<crate::native::RegisteredType>> {
        let key = (module.to_string(), name.to_string());
        self.native_types.borrow().get(&key).cloned()
    }

    /// Total number of registered native types — primarily for tests.
    pub fn native_type_count(&self) -> usize {
        self.native_types.borrow().len()
    }

    // ── Pinned owned buffers (spec C10 — Array<u8> pin) ──────────────────

    /// Register an owned byte buffer (the snapshot of an `Array<u8>` taken
    /// during `PinPtr`). Returns the buffer's data pointer for storage in
    /// the `Value::PinnedView`. The buffer remains alive until
    /// [`release_owned_buffer`] is called from a matching `UnpinPtr`.
    pub fn pin_owned_buffer(&self, buf: Box<[u8]>) -> u64 {
        let ptr = buf.as_ptr() as u64;
        self.pinned_owned_buffers.borrow_mut().insert(ptr, buf);
        ptr
    }

    /// Drop an owned buffer previously registered via [`pin_owned_buffer`].
    /// Idempotent: silently no-ops if `ptr` isn't registered (e.g. `Str`
    /// pins which never enter the table).
    pub fn release_owned_buffer(&self, ptr: u64) {
        let _ = self.pinned_owned_buffers.borrow_mut().remove(&ptr);
    }

    /// Total number of currently-pinned owned buffers — exposed for
    /// tests asserting that UnpinPtr cleaned up.
    pub fn pinned_owned_buffer_count(&self) -> usize {
        self.pinned_owned_buffers.borrow().len()
    }

    /// Load a native library and invoke its `<basename>_register` entry point.
    /// The library handle is stored on `self` until VM drop. Errors are
    /// returned as `anyhow::Error` and mirrored into the thread-local
    /// last-error slot so C callers see the same diagnostic via
    /// [`z42_last_error`](z42_abi::z42_last_error).
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

    /// 2026-05-02 impl-closure-l3-escape-stack: push frame regs + env_arena
    /// pointers atomically. 调用方需保证 raw ptr 在配对的 pop_frame_regs 之前
    /// 不失效（典型由 `FrameGuard` RAII 保证）。env_arena 可以是 null（frame
    /// 不持 stack closure）或指向 `Vec<Vec<Value>>` 的有效地址。
    /// 替代了 Phase 3f 的 push_frame_regs（仅 regs，单独存在的 API 已被废弃）。
    pub(crate) fn push_frame_state(
        &self,
        regs: *const Vec<Value>,
        env_arena: *const Vec<Vec<Value>>,
    ) {
        self.exec_stack.borrow_mut().push(regs);
        self.env_arena_stack.borrow_mut().push(env_arena);
    }

    /// Pop the most recently pushed frame regs pointer. No-op if empty
    /// (defensive; should not happen with correct RAII pairing). 同时弹出
    /// env_arena_stack 顶（与 push 1:1）。
    pub(crate) fn pop_frame_regs(&self) {
        self.exec_stack.borrow_mut().pop();
        self.env_arena_stack.borrow_mut().pop();
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
