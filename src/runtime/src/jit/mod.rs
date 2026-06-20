/// JIT backend — compiles z42 bytecode to native machine code using Cranelift.
///
/// Architecture
/// ------------
/// * `frame.rs`     — JitFrame (register file + var slots) and JitModuleCtx
/// * `helpers/`     — `extern "C"` helper functions called by JIT code, split
///                    by `Instruction` category and registered through
///                    `helpers::registry`. See `helpers/mod.rs` for the list.
/// * `translate.rs` — Cranelift IR translation
/// * `mod.rs`       — top-level compile_module / JitModule::run

mod frame;
pub(crate) mod helpers;
mod translate;
/// JIT↔VM read-only metadata contract — review.md Part 1 P0 / E1.P2
/// Phase 1 (2026-06-02). Compile-time path goes through this trait;
/// helpers still reach Module via raw pointer (Phase 2 territory).
pub(crate) mod vm_interface;

use crate::metadata::Module;
use vm_interface::JitVm;
use anyhow::Result;
use cranelift_codegen::ir::{AbiParam, types};
use cranelift_module::{FuncId, Linkage, Module as CraneliftModule};
use cranelift_jit::{JITBuilder, JITModule};
use crate::vm_context::VmContext;
use frame::{FnEntry, JitFrame, JitModuleCtx};
use helpers::{take_exception_error, JitFn};
use std::collections::HashMap;

// ─── Public API ─────────────────────────────────────────────────────────────

/// A fully compiled z42 module ready for native execution.
pub struct JitModule {
    /// Keeps the JITModule alive so the machine code pages remain valid.
    _jit: JITModule,
    ctx:  Box<JitModuleCtx>,
    // 2026-04-27 fix-static-field-access: removed `name: String` —
    // 之前用来 format `"{name}.__static_init__"`，新版扫描所有
    // `*.__static_init__` 函数，不再需要主模块名。
}

impl JitModule {
    /// Run a specific entry function by name (no static-init).
    ///
    /// `ctx` is the canonical state holder; we wire its raw pointer into
    /// `JitModuleCtx.vm_ctx` for the duration of this call so JIT helpers
    /// (which receive `*const JitModuleCtx`) can reach VmContext through it.
    pub fn run_fn(&mut self, ctx: &VmContext, entry_name: &str) -> Result<()> {
        let entry = match self.ctx.fn_entries.get(entry_name) {
            Some(e) => e.clone(),
            None => {
                // fix-jit-cross-zpkg-transitive-eager (2026-06-20): the target
                // function was skipped by `compile_module` (it contains an
                // interp-only opcode such as `LoadLocalAddr`) so it has no JIT
                // entry. Run it on the interpreter instead of hard-failing —
                // covers a skipped entry-point or `__static_init__`. The
                // interpreter never re-enters JIT code, so the whole call
                // subtree just runs interpreted. SAFETY: `module` is set in
                // `compile_module` from a `&Module` that outlives the JitModule.
                let module = unsafe { &*self.ctx.module };
                let func = module.func_index.get(entry_name)
                    .and_then(|&idx| module.functions.get(idx))
                    .ok_or_else(|| anyhow::anyhow!("JIT: entry `{}` not found", entry_name))?;
                return match crate::interp::exec_function(ctx, module, func, &[])? {
                    crate::interp::ExecOutcome::Returned(_) => Ok(()),
                    crate::interp::ExecOutcome::Thrown(val) =>
                        Err(anyhow::anyhow!("{}", crate::exception::format_uncaught(&val, module))),
                };
            }
        };
        // Cast `&VmContext` (immutable ref) to a `*mut VmContext` for the
        // JIT ABI. The JIT extern-C bridge expects a `*mut` pointer for
        // historical compatibility (the helper functions reach VmContext
        // through `(*jit_ctx).vm_ctx`), but they only ever call `&self`
        // methods on it. add-vmcontext-registry (2026-05-20) converted
        // the caller signature to `&VmContext`, so the cast goes via
        // `*const _` first to satisfy the strict pointer-cast rules.
        self.ctx.vm_ctx = (ctx as *const VmContext) as *mut VmContext;
        let mut frame = JitFrame::new(entry.max_reg, &[]);
        let f: JitFn = unsafe { std::mem::transmute(entry.ptr) };
        // 2026-05-10 unify-frame-chain: single push enrolling this entry
        // frame's regs / env_arena (GC roots) + name / file (trace) in
        // one VmFrame. Inner JIT calls are wrapped by jit_call / jit_vcall
        // / jit_call_indirect / jit_obj_new / jit_to_str on the same
        // unified API.
        ctx.push_frame(crate::exception::VmFrame::new(
            entry.name.clone(),
            entry.file.clone(),
            &frame.regs as *const _,
            &frame.env_arena as *const _,
        ));
        let r = unsafe { f(&mut frame, &*self.ctx) };
        ctx.pop_frame();
        frame.recycle();
        self.ctx.vm_ctx = std::ptr::null_mut();
        if r != 0 {
            // SAFETY: ctx.module set in compile_module from a &Module that
            // outlives the JitModule (caller-owned). Deref is safe here.
            let module = unsafe { &*self.ctx.module };
            return Err(take_exception_error(ctx, module));
        }
        Ok(())
    }

