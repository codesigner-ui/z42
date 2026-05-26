/// Cranelift IR translation: z42 SSA bytecode → native machine code.
///
/// One z42 basic block maps to one Cranelift block.
/// All value-level operations are dispatched to `extern "C"` helper functions
/// (see `helpers/`). Only branches, jumps, and function entry/exit are
/// emitted as inline Cranelift instructions.

use crate::metadata::{Function, Instruction, Terminator};
use anyhow::{bail, Result};
use cranelift_codegen::ir::{AbiParam, InstBuilder};
use cranelift_codegen::ir::types;
use cranelift_codegen::Context;
use cranelift_frontend::{FunctionBuilder, FunctionBuilderContext};
use cranelift_module::{FuncId, Module as CraneliftModule};
use cranelift_jit::JITModule;
use std::collections::HashMap;

pub use super::helpers::HelperIds;

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

                // spec fix-numeric-cast-lowering (2026-05-13)
                Instruction::Convert      { dst, .. } => Some(*dst),
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
    for (i, entry) in func.exception_table().iter().enumerate() {
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

/// formalize-jit-method-token Phase 2.C helper: look up the resolved
/// `MethodId.0` for a `Call` site. Returns `UNRESOLVED` (= u32::MAX)
/// for cross-zpkg lazy targets — `jit_call` falls back to name lookup.
fn method_id_at(func: &Function, block_idx: usize, instr_idx: usize) -> u32 {
    func.resolved.get()
        .and_then(|r| {
            let site = *r.site_index.get(block_idx)?.get(instr_idx)?;
            r.method_tokens.get(site as usize)
        })
        .map(|atom| atom.load(std::sync::atomic::Ordering::Relaxed))
        .unwrap_or(crate::metadata::tokens::UNRESOLVED)
}

/// formalize-jit-method-token Phase 2.E helper: stable raw pointer to
/// the `VCallIC` slot for the VCall site at `(block_idx, instr_idx)`.
/// Returns null when `Function.resolved` is unset (helper degrades to
/// non-IC slow path). The IC lives inside `Function.resolved.vcall_ic`
/// (which lives inside Module), so the pointer is valid for the entire
/// JitModule lifetime.
fn vcall_ic_ptr_at(func: &Function, block_idx: usize, instr_idx: usize) -> *const crate::metadata::resolver::VCallIC {
    func.resolved.get()
        .and_then(|r| {
            let site = *r.site_index.get(block_idx)?.get(instr_idx)?;
            r.vcall_ic.get(site as usize)
        })
        .map(|ic| ic as *const _)
        .unwrap_or(std::ptr::null())
}

/// formalize-jit-method-token Phase 2.E helper: stable raw pointer to
/// the `FieldIC` slot for a FieldGet/FieldSet site. Same lifetime
/// guarantees as `vcall_ic_ptr_at`.
fn field_ic_ptr_at(func: &Function, block_idx: usize, instr_idx: usize) -> *const crate::metadata::resolver::FieldIC {
    func.resolved.get()
        .and_then(|r| {
            let site = *r.site_index.get(block_idx)?.get(instr_idx)?;
            r.field_ic.get(site as usize)
        })
        .map(|ic| ic as *const _)
        .unwrap_or(std::ptr::null())
}

/// formalize-jit-method-token Phase 2 helper: look up the resolved
/// `StaticFieldId.0` for a `StaticGet` / `StaticSet` site at
/// `(block_idx, instr_idx)`. Falls back to a direct lookup for tests
/// that bypass `Vm::run` (which is the only path that populates
/// `Function.resolved`). Field name is captured for the fallback.
fn static_field_id_at(func: &Function, block_idx: usize, instr_idx: usize, _field: &str) -> u32 {
    func.resolved.get()
        .and_then(|r| {
            let site = *r.site_index.get(block_idx)?.get(instr_idx)?;
            r.static_field_tokens.get(site as usize)
        })
        .map(|atom| atom.load(std::sync::atomic::Ordering::Relaxed))
        .filter(|&id| id != crate::metadata::tokens::UNRESOLVED)
        .unwrap_or_else(|| {
            // Fallback: resolver hadn't run (only relevant for direct
            // compile_module callers in tests). Allocate id eagerly so
            // jit_static_get/set's by_id call doesn't see UNRESOLVED.
            // Note: this needs ctx; fallback is a corner case so we use
            // a sentinel that helper detects... but we can't reach ctx
            // here. Tests using this path either don't exist or run via
            // Vm::run. If this assertion fires, fix the test setup.
            panic!(
                "JIT codegen for StaticGet/Set at {:?}#{}.{} — Function.resolved missing. \
                 Caller must invoke metadata::resolver::resolve_module before JIT compile.",
                func.name, block_idx, instr_idx
            )
        })
}

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
    let hr_convert        = imp!(helper_ids.convert);
    // add-gc-safepoint-jit (2026-05-21): cooperative GC safepoint trampoline.
    let hr_check_safepoint = imp!(helper_ids.check_safepoint);

    // add-gc-safepoint-jit (2026-05-21): function-entry safepoint check.
    // A spawned worker that enters JIT-compiled code immediately after
    // spawn must respect a pending GC pause before touching any roots.
    // Idle fast path is one Mutex lock + one enum compare (~10ns).
    builder.ins().call(hr_check_safepoint, &[frame_val, ctx_val]);

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
                let entry      = &z42_func.exception_table()[ei];
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
        for (instr_idx, instr) in z42_block.instructions.iter().enumerate() {
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
                // formalize-jit-method-token Phase 2.C (2026-05-08): emit
                // pre-resolved MethodId + name (fallback for cross-zpkg).
                // Helper checks id first; UNRESOLVED → uses name HashMap.
                Instruction::Call { dst, func: fname, args } => {
                    let d = ri!(*dst);
                    let (np, nl) = str_val!(fname);
                    let (ap, al) = regs_val!(args);
                    let mid = method_id_at(z42_func, block_idx, instr_idx);
                    let mid_val = builder.ins().iconst(types::I32, mid as i64);
                    // 2026-05-10 jit-stack-trace + span-column-propagate: pass
                    // current source (line, col) so jit_call can stamp the
                    // caller's frame info before descending into the callee.
                    let (line, col) = crate::interp::resolve_line(z42_func.line_table(), block_idx as u32, instr_idx as u32);
                    let line_val = builder.ins().iconst(types::I32, line as i64);
                    let col_val  = builder.ins().iconst(types::I32, col as i64);
                    let inst = builder.ins().call(hr_call, &[frame_val, ctx_val, d, mid_val, np, nl, ap, al, line_val, col_val]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                    // add-gc-safepoint-jit (2026-05-21): post-Call safepoint
                    // — long callees may yield to a GC request that arrived
                    // partway through; the caller catches it on return.
                    builder.ins().call(hr_check_safepoint, &[frame_val, ctx_val]);
                }
                Instruction::Builtin { dst, name, args } => {
                    // formalize-jit-method-token Phase 2 (2026-05-08): emit
                    // pre-resolved BuiltinId as i32 const, drop name pointers.
                    // Resolver populates Function.resolved.builtin_tokens at
                    // load (closed set, never UNRESOLVED at this point).
                    let d = ri!(*dst);
                    let (ap, al) = regs_val!(args);
                    let builtin_id = z42_func.resolved.get()
                        .and_then(|r| {
                            let site = *r.site_index.get(block_idx)?.get(instr_idx)?;
                            r.builtin_tokens.get(site as usize).copied()
                        })
                        .unwrap_or_else(|| {
                            // Fallback: resolver hadn't run (shouldn't happen
                            // in production via Vm::run, but guards against
                            // direct compile_module callers in tests).
                            crate::corelib::builtin_id_of(name)
                                .unwrap_or_else(|| panic!("unknown builtin `{}`", name))
                                .0
                        });
                    let bid = builder.ins().iconst(types::I32, builtin_id as i64);
                    let inst = builder.ins().call(hr_builtin, &[frame_val, ctx_val, d, bid, ap, al]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }

                // Arrays
                Instruction::ArrayNew { dst, size, elem_tag } => {
                    let d = ri!(*dst); let s = ri!(*size);
                    let t = builder.ins().iconst(types::I8, *elem_tag as i64);
                    let inst = builder.ins().call(hr_array_new, &[frame_val, ctx_val, d, s, t]);
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
                Instruction::ObjNew { dst, class_name, ctor_name, args, type_args } => {
                    // 2026-05-07 expand-jit-type-args: marshal `Vec<String>` as a
                    // `*const String` + count to `jit_obj_new`. The IR storage
                    // lives for the module lifetime, so the raw pointer is valid
                    // for the duration of all JIT-compiled calls.
                    let d = ri!(*dst);
                    let (cp, cl) = str_val!(class_name);
                    let (kp, kl) = str_val!(ctor_name);
                    let (ap, al) = regs_val!(args);
                    let tap = builder.ins().iconst(ptr, type_args.as_ptr() as i64);
                    let tac = builder.ins().iconst(types::I64, type_args.len() as i64);
                    let inst = builder.ins().call(hr_obj_new,
                        &[frame_val, ctx_val, d, cp, cl, kp, kl, ap, al, tap, tac]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                // formalize-jit-method-token Phase 2.E (2026-05-08): emit
                // FieldIC pointer as i64 const so helper can take IC fast
                // path on monomorphic sites. Pointer is stable through
                // Function.resolved (OnceLock-set, never overwritten).
                Instruction::FieldGet { dst, obj, field_name } => {
                    let d = ri!(*dst); let o = ri!(*obj);
                    let (fp, fl) = str_val!(field_name);
                    let ic_ptr = field_ic_ptr_at(z42_func, block_idx, instr_idx);
                    let ic_val = builder.ins().iconst(ptr, ic_ptr as i64);
                    let inst = builder.ins().call(hr_field_get, &[frame_val, ctx_val, d, o, fp, fl, ic_val]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                Instruction::FieldSet { obj, field_name, val } => {
                    let o = ri!(*obj);
                    let (fp, fl) = str_val!(field_name);
                    let v = ri!(*val);
                    let ic_ptr = field_ic_ptr_at(z42_func, block_idx, instr_idx);
                    let ic_val = builder.ins().iconst(ptr, ic_ptr as i64);
                    let inst = builder.ins().call(hr_field_set, &[frame_val, ctx_val, o, fp, fl, v, ic_val]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                }
                // Phase 2.E: emit VCallIC pointer as trailing helper arg.
                Instruction::VCall { dst, obj, method, args } => {
                    let d = ri!(*dst); let o = ri!(*obj);
                    let (mp, ml) = str_val!(method);
                    let (ap, al) = regs_val!(args);
                    let ic_ptr = vcall_ic_ptr_at(z42_func, block_idx, instr_idx);
                    let ic_val = builder.ins().iconst(ptr, ic_ptr as i64);
                    // 2026-05-10 jit-stack-trace + span-column-propagate.
                    let (line, col) = crate::interp::resolve_line(z42_func.line_table(), block_idx as u32, instr_idx as u32);
                    let line_val = builder.ins().iconst(types::I32, line as i64);
                    let col_val  = builder.ins().iconst(types::I32, col as i64);
                    let inst = builder.ins().call(hr_vcall, &[frame_val, ctx_val, d, o, mp, ml, ap, al, ic_val, line_val, col_val]);
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
                // formalize-jit-method-token Phase 2 (2026-05-08): emit
                // pre-resolved StaticFieldId directly. Resolver populates
                // static_field_tokens at load via lazy ID allocation
                // (always succeeds), so id is never UNRESOLVED here.
                Instruction::StaticGet { dst, field } => {
                    let d = ri!(*dst);
                    let field_id = static_field_id_at(z42_func, block_idx, instr_idx, field);
                    let id_val = builder.ins().iconst(types::I32, field_id as i64);
                    builder.ins().call(hr_static_get, &[frame_val, ctx_val, d, id_val]);
                }
                Instruction::StaticSet { field, val } => {
                    let v = ri!(*val);
                    let field_id = static_field_id_at(z42_func, block_idx, instr_idx, field);
                    let id_val = builder.ins().iconst(types::I32, field_id as i64);
                    builder.ins().call(hr_static_set, &[frame_val, ctx_val, id_val, v]);
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

                // spec fix-numeric-cast-lowering (2026-05-13): explicit numeric cast
                Instruction::Convert { dst, src, to_tag } => {
                    let d = ri!(*dst);
                    let s = ri!(*src);
                    let t = builder.ins().iconst(types::I32, *to_tag as i64);
                    let inst = builder.ins().call(hr_convert, &[frame_val, ctx_val, d, s, t]);
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
                    // 2026-05-10 jit-stack-trace + span-column-propagate.
                    let (line, col) = crate::interp::resolve_line(z42_func.line_table(), block_idx as u32, instr_idx as u32);
                    let line_val = builder.ins().iconst(types::I32, line as i64);
                    let col_val  = builder.ins().iconst(types::I32, col as i64);
                    let inst = builder.ins().call(hr_call_indirect,
                        &[frame_val, ctx_val, d, c, ap, al, line_val, col_val]);
                    let ret  = builder.inst_results(inst)[0]; check!(ret);
                    // add-gc-safepoint-jit (2026-05-21): post-CallIndirect
                    // safepoint, see Instruction::Call for rationale.
                    builder.ins().call(hr_check_safepoint, &[frame_val, ctx_val]);
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
                // add-gc-safepoint-jit (2026-05-21): backward branch =
                // loop back-edge; check safepoint so long-running JIT
                // loops park promptly when GC requests a pause.
                if target <= block_idx {
                    builder.ins().call(hr_check_safepoint, &[frame_val, ctx_val]);
                }
                builder.ins().jump(cl_blocks[target], &[]);
            }
            Terminator::BrCond { cond, true_label, false_label } => {
                // add-gc-safepoint-jit (2026-05-21): BrCond's runtime target
                // isn't known until cond is evaluated; check unconditionally.
                // Idle fast path is cheap; this catches loops where the
                // back-edge is a BrCond rather than a Br.
                builder.ins().call(hr_check_safepoint, &[frame_val, ctx_val]);

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
                // 2026-05-10 jit-stack-trace + span-column-propagate: pass
                // the throw site's (line, col) so jit_throw can stamp the
                // throwing frame's FrameInfo before populating
                // Std.Exception.StackTrace. Throw is a block terminator;
                // mirror interp's "instr_idx = block.len()" so the position
                // resolves to the *last* LineEntry covering the block.
                let (line, col) = crate::interp::resolve_line(
                    z42_func.line_table(),
                    block_idx as u32,
                    z42_block.instructions.len() as u32,
                );
                let line_val = builder.ins().iconst(types::I32, line as i64);
                let col_val  = builder.ins().iconst(types::I32, col as i64);
                builder.ins().call(hr_throw, &[frame_val, ctx_val, rv, line_val, col_val]);
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
