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

use crate::metadata::Module;
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
    pub fn run_fn(&mut self, ctx: &mut VmContext, entry_name: &str) -> Result<()> {
        let entry = self.ctx.fn_entries.get(entry_name)
            .ok_or_else(|| anyhow::anyhow!("JIT: entry `{}` not found", entry_name))?;
        self.ctx.vm_ctx = ctx as *mut VmContext;
        let mut frame = JitFrame::new(entry.max_reg, &[]);
        let f: JitFn = unsafe { std::mem::transmute(entry.ptr) };
        // Phase 3f-2 + impl-closure-l3-escape-stack: register both frame.regs
        // 与 frame.env_arena 让 GC 同时扫到 stack closure env。
        ctx.push_frame_state(
            &frame.regs as *const _,
            &frame.env_arena as *const _,
        );
        let r = unsafe { f(&mut frame, &*self.ctx) };
        ctx.pop_frame_regs();
        frame.recycle();
        self.ctx.vm_ctx = std::ptr::null_mut();
        if r != 0 { return Err(take_exception_error(ctx)); }
        Ok(())
    }

    /// Run with static initialisation: clears static fields, calls **all**
    /// `*.__static_init__` functions (sorted) — including imported zpkgs —
    /// then calls the given entry function.
    ///
    /// 2026-04-27 fix-static-field-access: 与 interp 的 `run_with_static_init`
    /// 对称修复。修前只跑主模块 init，导入 zpkg（如 z42.math 的
    /// `Std.Math.__static_init__`）虽然 link 但永不被调用。
    pub fn run(&mut self, ctx: &mut VmContext, entry_name: &str) -> Result<()> {
        ctx.static_fields_clear();

        // Collect all __static_init__ entries; sort by name for determinism.
        let init_names: Vec<String> = {
            let mut v: Vec<&String> = self.ctx.fn_entries.keys()
                .filter(|n| n.ends_with(".__static_init__"))
                .collect();
            v.sort();
            v.into_iter().cloned().collect()
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

    // ── 2. Pre-compute max_reg for each function ──────────────────────────────
    let max_regs: HashMap<String, usize> = module.functions.iter()
        .map(|f| (f.name.clone(), translate::max_reg(f)))
        .collect();

    // ── 3. Declare all z42 functions in Cranelift ────────────────────────────
    let ptr = jit.target_config().pointer_type();
    let mut func_ids: HashMap<String, FuncId> = HashMap::new();
    for func in &module.functions {
        let mut sig = jit.make_signature();
        sig.params.push(AbiParam::new(ptr));   // frame *mut JitFrame
        sig.params.push(AbiParam::new(ptr));   // ctx   *const JitModuleCtx
        sig.returns.push(AbiParam::new(types::I8)); // 0 = ok, 1 = exception
        let id = jit.declare_function(&func.name, Linkage::Local, &sig)?;
        func_ids.insert(func.name.clone(), id);
    }

    // ── 4. Declare all helper functions as imports ────────────────────────────
    let helper_ids = helpers::declare_imports(&mut jit)?;

    // ── 5. Translate each z42 function ───────────────────────────────────────
    for func in &module.functions {
        let max_r = max_regs[&func.name];
        translate::translate_function(&mut jit, &helper_ids, func, max_r, &func_ids)?;
    }

    // ── 6. Finalise ──────────────────────────────────────────────────────────
    jit.finalize_definitions()?;

    // ── 7. Build fn_entries (by-name) + fn_entries_by_id (by MethodId) ───────
    // The by-id Vec is indexed in `module.functions` order so `MethodId.0`
    // matches the slot index. The HashMap stays as cross-zpkg lazy fallback.
    let mut fn_entries: HashMap<String, FnEntry> = HashMap::new();
    let mut fn_entries_by_id: Vec<Option<FnEntry>> = Vec::with_capacity(module.functions.len());
    for func in &module.functions {
        let entry = if let Some(&id) = func_ids.get(&func.name) {
            let ptr_raw = jit.get_finalized_function(id);
            let e = FnEntry {
                ptr:     ptr_raw as *const u8,
                max_reg: max_regs[&func.name],
            };
            fn_entries.insert(func.name.clone(), e);
            Some(e)
        } else {
            None
        };
        fn_entries_by_id.push(entry);
    }

    // ── 8. Build JitModuleCtx ─────────────────────────────────────────────────
    let ctx = Box::new(JitModuleCtx {
        string_pool: module.string_pool.clone(),
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
pub fn run(ctx: &mut VmContext, module: &Module, entry_name: &str) -> Result<()> {
    let mut jit_module = compile_module(module)?;
    jit_module.run(ctx, entry_name)
}