    /// Run with static initialisation: clears static fields, calls **all**
    /// `*.__static_init__` functions (sorted) — including imported zpkgs —
    /// then calls the given entry function.
    ///
    /// 2026-04-27 fix-static-field-access: 与 interp 的 `run_with_static_init`
    /// 对称修复。修前只跑主模块 init，导入 zpkg（如 z42.math 的
    /// `Std.Math.__static_init__`）虽然 link 但永不被调用。
    pub fn run(&mut self, ctx: &VmContext, entry_name: &str) -> Result<()> {
        ctx.static_fields_clear();

        // Collect all __static_init__ entries; sort by name for determinism.
        // fix-jit-cross-zpkg-transitive-eager (2026-06-20): enumerate from the
        // merged module (not `fn_entries`) so a `__static_init__` that was
        // skipped by `compile_module` (interp-only opcode) is still run — via
        // `run_fn`'s interp fallback. Matches interp's `init_static_fields`,
        // which also scans `module.functions`. SAFETY: see `run_fn`.
        let init_names: Vec<String> = {
            let module = unsafe { &*self.ctx.module };
            let mut v: Vec<String> = module.functions.iter()
                .map(|f| f.name.clone())
                .filter(|n| n.ends_with(".__static_init__"))
                .collect();
            v.sort();
            v
        };

        for init_name in init_names {
            self.run_fn(ctx, &init_name)?;
        }

        self.run_fn(ctx, entry_name)
    }
}

// ─── compile_module ──────────────────────────────────────────────────────────

