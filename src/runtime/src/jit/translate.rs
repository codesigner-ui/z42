/// Cranelift IR translation: z42 SSA bytecode → native machine code.
///
/// One z42 basic block maps to one Cranelift block.
/// All value-level operations are dispatched to `extern "C"` helper functions
/// (see `helpers.rs`).  Only branches, jumps, and function entry/exit are
/// emitted as inline Cranelift instructions.

use crate::bytecode::{Function, Instruction, Terminator};
use anyhow::Result;
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
    pub store:          FuncId,
    pub load:           FuncId,
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

    Ok(HelperIds {
        const_i32:     decl!("jit_const_i32",  [ptr, i32t, i32t],                    []),
        const_i64:     decl!("jit_const_i64",  [ptr, i32t, i64t],                    []),
        const_f64:     decl!("jit_const_f64",  [ptr, i32t, f64t],                    []),
        const_bool:    decl!("jit_const_bool", [ptr, i32t, i8t],                     []),
        const_null:    decl!("jit_const_null", [ptr, i32t],                           []),
        const_str:     decl!("jit_const_str",  [ptr, ptr, i32t, i32t],               [i8t]),
        copy:          decl!("jit_copy",       [ptr, i32t, i32t],                    []),
        add:           decl!("jit_add",        [ptr, i32t, i32t, i32t],              [i8t]),
        sub:           decl!("jit_sub",        [ptr, i32t, i32t, i32t],              [i8t]),
        mul:           decl!("jit_mul",        [ptr, i32t, i32t, i32t],              [i8t]),
        div:           decl!("jit_div",        [ptr, i32t, i32t, i32t],              [i8t]),
        rem:           decl!("jit_rem",        [ptr, i32t, i32t, i32t],              [i8t]),
        eq:            decl!("jit_eq",         [ptr, i32t, i32t, i32t],              []),
        ne:            decl!("jit_ne",         [ptr, i32t, i32t, i32t],              []),
        lt:            decl!("jit_lt",         [ptr, i32t, i32t, i32t],              [i8t]),
        le:            decl!("jit_le",         [ptr, i32t, i32t, i32t],              [i8t]),
        gt:            decl!("jit_gt",         [ptr, i32t, i32t, i32t],              [i8t]),
        ge:            decl!("jit_ge",         [ptr, i32t, i32t, i32t],              [i8t]),
        and:           decl!("jit_and",        [ptr, i32t, i32t, i32t],              [i8t]),
        or:            decl!("jit_or",         [ptr, i32t, i32t, i32t],              [i8t]),
        not:           decl!("jit_not",        [ptr, i32t, i32t],                    [i8t]),
        neg:           decl!("jit_neg",        [ptr, i32t, i32t],                    [i8t]),
        bit_and:       decl!("jit_bit_and",    [ptr, i32t, i32t, i32t],              [i8t]),
        bit_or:        decl!("jit_bit_or",     [ptr, i32t, i32t, i32t],              [i8t]),
        bit_xor:       decl!("jit_bit_xor",    [ptr, i32t, i32t, i32t],              [i8t]),
        bit_not:       decl!("jit_bit_not",    [ptr, i32t, i32t],                    [i8t]),
        shl:           decl!("jit_shl",        [ptr, i32t, i32t, i32t],              [i8t]),
        shr:           decl!("jit_shr",        [ptr, i32t, i32t, i32t],              [i8t]),
        // jit_store(frame, var_ptr, var_len, src)  -> void
        store:         decl!("jit_store",      [ptr, ptr, i64t, i32t],               []),
        // jit_load(frame, dst, var_ptr, var_len)   -> u8
        load:          decl!("jit_load",       [ptr, i32t, ptr, i64t],               [i8t]),
        str_concat:    decl!("jit_str_concat", [ptr, i32t, i32t, i32t],              [i8t]),
        to_str:        decl!("jit_to_str",     [ptr, i32t, i32t],                    []),
        // jit_call(frame, ctx, dst, name_ptr, name_len, args_ptr, argc) -> u8
        call:          decl!("jit_call",       [ptr, ptr, i32t, ptr, i64t, ptr, i64t], [i8t]),
        // jit_builtin(frame, dst, name_ptr, name_len, args_ptr, argc) -> u8
        builtin:       decl!("jit_builtin",    [ptr, i32t, ptr, i64t, ptr, i64t],     [i8t]),
        array_new:     decl!("jit_array_new",     [ptr, i32t, i32t],                 [i8t]),
        array_new_lit: decl!("jit_array_new_lit", [ptr, i32t, ptr, i64t],            []),
        array_get:     decl!("jit_array_get",     [ptr, i32t, i32t, i32t],           [i8t]),
        array_set:     decl!("jit_array_set",     [ptr, i32t, i32t, i32t],           [i8t]),
        array_len:     decl!("jit_array_len",     [ptr, i32t, i32t],                 [i8t]),
        // jit_obj_new(frame, ctx, dst, cls_ptr, cls_len, args_ptr, argc) -> u8
        obj_new:       decl!("jit_obj_new",    [ptr, ptr, i32t, ptr, i64t, ptr, i64t], [i8t]),
        field_get:     decl!("jit_field_get",  [ptr, i32t, i32t, ptr, i64t],          [i8t]),
        field_set:     decl!("jit_field_set",  [ptr, i32t, ptr, i64t, i32t],          [i8t]),
        // jit_vcall(frame, ctx, dst, obj, method_ptr, method_len, args_ptr, argc) -> u8
        vcall:         decl!("jit_vcall",      [ptr, ptr, i32t, i32t, ptr, i64t, ptr, i64t], [i8t]),
        is_instance:   decl!("jit_is_instance",[ptr, ptr, i32t, i32t, ptr, i64t],    []),
        as_cast:       decl!("jit_as_cast",    [ptr, ptr, i32t, i32t, ptr, i64t],    []),
        static_get:    decl!("jit_static_get", [ptr, i32t, ptr, i64t],               []),
        static_set:    decl!("jit_static_set", [ptr, ptr, i64t, i32t],               []),
        get_bool:      decl!("jit_get_bool",      [ptr, i32t],                       [i8t]),
        set_ret:       decl!("jit_set_ret",       [ptr, i32t],                       []),
        throw:         decl!("jit_throw",         [ptr, i32t],                       []),
        install_catch: decl!("jit_install_catch", [ptr, i32t],                       []),
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
                Instruction::Store     { .. }        => None,
                Instruction::Load      { dst, .. }  => Some(*dst),
                Instruction::StrConcat { dst, .. }  => Some(*dst),
                Instruction::ToStr     { dst, .. }  => Some(*dst),
                Instruction::Call      { dst, .. }  => Some(*dst),
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

fn find_handler_entry(func: &Function, block_idx: usize) -> Option<usize> {
    for (i, entry) in func.exception_table.iter().enumerate() {
        let start = func.blocks.iter().position(|b| b.label == entry.try_start)?;
        let end   = func.blocks.iter().position(|b| b.label == entry.try_end)?;
        if block_idx >= start && block_idx < end {
            return Some(i);
        }
    }
    None
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
    let hr_store         = imp!(helper_ids.store);
    let hr_load          = imp!(helper_ids.load);
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
    let hr_throw         = imp!(helper_ids.throw);
    let hr_install_catch = imp!(helper_ids.install_catch);

    // ── Translate each z42 block ──────────────────────────────────────────────
    for (block_idx, z42_block) in z42_func.blocks.iter().enumerate() {
        if block_idx != 0 {
            builder.switch_to_block(cl_blocks[block_idx]);
        }

        // Find enclosing exception-handler entry for this block (if any).
        let catch_info: Option<(cranelift_codegen::ir::Block, u32)> =
            find_handler_entry(z42_func, block_idx).map(|ei| {
                let entry      = &z42_func.exception_table[ei];
                let catch_idx  = z42_func.blocks.iter().position(|b| b.label == entry.catch_label)
                    .expect("catch_label block must exist");
                (cl_blocks[catch_idx], entry.catch_reg)
            });

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

        // After a helper call that returns u8: branch to catch or return 1 on error.
        // Blocks are NOT sealed here; seal_all_blocks() is called once after all
        // control-flow edges are established (handles back-edges in loops correctly).
        macro_rules! check {
            ($ret:expr) => {{
                let ok_blk  = builder.create_block();
                if let Some((catch_cl, catch_reg)) = catch_info {
                    let exc_blk = builder.create_block();
                    builder.ins().brif($ret, exc_blk, &[], ok_blk, &[]);
                    builder.switch_to_block(exc_blk);
                    let creg = ri!(catch_reg);
                    builder.ins().call(hr_install_catch, &[frame_val, creg]);
                    builder.ins().jump(catch_cl, &[]);
                } else {
                    let exc_blk = builder.create_block();
                    builder.ins().brif($ret, exc_blk, &[], ok_blk, &[]);
                    builder.switch_to_block(exc_blk);
                    let one = builder.ins().iconst(types::I8, 1);
                    builder.ins().return_(&[one]);
                }
                builder.switch_to_block(ok_blk);
            }};
        }

        // ── Instruction translation ───────────────────────────────────────────
        for instr in &z42_block.instructions {
            match instr {
                Instruction::ConstI32 { dst, val } => {
                    let d = ri!(*dst); let v = builder.ins().iconst(types::I32, *val as i64);
                    builder.ins().call(hr_const_i32, &[frame_val, d, v]);
                }
                Instruction::ConstI64 { dst, val } => {
                    let d = ri!(*dst); let v = builder.ins().iconst(types::I64, *val);
                    builder.ins().call(hr_const_i64, &[frame_val, d, v]);
                }
                Instruction::ConstF64 { dst, val } => {
                    let d = ri!(*dst); let v = builder.ins().f64const(*val);
                    builder.ins().call(hr_const_f64, &[frame_val, d, v]);
                }
                Instruction::ConstBool { dst, val } => {
                    let d = ri!(*dst); let v = builder.ins().iconst(types::I8, if *val { 1 } else { 0 });
                    builder.ins().call(hr_const_bool, &[frame_val, d, v]);
                }
                Instruction::ConstNull { dst } => {
                    let d = ri!(*dst);
                    builder.ins().call(hr_const_null, &[frame_val, d]);
                }
                Instruction::ConstStr { dst, idx } => {
                    let d = ri!(*dst); let i = ri!(*idx);
                    let inst = builder.ins().call(hr_const_str, &[frame_val, ctx_val, d, i]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Copy { dst, src } => {
                    let d = ri!(*dst); let s = ri!(*src);
                    builder.ins().call(hr_copy, &[frame_val, d, s]);
                }

                // Arithmetic
                Instruction::Add { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_add, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Sub { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_sub, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Mul { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_mul, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Div { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_div, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Rem { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_rem, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // Comparison
                Instruction::Eq { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    builder.ins().call(hr_eq, &[frame_val, d, av, bv]);
                }
                Instruction::Ne { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    builder.ins().call(hr_ne, &[frame_val, d, av, bv]);
                }
                Instruction::Lt { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_lt, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Le { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_le, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Gt { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_gt, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Ge { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_ge, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // Logical
                Instruction::And { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_and, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Or { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_or, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Not { dst, src } => {
                    let d = ri!(*dst); let s = ri!(*src);
                    let inst = builder.ins().call(hr_not, &[frame_val, d, s]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // Unary arithmetic
                Instruction::Neg { dst, src } => {
                    let d = ri!(*dst); let s = ri!(*src);
                    let inst = builder.ins().call(hr_neg, &[frame_val, d, s]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // Bitwise
                Instruction::BitAnd { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_bit_and, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::BitOr { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_bit_or, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::BitXor { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_bit_xor, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::BitNot { dst, src } => {
                    let d = ri!(*dst); let s = ri!(*src);
                    let inst = builder.ins().call(hr_bit_not, &[frame_val, d, s]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Shl { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_shl, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::Shr { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_shr, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // Variable slots
                Instruction::Store { var, src } => {
                    let (sptr, slen) = str_val!(var);
                    let sv = ri!(*src);
                    builder.ins().call(hr_store, &[frame_val, sptr, slen, sv]);
                }
                Instruction::Load { dst, var } => {
                    let d = ri!(*dst);
                    let (vptr, vlen) = str_val!(var);
                    let inst = builder.ins().call(hr_load, &[frame_val, d, vptr, vlen]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // String
                Instruction::StrConcat { dst, a, b } => {
                    let (d, av, bv) = (ri!(*dst), ri!(*a), ri!(*b));
                    let inst = builder.ins().call(hr_str_concat, &[frame_val, d, av, bv]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::ToStr { dst, src } => {
                    let d = ri!(*dst); let s = ri!(*src);
                    builder.ins().call(hr_to_str, &[frame_val, d, s]);
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
                    let inst = builder.ins().call(hr_builtin, &[frame_val, d, np, nl, ap, al]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // Arrays
                Instruction::ArrayNew { dst, size } => {
                    let d = ri!(*dst); let s = ri!(*size);
                    let inst = builder.ins().call(hr_array_new, &[frame_val, d, s]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::ArrayNewLit { dst, elems } => {
                    let d = ri!(*dst);
                    let (ep, el) = regs_val!(elems);
                    builder.ins().call(hr_array_new_lit, &[frame_val, d, ep, el]);
                }
                Instruction::ArrayGet { dst, arr, idx } => {
                    let d = ri!(*dst); let a = ri!(*arr); let i = ri!(*idx);
                    let inst = builder.ins().call(hr_array_get, &[frame_val, d, a, i]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::ArraySet { arr, idx, val } => {
                    let a = ri!(*arr); let i = ri!(*idx); let v = ri!(*val);
                    let inst = builder.ins().call(hr_array_set, &[frame_val, a, i, v]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::ArrayLen { dst, arr } => {
                    let d = ri!(*dst); let a = ri!(*arr);
                    let inst = builder.ins().call(hr_array_len, &[frame_val, d, a]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // Objects
                Instruction::ObjNew { dst, class_name, args } => {
                    let d = ri!(*dst);
                    let (cp, cl) = str_val!(class_name);
                    let (ap, al) = regs_val!(args);
                    let inst = builder.ins().call(hr_obj_new, &[frame_val, ctx_val, d, cp, cl, ap, al]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::FieldGet { dst, obj, field_name } => {
                    let d = ri!(*dst); let o = ri!(*obj);
                    let (fp, fl) = str_val!(field_name);
                    let inst = builder.ins().call(hr_field_get, &[frame_val, d, o, fp, fl]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::FieldSet { obj, field_name, val } => {
                    let o = ri!(*obj);
                    let (fp, fl) = str_val!(field_name);
                    let v = ri!(*val);
                    let inst = builder.ins().call(hr_field_set, &[frame_val, o, fp, fl, v]);
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
                    builder.ins().call(hr_static_get, &[frame_val, d, fp, fl]);
                }
                Instruction::StaticSet { field, val } => {
                    let (fp, fl) = str_val!(field);
                    let v = ri!(*val);
                    builder.ins().call(hr_static_set, &[frame_val, fp, fl, v]);
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
                builder.ins().call(hr_set_ret, &[frame_val, rv]);
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
                let inst = builder.ins().call(hr_get_bool, &[frame_val, cv]);
                let b    = builder.inst_results(inst)[0];

                let true_idx  = z42_func.blocks.iter().position(|blk| &blk.label == true_label)
                    .expect("true_label not found");
                let false_idx = z42_func.blocks.iter().position(|blk| &blk.label == false_label)
                    .expect("false_label not found");

                builder.ins().brif(b, cl_blocks[true_idx], &[], cl_blocks[false_idx], &[]);
            }
            Terminator::Throw { reg } => {
                let rv = ri!(*reg);
                builder.ins().call(hr_throw, &[frame_val, rv]);

                if let Some((catch_cl, catch_reg)) = catch_info {
                    let creg = ri!(catch_reg);
                    builder.ins().call(hr_install_catch, &[frame_val, creg]);
                    builder.ins().jump(catch_cl, &[]);
                } else {
                    let one = builder.ins().iconst(types::I8, 1);
                    builder.ins().return_(&[one]);
                }
            }
        }

    }

    builder.seal_all_blocks();
    builder.finalize();

    jit.define_function(func_id, &mut ctx)?;
    jit.clear_context(&mut ctx);
    Ok(())
}
