/// Cranelift IR translation: z42 SSA bytecode → native machine code.
///
/// One z42 basic block maps to one Cranelift block.
/// All value-level operations are dispatched to `extern "C"` helper functions
/// (see `helpers.rs`).  Only branches, jumps, and function entry/exit are
/// emitted as inline Cranelift instructions.

use crate::metadata::{Function, Instruction, Terminator};
use anyhow::{bail, Result};
use cranelift_codegen::ir::{AbiParam, InstBuilder};
use cranelift_codegen::ir::types;
use cranelift_codegen::Context;
use cranelift_frontend::{FunctionBuilder, FunctionBuilderContext};
use cranelift_module::{FuncId, Linkage, Module as CraneliftModule};
use cranelift_jit::JITModule;
use std::collections::HashMap;

// ═════════════════════════════════════════════════════════════════════════════
// HelperIds — module-level FuncId for each helper (not per-function)
// ═════════════════════════════════════════════════════════════════════════════

pub struct HelperIds {
    pub const_i32:      FuncId,
    pub const_i64:      FuncId,
    pub const_f64:      FuncId,
    pub const_bool:     FuncId,
    pub const_char:     FuncId,
    pub const_null:     FuncId,
    pub const_str:      FuncId,
    pub copy:           FuncId,
    pub add:            FuncId,
    pub sub:            FuncId,
    pub mul:            FuncId,
    pub div:            FuncId,
    pub rem:            FuncId,
    pub eq:             FuncId,
    pub ne:             FuncId,
    pub lt:             FuncId,
    pub le:             FuncId,
    pub gt:             FuncId,
    pub ge:             FuncId,
    pub and:            FuncId,
    pub or:             FuncId,
    pub not:            FuncId,
    pub neg:            FuncId,
    pub bit_and:        FuncId,
    pub bit_or:         FuncId,
    pub bit_xor:        FuncId,
    pub bit_not:        FuncId,
    pub shl:            FuncId,
    pub shr:            FuncId,
    pub str_concat:     FuncId,
    pub to_str:         FuncId,
    pub call:           FuncId,
    pub builtin:        FuncId,
    pub array_new:      FuncId,
    pub array_new_lit:  FuncId,
    pub array_get:      FuncId,
    pub array_set:      FuncId,
    pub array_len:      FuncId,
    pub obj_new:        FuncId,
    pub field_get:      FuncId,
    pub field_set:      FuncId,
    pub vcall:          FuncId,
    pub is_instance:    FuncId,
    pub as_cast:        FuncId,
    pub static_get:     FuncId,
    pub static_set:     FuncId,
    pub get_bool:       FuncId,
    pub set_ret:        FuncId,
    pub throw:          FuncId,
    pub install_catch:  FuncId,
    /// catch-by-generic-type (2026-05-06): peek at pending exception's class +
    /// subclass walk vs `target` string. Returns 1 on match, 0 otherwise.
    pub match_catch_type: FuncId,
    // L3 closure helpers (impl-closure-l3-jit-complete)
    pub load_fn:        FuncId,
    pub mk_clos:        FuncId,
    pub call_indirect:  FuncId,
    // D1b add-method-group-conversion: cached method group conversion
    pub load_fn_cached: FuncId,
    // D-8b-3 Phase 2: generic-T `default(T)` runtime resolution
    pub default_of:     FuncId,
}

// ═════════════════════════════════════════════════════════════════════════════
// declare_helpers — register all helpers in the JITModule
// ═════════════════════════════════════════════════════════════════════════════