/// Compile every function in `module` to native code and return a `JitModule`.
pub fn compile_module(module: &Module) -> Result<JitModule> {
    // ── 1. Create JITBuilder and register all helper symbols ─────────────────
    let isa = cranelift_native::builder()
        .map_err(|e| anyhow::anyhow!("native ISA unavailable: {}", e))?
        .finish(cranelift_codegen::settings::Flags::new(
            cranelift_codegen::settings::builder(),
        ))?;

    let mut jit_builder = JITBuilder::with_isa(isa, cranelift_module::default_libcall_names());

    // Single source of truth for the helper set lives in `helpers::registry`.
    // Adding a new helper updates that file + the helper definition file —
    // mod.rs no longer needs to know the helper list.
    helpers::register_symbols(&mut jit_builder);

    let mut jit = JITModule::new(jit_builder);

    // Access Module fields through the `JitVm` trait — review.md Part 1
    // P0 / E1.P2 Phase 1 (2026-06-02). Codifies the read surface that
    // compile-time JIT uses; helpers still reach `Module` via raw pointer
    // (Phase 2 territory). The signature stays `&Module` because the
    // raw-pointer ABI in `JitModuleCtx.module` cannot accept a fat
    // `*const dyn JitVm`.
    let functions = module.functions();

    // ── 2. Pre-compute max_reg for each function ──────────────────────────────
    let max_regs: HashMap<String, usize> = functions.iter()
        .map(|f| (f.name.clone(), translate::max_reg(f)))
        .collect();

    // ── 3. Declare all JIT-translatable z42 functions in Cranelift ───────────
    // fix-jit-cross-zpkg-transitive-eager (2026-06-20): a function containing
    // an opcode the JIT can't translate yet (out/ref params → LoadLocalAddr,
    // native interop → CallNative, …) is skipped here — neither declared nor
    // defined — so the whole-module compile no longer aborts. Skipped functions
    // are absent from `func_ids` / `fn_entries`, so a `Call` to one misses the
    // JIT entry table at runtime and lands on the interp fallback
    // (`jit_call` → `cross_zpkg_via_interp`). `Call` always routes through the
    // `hr_call` helper (never a direct cranelift call), so callers need no
    // special handling.
    let ptr = jit.target_config().pointer_type();
    let mut func_ids: HashMap<String, FuncId> = HashMap::new();
    for func in functions {
        if translate::jit_unsupported_reason(func).is_some() {
            continue;
        }
        let mut sig = jit.make_signature();
        sig.params.push(AbiParam::new(ptr));   // frame *mut JitFrame
        sig.params.push(AbiParam::new(ptr));   // ctx   *const JitModuleCtx
        sig.returns.push(AbiParam::new(types::I8)); // 0 = ok, 1 = exception
        let id = jit.declare_function(&func.name, Linkage::Local, &sig)?;
        func_ids.insert(func.name.clone(), id);
    }

    // ── 4. Declare all helper functions as imports ────────────────────────────
    let helper_ids = helpers::declare_imports(&mut jit)?;

    // ── 5. Translate each JIT-translatable z42 function ──────────────────────
    for func in functions {
        if !func_ids.contains_key(&func.name) {
            continue;  // skipped in step 3 → runs on interp fallback
        }
        let max_r = max_regs[&func.name];
        translate::translate_function(&mut jit, &helper_ids, func, max_r, &func_ids)?;
    }

    // ── 6. Finalise ──────────────────────────────────────────────────────────
    jit.finalize_definitions()?;

    // ── 7. Build fn_entries (by-name) + fn_entries_by_id (by MethodId) ───────
    // The by-id Vec is indexed in `functions` order so `MethodId.0`
    // matches the slot index. The HashMap stays as cross-zpkg lazy fallback.
    let mut fn_entries: HashMap<String, FnEntry> = HashMap::new();
    let mut fn_entries_by_id: Vec<Option<FnEntry>> = Vec::with_capacity(functions.len());
    for func in functions {
        let entry = if let Some(&id) = func_ids.get(&func.name) {
            let ptr_raw = jit.get_finalized_function(id);
            // 2026-05-10 jit-stack-trace: precompute name + file Arcs so
            // jit_call / jit_vcall can push FrameInfo without reverse lookup.
            let file_str: std::sync::Arc<str> = func.line_table().first()
                .and_then(|e| e.file.as_deref())
                .unwrap_or("")
                .into();
            // 1.3 split-debug-symbols Phase 4: precompute signature-decorated
            // frame name (e.g. `Demo.greet(str)`) so JIT push sites get
            // overload-disambiguated traces matching interp behaviour.
            let frame_name: std::sync::Arc<str> =
                std::sync::Arc::from(crate::metadata::bytecode::format_frame_name(func).as_str());
            let e = FnEntry {
                ptr:     ptr_raw as *const u8,
                max_reg: max_regs[&func.name],
                name:    frame_name,
                file:    file_str,
            };
            fn_entries.insert(func.name.clone(), e.clone());
            Some(e)
        } else {
            None
        };
        fn_entries_by_id.push(entry);
    }

    // ── 8. Build JitModuleCtx ─────────────────────────────────────────────────
    // review.md C3 Phase 1 (2026-06-03): copy the pre-interned Arc<str> pool
    // (cheap — `Arc::clone` per slot, no String allocation) into the JIT
    // module ctx so `jit_const_str` later avoids the prior two-alloc path.
    let ctx = Box::new(JitModuleCtx {
        string_pool: module.interned_strings.clone(),
        fn_entries,
        fn_entries_by_id,
        module: module as *const Module,
        // Set by JitModule::run for the duration of an entry call; null
        // outside that window.
        vm_ctx: std::ptr::null_mut(),
    });

    Ok(JitModule { _jit: jit, ctx })
}

// ─── Public entry point called from vm.rs ───────────────────────────────────

/// Called by `Vm::run` when the execution mode is JIT.
///
/// Phase 2 D3+D6 wiring (2026-05-26): records `jit_methods_compiled` +
/// `jit_compile_us_total` counter increments + fires one
/// [`RuntimeEvent::JitModuleCompiled`] event per module compile. Per-
/// function granularity is deferred to a future spec (the granularity
/// trade-off — N events vs 1 event for a module with N functions —
/// favors aggregate for now).
pub fn run(ctx: &VmContext, module: &Module, entry_name: &str) -> Result<()> {
    use std::sync::atomic::Ordering;

    // E1.P2 Phase 1 (2026-06-02): metadata reads routed through `JitVm`.
    let function_count = module.functions().len() as u64;
    let start = std::time::Instant::now();

    let mut jit_module = compile_module(module)?;

    let duration_us = start.elapsed().as_micros() as u64;
    if std::env::var("Z42_JIT_PROFILE").is_ok() {
        eprintln!("[JIT PROFILE] compiled {} functions in {:.3}s",
            function_count, duration_us as f64 / 1_000_000.0);
    }
    ctx.counters().jit_methods_compiled.fetch_add(function_count, Ordering::Relaxed);
    ctx.counters().jit_compile_us_total.fetch_add(duration_us, Ordering::Relaxed);

    ctx.fire_runtime_event(&crate::observer::RuntimeEvent::JitModuleCompiled {
        module_name:    module.module_name().to_string(),
        function_count: function_count as u32,
        duration_us,
    });

    jit_module.run(ctx, entry_name)
}
