/// JIT backend — compiles z42 bytecode to native machine code using Cranelift.
///
/// Architecture
/// ------------
/// * `frame.rs`     — JitFrame (register file + var slots) and JitModuleCtx
/// * `helpers.rs`   — all `extern "C"` helper functions called by JIT code
/// * `translate.rs` — Cranelift IR translation
/// * `mod.rs`       — top-level compile_module / JitModule::run

mod frame;
pub(crate) mod helpers;
mod translate;

use crate::bytecode::Module;
use anyhow::Result;
use cranelift_codegen::ir::{AbiParam, types};
use cranelift_module::{FuncId, Linkage, Module as CraneliftModule};
use cranelift_jit::{JITBuilder, JITModule};
use frame::{FnEntry, JitFrame, JitModuleCtx};
use helpers::{take_exception_error, static_fields_clear, JitFn};
use std::collections::HashMap;

// ─── Public API ─────────────────────────────────────────────────────────────

/// A fully compiled z42 module ready for native execution.
pub struct JitModule {
    /// Keeps the JITModule alive so the machine code pages remain valid.
    _jit: JITModule,
    ctx:  Box<JitModuleCtx>,
    /// Module name (used to locate __static_init__ and entry points).
    name: String,
}

impl JitModule {
    /// Run a specific entry function by name (no static-init).
    pub fn run_fn(&self, entry_name: &str) -> Result<()> {
        let entry = self.ctx.fn_entries.get(entry_name)
            .ok_or_else(|| anyhow::anyhow!("JIT: entry `{}` not found", entry_name))?;
        let mut frame = JitFrame::new(entry.max_reg, &[]);
        let f: JitFn = unsafe { std::mem::transmute(entry.ptr) };
        let r = unsafe { f(&mut frame, &*self.ctx) };
        if r != 0 { return Err(take_exception_error()); }
        Ok(())
    }

    /// Run with static initialisation: clears static fields, calls __static_init__ if
    /// present, then calls the given entry function.
    pub fn run(&self, entry_name: &str) -> Result<()> {
        static_fields_clear();

        // Call __static_init__ if compiled.
        let init_name = format!("{}.__static_init__", self.name);
        if let Some(entry) = self.ctx.fn_entries.get(&init_name) {
            let mut frame = JitFrame::new(entry.max_reg, &[]);
            let f: JitFn  = unsafe { std::mem::transmute(entry.ptr) };
            let r = unsafe { f(&mut frame, &*self.ctx) };
            if r != 0 { return Err(take_exception_error()); }
        }

        self.run_fn(entry_name)
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

    // Register every extern "C" helper by its #[no_mangle] name.
    macro_rules! reg {
        ($name:expr, $fn:expr) => {
            jit_builder.symbol($name, $fn as *const u8);
        };
    }
    reg!("jit_const_i32",     helpers::jit_const_i32);
    reg!("jit_const_i64",     helpers::jit_const_i64);
    reg!("jit_const_f64",     helpers::jit_const_f64);
    reg!("jit_const_bool",    helpers::jit_const_bool);
    reg!("jit_const_null",    helpers::jit_const_null);
    reg!("jit_const_str",     helpers::jit_const_str);
    reg!("jit_copy",          helpers::jit_copy);
    reg!("jit_add",           helpers::jit_add);
    reg!("jit_sub",           helpers::jit_sub);
    reg!("jit_mul",           helpers::jit_mul);
    reg!("jit_div",           helpers::jit_div);
    reg!("jit_rem",           helpers::jit_rem);
    reg!("jit_eq",            helpers::jit_eq);
    reg!("jit_ne",            helpers::jit_ne);
    reg!("jit_lt",            helpers::jit_lt);
    reg!("jit_le",            helpers::jit_le);
    reg!("jit_gt",            helpers::jit_gt);
    reg!("jit_ge",            helpers::jit_ge);
    reg!("jit_and",           helpers::jit_and);
    reg!("jit_or",            helpers::jit_or);
    reg!("jit_not",           helpers::jit_not);
    reg!("jit_neg",           helpers::jit_neg);
    reg!("jit_bit_and",       helpers::jit_bit_and);
    reg!("jit_bit_or",        helpers::jit_bit_or);
    reg!("jit_bit_xor",       helpers::jit_bit_xor);
    reg!("jit_bit_not",       helpers::jit_bit_not);
    reg!("jit_shl",           helpers::jit_shl);
    reg!("jit_shr",           helpers::jit_shr);
    reg!("jit_store",         helpers::jit_store);
    reg!("jit_load",          helpers::jit_load);
    reg!("jit_str_concat",    helpers::jit_str_concat);
    reg!("jit_to_str",        helpers::jit_to_str);
    reg!("jit_call",          helpers::jit_call);
    reg!("jit_builtin",       helpers::jit_builtin);
    reg!("jit_array_new",     helpers::jit_array_new);
    reg!("jit_array_new_lit", helpers::jit_array_new_lit);
    reg!("jit_array_get",     helpers::jit_array_get);
    reg!("jit_array_set",     helpers::jit_array_set);
    reg!("jit_array_len",     helpers::jit_array_len);
    reg!("jit_obj_new",       helpers::jit_obj_new);
    reg!("jit_field_get",     helpers::jit_field_get);
    reg!("jit_field_set",     helpers::jit_field_set);
    reg!("jit_vcall",         helpers::jit_vcall);
    reg!("jit_is_instance",   helpers::jit_is_instance);
    reg!("jit_as_cast",       helpers::jit_as_cast);
    reg!("jit_static_get",    helpers::jit_static_get);
    reg!("jit_static_set",    helpers::jit_static_set);
    reg!("jit_get_bool",      helpers::jit_get_bool);
    reg!("jit_set_ret",       helpers::jit_set_ret);
    reg!("jit_throw",         helpers::jit_throw);
    reg!("jit_install_catch", helpers::jit_install_catch);

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
    let helper_ids = translate::declare_helpers(&mut jit)?;

    // ── 5. Translate each z42 function ───────────────────────────────────────
    for func in &module.functions {
        let max_r = max_regs[&func.name];
        translate::translate_function(&mut jit, &helper_ids, func, max_r, &func_ids)?;
    }

    // ── 6. Finalise ──────────────────────────────────────────────────────────
    jit.finalize_definitions()?;

    // ── 7. Build fn_entries ───────────────────────────────────────────────────
    let mut fn_entries: HashMap<String, FnEntry> = HashMap::new();
    for (name, id) in &func_ids {
        let ptr_raw = jit.get_finalized_function(*id);
        fn_entries.insert(name.clone(), FnEntry {
            ptr:     ptr_raw as *const u8,
            max_reg: max_regs[name],
        });
    }

    // ── 8. Build JitModuleCtx ─────────────────────────────────────────────────
    let ctx = Box::new(JitModuleCtx {
        string_pool: module.string_pool.clone(),
        fn_entries,
        module: module as *const Module,
    });

    Ok(JitModule { _jit: jit, ctx, name: module.name.clone() })
}

// ─── Public entry point called from vm.rs ───────────────────────────────────

/// Called by `Vm::run` when the execution mode is JIT.
pub fn run(module: &Module, entry_name: &str) -> Result<()> {
    let jit_module = compile_module(module)?;
    jit_module.run(entry_name)
}