pub fn declare_helpers(jit: &mut JITModule) -> Result<HelperIds> {
    let ptr  = jit.target_config().pointer_type();
    let i8t  = types::I8;
    let i32t = types::I32;
    let i64t = types::I64;
    let f64t = types::F64;

    macro_rules! decl {
        ($name:expr, [$($p:expr),*], [$($r:expr),*]) => {{
            let mut sig = jit.make_signature();
            $(sig.params.push(AbiParam::new($p));)*
            $(sig.returns.push(AbiParam::new($r));)*
            jit.declare_function($name, Linkage::Import, &sig)?
        }};
    }

    // extend-jit-helper-abi (2026-04-28): every helper now receives
    // `ctx: *const JitModuleCtx` as 2nd param (after `frame`). Helper bodies
    // reach VmContext via `(*ctx).vm_ctx`, replacing the previous
    // `thread_local!` slots in `helpers.rs`.
    Ok(HelperIds {
        const_i32:     decl!("jit_const_i32",  [ptr, ptr, i32t, i32t],                    []),
        const_i64:     decl!("jit_const_i64",  [ptr, ptr, i32t, i64t],                    []),
        const_f64:     decl!("jit_const_f64",  [ptr, ptr, i32t, f64t],                    []),
        const_bool:    decl!("jit_const_bool", [ptr, ptr, i32t, i8t],                     []),
        const_char:    decl!("jit_const_char", [ptr, ptr, i32t, i32t],                    []),
        const_null:    decl!("jit_const_null", [ptr, ptr, i32t],                           []),
        const_str:     decl!("jit_const_str",  [ptr, ptr, i32t, i32t],                    [i8t]),
        copy:          decl!("jit_copy",       [ptr, ptr, i32t, i32t],                    []),
        add:           decl!("jit_add",        [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        sub:           decl!("jit_sub",        [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        mul:           decl!("jit_mul",        [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        div:           decl!("jit_div",        [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        rem:           decl!("jit_rem",        [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        eq:            decl!("jit_eq",         [ptr, ptr, i32t, i32t, i32t],              []),
        ne:            decl!("jit_ne",         [ptr, ptr, i32t, i32t, i32t],              []),
        lt:            decl!("jit_lt",         [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        le:            decl!("jit_le",         [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        gt:            decl!("jit_gt",         [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        ge:            decl!("jit_ge",         [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        and:           decl!("jit_and",        [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        or:            decl!("jit_or",         [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        not:           decl!("jit_not",        [ptr, ptr, i32t, i32t],                    [i8t]),
        neg:           decl!("jit_neg",        [ptr, ptr, i32t, i32t],                    [i8t]),
        bit_and:       decl!("jit_bit_and",    [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        bit_or:        decl!("jit_bit_or",     [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        bit_xor:       decl!("jit_bit_xor",    [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        bit_not:       decl!("jit_bit_not",    [ptr, ptr, i32t, i32t],                    [i8t]),
        shl:           decl!("jit_shl",        [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        shr:           decl!("jit_shr",        [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        str_concat:    decl!("jit_str_concat", [ptr, ptr, i32t, i32t, i32t],              [i8t]),
        to_str:        decl!("jit_to_str",     [ptr, ptr, i32t, i32t],                    [i8t]),
        // jit_call(frame, ctx, dst, name_ptr, name_len, args_ptr, argc) -> u8
        call:          decl!("jit_call",       [ptr, ptr, i32t, ptr, i64t, ptr, i64t],    [i8t]),
        // jit_builtin(frame, ctx, dst, name_ptr, name_len, args_ptr, argc) -> u8
        builtin:       decl!("jit_builtin",    [ptr, ptr, i32t, ptr, i64t, ptr, i64t],    [i8t]),
        array_new:     decl!("jit_array_new",     [ptr, ptr, i32t, i32t],                 [i8t]),
        array_new_lit: decl!("jit_array_new_lit", [ptr, ptr, i32t, ptr, i64t],            []),
        array_get:     decl!("jit_array_get",     [ptr, ptr, i32t, i32t, i32t],           [i8t]),
        array_set:     decl!("jit_array_set",     [ptr, ptr, i32t, i32t, i32t],           [i8t]),
        array_len:     decl!("jit_array_len",     [ptr, ptr, i32t, i32t],                 [i8t]),
        // jit_obj_new(frame, ctx, dst, cls_ptr, cls_len, ctor_ptr, ctor_len, args_ptr, argc) -> u8
        obj_new:       decl!("jit_obj_new",    [ptr, ptr, i32t, ptr, i64t, ptr, i64t, ptr, i64t], [i8t]),
        field_get:     decl!("jit_field_get",  [ptr, ptr, i32t, i32t, ptr, i64t],         [i8t]),
        field_set:     decl!("jit_field_set",  [ptr, ptr, i32t, ptr, i64t, i32t],         [i8t]),
        // jit_vcall(frame, ctx, dst, obj, method_ptr, method_len, args_ptr, argc) -> u8
        vcall:         decl!("jit_vcall",      [ptr, ptr, i32t, i32t, ptr, i64t, ptr, i64t], [i8t]),
        is_instance:   decl!("jit_is_instance",[ptr, ptr, i32t, i32t, ptr, i64t],         []),
        as_cast:       decl!("jit_as_cast",    [ptr, ptr, i32t, i32t, ptr, i64t],         []),
        static_get:    decl!("jit_static_get", [ptr, ptr, i32t, ptr, i64t],               []),
        static_set:    decl!("jit_static_set", [ptr, ptr, ptr, i64t, i32t],               []),
        get_bool:      decl!("jit_get_bool",      [ptr, ptr, i32t],                       [i8t]),
        set_ret:       decl!("jit_set_ret",       [ptr, ptr, i32t],                       []),
        throw:         decl!("jit_throw",         [ptr, ptr, i32t],                       []),
        install_catch: decl!("jit_install_catch", [ptr, ptr, i32t],                       []),
        // jit_match_catch_type(frame, ctx, target_ptr, target_len) -> i8
        match_catch_type: decl!("jit_match_catch_type", [ptr, ptr, ptr, i64t],            [i8t]),
        // jit_load_fn(frame, ctx, dst, name_ptr, name_len) -> u8
        load_fn:        decl!("jit_load_fn",       [ptr, ptr, i32t, ptr, i64t],                  [i8t]),
        // jit_mk_clos(frame, ctx, dst, name_ptr, name_len, caps_ptr, caps_len, stack_alloc:u8) -> u8
        mk_clos:        decl!("jit_mk_clos",       [ptr, ptr, i32t, ptr, i64t, ptr, i64t, i8t], [i8t]),
        // jit_call_indirect(frame, ctx, dst, callee, args_ptr, args_len) -> u8
        call_indirect:  decl!("jit_call_indirect", [ptr, ptr, i32t, i32t, ptr, i64t],            [i8t]),
        // jit_load_fn_cached(frame, ctx, dst, name_ptr, name_len, slot_id) -> u8
        load_fn_cached: decl!("jit_load_fn_cached", [ptr, ptr, i32t, ptr, i64t, i32t],           [i8t]),
        // jit_default_of(frame, ctx, dst, param_index) -> u8
        default_of:     decl!("jit_default_of",     [ptr, ptr, i32t, i32t],                      [i8t]),
    })
}

// ═════════════════════════════════════════════════════════════════════════════
// max_reg — largest register index used in a function
// ═════════════════════════════════════════════════════════════════════════════

pub fn max_reg(func: &Function) -> usize {
    let mut max = func.param_count.saturating_sub(1);
    for block in &func.blocks {
        for instr in &block.instructions {
            let dst: Option<u32> = match instr {
                Instruction::ConstStr  { dst, .. } => Some(*dst),
                Instruction::ConstI32  { dst, .. } => Some(*dst),
                Instruction::ConstI64  { dst, .. } => Some(*dst),
                Instruction::ConstF64  { dst, .. } => Some(*dst),
                Instruction::ConstBool { dst, .. } => Some(*dst),
                Instruction::ConstChar { dst, .. } => Some(*dst),
                Instruction::ConstNull { dst }      => Some(*dst),
                Instruction::Copy      { dst, .. }  => Some(*dst),
                Instruction::Add       { dst, .. }  => Some(*dst),
                Instruction::Sub       { dst, .. }  => Some(*dst),
                Instruction::Mul       { dst, .. }  => Some(*dst),
                Instruction::Div       { dst, .. }  => Some(*dst),
                Instruction::Rem       { dst, .. }  => Some(*dst),
                Instruction::Eq        { dst, .. }  => Some(*dst),
                Instruction::Ne        { dst, .. }  => Some(*dst),
                Instruction::Lt        { dst, .. }  => Some(*dst),
                Instruction::Le        { dst, .. }  => Some(*dst),
                Instruction::Gt        { dst, .. }  => Some(*dst),
                Instruction::Ge        { dst, .. }  => Some(*dst),
                Instruction::And       { dst, .. }  => Some(*dst),
                Instruction::Or        { dst, .. }  => Some(*dst),
                Instruction::Not       { dst, .. }  => Some(*dst),
                Instruction::Neg       { dst, .. }  => Some(*dst),
                Instruction::BitAnd    { dst, .. }  => Some(*dst),
                Instruction::BitOr     { dst, .. }  => Some(*dst),
                Instruction::BitXor    { dst, .. }  => Some(*dst),
                Instruction::BitNot    { dst, .. }  => Some(*dst),
                Instruction::Shl       { dst, .. }  => Some(*dst),
                Instruction::Shr       { dst, .. }  => Some(*dst),
                Instruction::StrConcat { dst, .. }  => Some(*dst),
                Instruction::ToStr     { dst, .. }  => Some(*dst),
                Instruction::Call      { dst, .. }  => Some(*dst),
                // Spec impl-ref-out-in-runtime: address-load opcodes (interp
                // only; JIT body match further down emits unimplemented).
                Instruction::LoadLocalAddr { dst, .. } => Some(*dst),
                Instruction::LoadElemAddr  { dst, .. } => Some(*dst),
                Instruction::LoadFieldAddr { dst, .. } => Some(*dst),
                Instruction::DefaultOf     { dst, .. } => Some(*dst),
                Instruction::Builtin   { dst, .. }  => Some(*dst),
                Instruction::ArrayNew    { dst, .. } => Some(*dst),
                Instruction::ArrayNewLit { dst, .. } => Some(*dst),
                Instruction::ArrayGet    { dst, .. } => Some(*dst),
                Instruction::ArraySet    { .. }      => None,
                Instruction::ArrayLen    { dst, .. } => Some(*dst),
                Instruction::ObjNew    { dst, .. }  => Some(*dst),
                Instruction::FieldGet  { dst, .. }  => Some(*dst),
                Instruction::FieldSet  { .. }       => None,
                Instruction::VCall     { dst, .. }  => Some(*dst),
                Instruction::IsInstance { dst, .. } => Some(*dst),
                Instruction::AsCast     { dst, .. } => Some(*dst),
                Instruction::StaticGet  { dst, .. } => Some(*dst),
                Instruction::StaticSet  { .. }      => None,

                // C1 native interop scaffold — JIT path lands in L3.M16; for
                // now compute dst register correctly so reg-allocator stays
                // sound when these opcodes appear in interp-mode bytecode.
                Instruction::CallNative       { dst, .. } => Some(*dst),
                Instruction::CallNativeVtable { dst, .. } => Some(*dst),
                Instruction::PinPtr           { dst, .. } => Some(*dst),
                Instruction::UnpinPtr         { .. }      => None,

                // impl-lambda-l2: JIT path lands in L3+. For now compute dst
                // correctly so reg-allocation stays sound; translation falls
                // back to interp mode (see translate.rs match below).
                Instruction::LoadFn       { dst, .. } => Some(*dst),
                Instruction::LoadFnCached { dst, .. } => Some(*dst),
                Instruction::CallIndirect { dst, .. } => Some(*dst),
                Instruction::MkClos       { dst, .. } => Some(*dst),
            };
            if let Some(d) = dst {
                if d as usize > max { max = d as usize; }
            }
        }
    }
    max
}

// ═════════════════════════════════════════════════════════════════════════════
// Exception table helper
// ═════════════════════════════════════════════════════════════════════════════

/// Find every exception_table entry whose try region covers `block_idx`,
/// in source order. catch-by-generic-type (2026-05-06) requires the JIT to
/// see all covering entries (not just the first) so it can emit a typed-catch
/// chain that probes each candidate's `catch_type` against the thrown value's
/// class and jumps to the first matching handler.
fn find_handler_entries(func: &Function, block_idx: usize) -> Vec<usize> {
    let mut out = Vec::new();
    for (i, entry) in func.exception_table.iter().enumerate() {
        let Some(start) = func.blocks.iter().position(|b| b.label == entry.try_start) else { continue };
        let Some(end)   = func.blocks.iter().position(|b| b.label == entry.try_end)   else { continue };
        if block_idx >= start && block_idx < end {
            out.push(i);
        }
    }
    out
}

// ═════════════════════════════════════════════════════════════════════════════
// translate_function
// ═════════════════════════════════════════════════════════════════════════════

pub fn translate_function(
    jit:          &mut JITModule,
    helper_ids:   &HelperIds,
    z42_func:     &Function,
    _func_max_reg: usize,
    func_ids:     &HashMap<String, FuncId>,
) -> Result<()> {
    let func_id = func_ids[&z42_func.name];
    let ptr     = jit.target_config().pointer_type();

    // Build Cranelift function signature: (frame_ptr, ctx_ptr) -> i8
    let mut cl_sig = jit.make_signature();
    cl_sig.params.push(AbiParam::new(ptr));
    cl_sig.params.push(AbiParam::new(ptr));
    cl_sig.returns.push(AbiParam::new(types::I8));

    let mut ctx = Context::new();
    ctx.func.signature = cl_sig;

    let mut fb_ctx = FunctionBuilderContext::new();
    let mut builder = FunctionBuilder::new(&mut ctx.func, &mut fb_ctx);

    // Create one Cranelift block per z42 block.
    let num_blocks = z42_func.blocks.len();
    let cl_blocks: Vec<cranelift_codegen::ir::Block> = (0..num_blocks)
        .map(|_| builder.create_block())
        .collect();

    // Entry: append function parameters to cl_blocks[0].
    builder.append_block_params_for_function_params(cl_blocks[0]);
    builder.switch_to_block(cl_blocks[0]);

    let frame_val = builder.block_params(cl_blocks[0])[0];
    let ctx_val   = builder.block_params(cl_blocks[0])[1];

    // Import all helpers as FuncRef (per-function, valid for this function only).
    macro_rules! imp {
        ($id:expr) => { jit.declare_func_in_func($id, builder.func) }
    }
    let hr_const_i32     = imp!(helper_ids.const_i32);
    let hr_const_i64     = imp!(helper_ids.const_i64);
    let hr_const_f64     = imp!(helper_ids.const_f64);
    let hr_const_bool    = imp!(helper_ids.const_bool);
    let hr_const_char    = imp!(helper_ids.const_char);
    let hr_const_null    = imp!(helper_ids.const_null);
    let hr_const_str     = imp!(helper_ids.const_str);
    let hr_copy          = imp!(helper_ids.copy);
    let hr_add           = imp!(helper_ids.add);
    let hr_sub           = imp!(helper_ids.sub);
    let hr_mul           = imp!(helper_ids.mul);
    let hr_div           = imp!(helper_ids.div);
    let hr_rem           = imp!(helper_ids.rem);
    let hr_eq            = imp!(helper_ids.eq);
    let hr_ne            = imp!(helper_ids.ne);
    let hr_lt            = imp!(helper_ids.lt);
    let hr_le            = imp!(helper_ids.le);
    let hr_gt            = imp!(helper_ids.gt);
    let hr_ge            = imp!(helper_ids.ge);
    let hr_and           = imp!(helper_ids.and);
    let hr_or            = imp!(helper_ids.or);
    let hr_not           = imp!(helper_ids.not);
    let hr_neg           = imp!(helper_ids.neg);
    let hr_bit_and       = imp!(helper_ids.bit_and);
    let hr_bit_or        = imp!(helper_ids.bit_or);
    let hr_bit_xor       = imp!(helper_ids.bit_xor);
    let hr_bit_not       = imp!(helper_ids.bit_not);
    let hr_shl           = imp!(helper_ids.shl);
    let hr_shr           = imp!(helper_ids.shr);
    let hr_str_concat    = imp!(helper_ids.str_concat);
    let hr_to_str        = imp!(helper_ids.to_str);
    let hr_call          = imp!(helper_ids.call);
    let hr_builtin       = imp!(helper_ids.builtin);
    let hr_array_new     = imp!(helper_ids.array_new);
    let hr_array_new_lit = imp!(helper_ids.array_new_lit);
    let hr_array_get     = imp!(helper_ids.array_get);
    let hr_array_set     = imp!(helper_ids.array_set);
    let hr_array_len     = imp!(helper_ids.array_len);
    let hr_obj_new       = imp!(helper_ids.obj_new);
    let hr_field_get     = imp!(helper_ids.field_get);
    let hr_field_set     = imp!(helper_ids.field_set);
    let hr_vcall         = imp!(helper_ids.vcall);
    let hr_is_instance   = imp!(helper_ids.is_instance);
    let hr_as_cast       = imp!(helper_ids.as_cast);
    let hr_static_get    = imp!(helper_ids.static_get);
    let hr_static_set    = imp!(helper_ids.static_set);
    let hr_get_bool      = imp!(helper_ids.get_bool);
    let hr_set_ret       = imp!(helper_ids.set_ret);
    let hr_throw            = imp!(helper_ids.throw);
    let hr_install_catch    = imp!(helper_ids.install_catch);
    let hr_match_catch_type = imp!(helper_ids.match_catch_type);
    let hr_load_fn       = imp!(helper_ids.load_fn);
    let hr_mk_clos       = imp!(helper_ids.mk_clos);
    let hr_call_indirect = imp!(helper_ids.call_indirect);
    let hr_load_fn_cached = imp!(helper_ids.load_fn_cached);
    let hr_default_of     = imp!(helper_ids.default_of);

    // ── Translate each z42 block ──────────────────────────────────────────────
    for (block_idx, z42_block) in z42_func.blocks.iter().enumerate() {
        if block_idx != 0 {
            builder.switch_to_block(cl_blocks[block_idx]);
        }

        // catch-by-generic-type (2026-05-06): collect every enclosing exception-
        // handler entry, in source order. Each tuple is
        //   (catch_cl, catch_reg, catch_type)
        // where `catch_type` is None for wildcard / synthetic-finally fallthrough
        // (matches any thrown value) and Some(t) for a typed catch (only matches
        // when the thrown value's class is `t` or a subclass).
        //
        // The legacy single-entry shortcut is preserved as `catch_info` for the
        // wildcard-only case so the unconditional jump path stays identical to
        // pre-fix behaviour. Typed / multi-catch goes through `catch_chain`.
        let catch_chain: Vec<(cranelift_codegen::ir::Block, u32, Option<&str>)> =
            find_handler_entries(z42_func, block_idx).into_iter().map(|ei| {
                let entry      = &z42_func.exception_table[ei];
                let catch_idx  = z42_func.blocks.iter().position(|b| b.label == entry.catch_label)
                    .expect("catch_label block must exist");
                let ty: Option<&str> = match entry.catch_type.as_deref() {
                    None | Some("*") => None,
                    Some(t)          => Some(t),
                };
                (cl_blocks[catch_idx], entry.catch_reg, ty)
            }).collect();
        // Wildcard shortcut: if there is exactly one covering entry and it is
        // untyped, the JIT can skip the type-probe chain entirely (cheap path
        // for the existing 7 untyped-catch goldens).
        let catch_info: Option<(cranelift_codegen::ir::Block, u32)> =
            if catch_chain.len() == 1 && catch_chain[0].2.is_none() {
                Some((catch_chain[0].0, catch_chain[0].1))
            } else {
                None
            };

        // ── Inline helpers used in match arms ────────────────────────────────

        // Emit an i32 constant for a register index.
        macro_rules! ri {
            ($r:expr) => { builder.ins().iconst(types::I32, $r as i64) }
        }

        // Embed a &str as (ptr: pointer_type, len: i64) Cranelift constants.
        // We use a global_value backed by a static string slice.
        // Since we can't easily add global data from here, we pass the string
        // pointer as an i64 address constant (valid for the duration of execution).
        macro_rules! str_val {
            ($s:expr) => {{
                // SAFETY: the string literal is 'static (from the bytecode module
                // which lives for the whole JitModule lifetime).
                let bytes: &'static [u8] = unsafe {
                    std::slice::from_raw_parts(
                        $s.as_ptr(),
                        $s.len(),
                    )
                };
                let sptr = builder.ins().iconst(ptr, bytes.as_ptr() as i64);
                let slen = builder.ins().iconst(types::I64, bytes.len() as i64);
                (sptr, slen)
            }};
        }

        // Pack a &[u32] of register indices into a static-lifetime pointer+len.
        macro_rules! regs_val {
            ($regs:expr) => {{
                let slice: &'static [u32] = unsafe {
                    std::slice::from_raw_parts(
                        $regs.as_ptr(),
                        $regs.len(),
                    )
                };
                let rptr = builder.ins().iconst(ptr, slice.as_ptr() as i64);
                let rlen = builder.ins().iconst(types::I64, slice.len() as i64);
                (rptr, rlen)
            }};
        }

        // After a helper call that returns u8: branch to catch dispatch or
        // return 1 on error. Blocks are NOT sealed here; seal_all_blocks() is
        // called once after all control-flow edges are established (handles
        // back-edges in loops correctly).
        //
        // catch-by-generic-type (2026-05-06): when the enclosing scope has any
        // typed catches (or multiple covering entries), the exception path
        // probes each entry's catch_type via `jit_match_catch_type` and jumps
        // to the first match; falls through to return-1 if none match. The
        // wildcard fast-path (single covering untyped entry → unconditional
        // jump) is preserved via the `catch_info` shortcut on the cold side.
        macro_rules! emit_dispatch_to_catch_or_return {
            () => {{
                if let Some((catch_cl, catch_reg)) = catch_info {
                    let creg = ri!(catch_reg);
                    builder.ins().call(hr_install_catch, &[frame_val, ctx_val, creg]);
                    builder.ins().jump(catch_cl, &[]);
                } else if !catch_chain.is_empty() {
                    // Typed / multi-catch chain: probe each entry's catch_type;
                    // first instance-of match wins. The `closed_by_wildcard`
                    // flag tracks whether a wildcard entry already terminated
                    // the current Cranelift block via `jump` — once that happens
                    // the block is "filled" and the trailing return-1 fallthrough
                    // would be illegal (panic in Cranelift's frontend).
                    let mut closed_by_wildcard = false;
                    for (catch_cl, catch_reg, ty) in catch_chain.iter() {
                        match ty {
                            None => {
                                // Wildcard / synthetic-finally fallthrough — always match.
                                let creg = ri!(*catch_reg);
                                builder.ins().call(hr_install_catch, &[frame_val, ctx_val, creg]);
                                builder.ins().jump(*catch_cl, &[]);
                                closed_by_wildcard = true;
                                break;
                            }
                            Some(t) => {
                                let (tptr, tlen) = str_val!(t);
                                let inst = builder.ins().call(hr_match_catch_type, &[frame_val, ctx_val, tptr, tlen]);
                                let m = builder.inst_results(inst)[0];
                                let take_blk = builder.create_block();
                                let next_blk = builder.create_block();
                                builder.ins().brif(m, take_blk, &[], next_blk, &[]);
                                builder.switch_to_block(take_blk);
                                let creg = ri!(*catch_reg);
                                builder.ins().call(hr_install_catch, &[frame_val, ctx_val, creg]);
                                builder.ins().jump(*catch_cl, &[]);
                                builder.switch_to_block(next_blk);
                            }
                        }
                    }
                    if !closed_by_wildcard {
                        // All entries were typed and none matched — propagate.
                        let one = builder.ins().iconst(types::I8, 1);
                        builder.ins().return_(&[one]);
                    }
                } else {
                    let one = builder.ins().iconst(types::I8, 1);
                    builder.ins().return_(&[one]);
                }
            }};
        }
        macro_rules! check {
            ($ret:expr) => {{
                let ok_blk  = builder.create_block();
                let exc_blk = builder.create_block();
                builder.ins().brif($ret, exc_blk, &[], ok_blk, &[]);
                builder.switch_to_block(exc_blk);
                emit_dispatch_to_catch_or_return!();
                builder.switch_to_block(ok_blk);
            }};
        }

        // ── Instruction translation ───────────────────────────────────────────
        for instr in &z42_block.instructions {
            match instr {
                Instruction::ConstI32 { dst, val } => {
                    let d = ri!(*dst); let v = builder.ins().iconst(types::I32, *val as i64);
                    builder.ins().call(hr_const_i32, &[frame_val, ctx_val, d, v]);
                }
                Instruction::ConstI64 { dst, val } => {
                    let d = ri!(*dst); let v = builder.ins().iconst(types::I64, *val);
                    builder.ins().call(hr_const_i64, &[frame_val, ctx_val, d, v]);
                }
                Instruction::ConstF64 { dst, val } => {
                    let d = ri!(*dst); let v = builder.ins().f64const(*val);
                    builder.ins().call(hr_const_f64, &[frame_val, ctx_val, d, v]);
                }
                Instruction::ConstBool { dst, val } => {
                    let d = ri!(*dst); let v = builder.ins().iconst(types::I8, if *val { 1 } else { 0 });
                    builder.ins().call(hr_const_bool, &[frame_val, ctx_val, d, v]);
                }
                Instruction::ConstChar { dst, val } => {
                    let d = ri!(*dst); let v = builder.ins().iconst(types::I32, *val as i32 as i64);
                    builder.ins().call(hr_const_char, &[frame_val, ctx_val, d, v]);
                }
                Instruction::ConstNull { dst } => {
                    let d = ri!(*dst);
                    builder.ins().call(hr_const_null, &[frame_val, ctx_val, d]);
                }
                Instruction::ConstStr { dst, idx } => {
                    let d = ri!(*dst); let i = ri!(*idx);
                    let inst = builder.ins().call(hr_const_str, &[frame_val, ctx_val, d, i]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Copy { dst, src } => {
                    let d = ri!(*dst); let s = ri!(*src);
                    builder.ins().call(hr_copy, &[frame_val, ctx_val, d, s]);
                }

                // Arithmetic
                Instruction::Add { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_add, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Sub { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_sub, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Mul { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_mul, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Div { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_div, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Rem { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_rem, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // Comparison
                Instruction::Eq { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    builder.ins().call(hr_eq, &[frame_val, ctx_val, d, av, bv]);
                }
                Instruction::Ne { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    builder.ins().call(hr_ne, &[frame_val, ctx_val, d, av, bv]);
                }
                Instruction::Lt { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_lt, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Le { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_le, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Gt { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_gt, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Ge { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_ge, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // Logical
                Instruction::And { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_and, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Or { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_or, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Not { dst, src } => {
                    let d = ri!(*dst); let s = ri!(*src);
                    let inst = builder.ins().call(hr_not, &[frame_val, ctx_val, d, s]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // Unary arithmetic
                Instruction::Neg { dst, src } => {
                    let d = ri!(*dst); let s = ri!(*src);
                    let inst = builder.ins().call(hr_neg, &[frame_val, ctx_val, d, s]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // Bitwise
                Instruction::BitAnd { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_bit_and, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::BitOr { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_bit_or, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::BitXor { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_bit_xor, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::BitNot { dst, src } => {
                    let d = ri!(*dst); let s = ri!(*src);
                    let inst = builder.ins().call(hr_bit_not, &[frame_val, ctx_val, d, s]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Shl { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_shl, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Shr { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_shr, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // String
                Instruction::StrConcat { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_str_concat, &[frame_val, ctx_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::ToStr { dst, src } => {
                    let d = ri!(*dst); let s = ri!(*src);
                    let inst = builder.ins().call(hr_to_str, &[frame_val, ctx_val, d, s]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // Calls
                Instruction::Call { dst, func: fname, args } => {
                    let d = ri!(*dst);
                    let (np, nl) = str_val!(fname);
                    let (ap, al) = regs_val!(args);
                    let inst = builder.ins().call(hr_call, &[frame_val, ctx_val, d, np, nl, ap, al]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Builtin { dst, name, args } => {
                    let d = ri!(*dst);
                    let (np, nl) = str_val!(name);
                    let (ap, al) = regs_val!(args);
                    let inst = builder.ins().call(hr_builtin, &[frame_val, ctx_val, d, np, nl, ap, al]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // Arrays
                Instruction::ArrayNew { dst, size } => {
                    let d = ri!(*dst); let s = ri!(*size);
                    let inst = builder.ins().call(hr_array_new, &[frame_val, ctx_val, d, s]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::ArrayNewLit { dst, elems } => {
                    let d = ri!(*dst);
                    let (ep, el) = regs_val!(elems);
                    builder.ins().call(hr_array_new_lit, &[frame_val, ctx_val, d, ep, el]);
                }
                Instruction::ArrayGet { dst, arr, idx } => {
                    let d = ri!(*dst); let a = ri!(*arr); let i = ri!(*idx);
                    let inst = builder.ins().call(hr_array_get, &[frame_val, ctx_val, d, a, i]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::ArraySet { arr, idx, val } => {
                    let a = ri!(*arr); let i = ri!(*idx); let v = ri!(*val);
                    let inst = builder.ins().call(hr_array_set, &[frame_val, ctx_val, a, i, v]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::ArrayLen { dst, arr } => {
                    let d = ri!(*dst); let a = ri!(*arr);
                    let inst = builder.ins().call(hr_array_len, &[frame_val, ctx_val, d, a]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // Objects
                Instruction::ObjNew { dst, class_name, ctor_name, args, type_args: _ } => {
                    // 2026-05-07 add-default-generic-typeparam (D-8b-3 Phase 2):
                    // JIT path drops type_args (helper signature unchanged). The
                    // resulting instance carries empty `type_args`, so `default(T)`
                    // inside JIT-compiled methods on generic classes degrades to
                    // Value::Null (interp path is the source of truth for full
                    // generic-T zero-value resolution). Same trade-off as
                    // LoadFieldAddr — JIT keeps simple, interp covers correctness.
                    let d = ri!(*dst);
                    let (cp, cl) = str_val!(class_name);
                    let (kp, kl) = str_val!(ctor_name);
                    let (ap, al) = regs_val!(args);
                    let inst = builder.ins().call(hr_obj_new, &[frame_val, ctx_val, d, cp, cl, kp, kl, ap, al]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::FieldGet { dst, obj, field_name } => {
                    let d = ri!(*dst); let o = ri!(*obj);
                    let (fp, fl) = str_val!(field_name);
                    let inst = builder.ins().call(hr_field_get, &[frame_val, ctx_val, d, o, fp, fl]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::FieldSet { obj, field_name, val } => {
                    let o = ri!(*obj);
                    let (fp, fl) = str_val!(field_name);
                    let v = ri!(*val);
                    let inst = builder.ins().call(hr_field_set, &[frame_val, ctx_val, o, fp, fl, v]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::VCall { dst, obj, method, args } => {
                    let d = ri!(*dst); let o = ri!(*obj);
                    let (mp, ml) = str_val!(method);
                    let (ap, al) = regs_val!(args);
                    let inst = builder.ins().call(hr_vcall, &[frame_val, ctx_val, d, o, mp, ml, ap, al]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::IsInstance { dst, obj, class_name } => {
                    let d = ri!(*dst); let o = ri!(*obj);
                    let (cp, cl) = str_val!(class_name);
                    builder.ins().call(hr_is_instance, &[frame_val, ctx_val, d, o, cp, cl]);
                }
                Instruction::AsCast { dst, obj, class_name } => {
                    let d = ri!(*dst); let o = ri!(*obj);
                    let (cp, cl) = str_val!(class_name);
                    builder.ins().call(hr_as_cast, &[frame_val, ctx_val, d, o, cp, cl]);
                }

                // Static fields
                Instruction::StaticGet { dst, field } => {
                    let d = ri!(*dst);
                    let (fp, fl) = str_val!(field);
                    builder.ins().call(hr_static_get, &[frame_val, ctx_val, d, fp, fl]);
                }
                Instruction::StaticSet { field, val } => {
                    let (fp, fl) = str_val!(field);
                    let v = ri!(*val);
                    builder.ins().call(hr_static_set, &[frame_val, ctx_val, fp, fl, v]);
                }

                // C1 native interop scaffold: JIT translation lands in
                // L3.M16. Refuse to compile a function that contains these
                // opcodes; caller should keep the function in Interp mode.
                Instruction::CallNative { module, type_name, symbol, .. } => {
                    bail!(
                        "JIT cannot translate CallNative yet (L3.M16): {module}::{type_name}::{symbol}"
                    );
                }
                Instruction::CallNativeVtable { vtable_slot, .. } => {
                    bail!(
                        "JIT cannot translate CallNativeVtable yet (L3.M16): slot={vtable_slot}"
                    );
                }
                Instruction::PinPtr { .. } => {
                    bail!("JIT cannot translate PinPtr yet (L3.M16)");
                }
                Instruction::UnpinPtr { .. } => {
                    bail!("JIT cannot translate UnpinPtr yet (L3.M16)");
                }

                // Spec impl-ref-out-in-runtime: address-load opcodes are
                // interp-only; JIT path needs Value::Ref handling + cross-
                // frame deref support which is not yet implemented (CLAUDE.md
                // "interp 全绿前不碰 JIT/AOT"). Function falls back to interp.
                Instruction::LoadLocalAddr { .. } => {
                    bail!("JIT cannot translate LoadLocalAddr yet (impl-ref-out-in-runtime; interp only)");
                }
                Instruction::LoadElemAddr { .. } => {
                    bail!("JIT cannot translate LoadElemAddr yet (impl-ref-out-in-runtime; interp only)");
                }
                Instruction::LoadFieldAddr { .. } => {
                    bail!("JIT cannot translate LoadFieldAddr yet (impl-ref-out-in-runtime; interp only)");
                }
                // 2026-05-07 D-8b-3 Phase 2 + switch-multicast-funcpredicate-to-generic-exception:
                // emit `jit_default_of(frame, ctx, dst, param_index)` helper call.
                // JIT-allocated instances still have empty type_args (jit_obj_new
                // doesn't propagate them yet), so the helper falls through to Null
                // when called on a JIT-allocated generic instance — same path as
                // method-level / free generic graceful-degradation.
                Instruction::DefaultOf { dst, param_index } => {
                    let d  = ri!(*dst);
                    let pi = builder.ins().iconst(types::I32, *param_index as i64);
                    let inst = builder.ins().call(hr_default_of, &[frame_val, ctx_val, d, pi]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // impl-lambda-l2: lambdas / function references — JIT support
                // lands in a later iteration (L3+). Refuse to compile so the
                // caller keeps the function in Interp mode.
                // L3 closure helpers (impl-closure-l3-jit-complete).
                // Behaviour mirrors interp::exec_instr; see closure.md §6.
                Instruction::LoadFn { dst, func } => {
                    let d = ri!(*dst);
                    let (np, nl) = str_val!(func);
                    let inst = builder.ins().call(hr_load_fn, &[frame_val, ctx_val, d, np, nl]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                // 2026-05-02 D1b: cached method group conversion
                Instruction::LoadFnCached { dst, func, slot_id } => {
                    let d = ri!(*dst);
                    let (np, nl) = str_val!(func);
                    let sid = builder.ins().iconst(types::I32, *slot_id as i64);
                    let inst = builder.ins().call(hr_load_fn_cached,
                        &[frame_val, ctx_val, d, np, nl, sid]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::MkClos { dst, fn_name, captures, stack_alloc } => {
                    let d = ri!(*dst);
                    let (np, nl) = str_val!(fn_name);
                    let (cp, cl) = regs_val!(captures);
                    let sa = builder.ins().iconst(types::I8, if *stack_alloc { 1 } else { 0 });
                    let inst = builder.ins().call(hr_mk_clos,
                        &[frame_val, ctx_val, d, np, nl, cp, cl, sa]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::CallIndirect { dst, callee, args } => {
                    let d = ri!(*dst);
                    let c = ri!(*callee);
                    let (ap, al) = regs_val!(args);
                    let inst = builder.ins().call(hr_call_indirect,
                        &[frame_val, ctx_val, d, c, ap, al]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
            }
        }

        // ── Terminator ───────────────────────────────────────────────────────
        match &z42_block.terminator {
            Terminator::Ret { reg: None } => {
                let zero = builder.ins().iconst(types::I8, 0);
                builder.ins().return_(&[zero]);
            }
            Terminator::Ret { reg: Some(r) } => {
                let rv   = ri!(*r);
                builder.ins().call(hr_set_ret, &[frame_val, ctx_val, rv]);
                let zero = builder.ins().iconst(types::I8, 0);
                builder.ins().return_(&[zero]);
            }
            Terminator::Br { label } => {
                let target = z42_func.blocks.iter().position(|b| &b.label == label)
                    .expect("Br label not found");
                builder.ins().jump(cl_blocks[target], &[]);
            }
            Terminator::BrCond { cond, true_label, false_label } => {
                let cv   = ri!(*cond);
                let inst = builder.ins().call(hr_get_bool, &[frame_val, ctx_val, cv]);
                let b    = builder.inst_results(inst)[0];

                let true_idx  = z42_func.blocks.iter().position(|blk| &blk.label == true_label)
                    .expect("true_label not found");
                let false_idx = z42_func.blocks.iter().position(|blk| &blk.label == false_label)
                    .expect("false_label not found");

                builder.ins().brif(b, cl_blocks[true_idx], &[], cl_blocks[false_idx], &[]);
            }
            Terminator::Throw { reg } => {
                let rv = ri!(*reg);
                builder.ins().call(hr_throw, &[frame_val, ctx_val, rv]);
                emit_dispatch_to_catch_or_return!();
            }
        }

    }

    builder.seal_all_blocks();
    builder.finalize();

    jit.define_function(func_id, &mut ctx)?;
    jit.clear_context(&mut ctx);
    Ok(())
}
