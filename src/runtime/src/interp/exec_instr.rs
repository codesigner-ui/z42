/// Single-instruction dispatch for the interpreter.
///
/// This file is a thin dispatcher: each `Instruction` variant matches one arm
/// that delegates to a per-category helper (see sibling `exec_*.rs` modules).
/// The match is **exhaustive** ([runtime-rust.md](../../../../.claude/rules/runtime-rust.md)
/// "不允许有 `_` 通配兜底"); adding a new `Instruction` variant produces a
/// compile error here, forcing the matching helper / category decision.
///
/// Helpers that may propagate a callee user exception return
/// `Result<Option<Value>>` and the dispatcher checks `is_some()` to forward
/// the throw upstack. All other helpers return `Result<()>`.

use crate::metadata::{Function, Instruction, Module, Value};
use crate::metadata::tokens::UNRESOLVED;
use crate::vm_context::VmContext;
use anyhow::Result;

use super::Frame;

/// Execute a single instruction.
/// Returns:
///   Ok(None)       — normal completion
///   Ok(Some(val))  — a callee threw a user exception (value-based propagation)
///   Err(e)         — internal VM error
///
/// `func` / `block_idx` / `instr_idx` are passed through for the
/// introduce-method-token Phase 4 dispatch fast path. Token-bearing
/// helpers (Call / Builtin / ObjNew / VCall / FieldGet / FieldSet /
/// StaticGet / StaticSet) read `func.resolved.site_index[block_idx]
/// [instr_idx]` to find their per-kind cache slot. Non-token-bearing
/// instructions ignore these parameters.
pub fn exec_instr(
    ctx: &VmContext, module: &Module, frame: &mut Frame,
    func: &Function, block_idx: usize, instr_idx: usize,
    instr: &Instruction,
) -> Result<Option<Value>> {
    use super::{exec_address, exec_array, exec_call, exec_object, exec_value, exec_vcall};
    #[cfg(feature = "native-interop")]
    use super::exec_native;

    // Look up per-instruction site_idx (UNRESOLVED if non-token-bearing or
    // resolver hasn't run). `resolved` is None only if Vm::run hasn't been
    // called yet (e.g. unit tests calling exec_function directly without
    // resolver hookup) — helpers fall back to string lookup in that case.
    let resolved = func.resolved.get();
    let _site_idx = resolved
        .and_then(|r| r.site_index.get(block_idx))
        .and_then(|b| b.get(instr_idx).copied())
        .unwrap_or(UNRESOLVED);
    // ↑ `_site_idx` is read by token-bearing arms below; hidden behind `_`
    //   prefix so the variable isn't a warning when no consumer is enabled.

    match instr {
        // ── Constants ────────────────────────────────────────────────────────
        Instruction::ConstStr  { dst, idx } => exec_value::const_str(ctx, module, frame, *dst, *idx)?,
        Instruction::ConstI32  { dst, val } => exec_value::const_i32(frame, *dst, *val),
        Instruction::ConstI64  { dst, val } => exec_value::const_i64(frame, *dst, *val),
        Instruction::ConstF64  { dst, val } => exec_value::const_f64(frame, *dst, *val),
        Instruction::ConstBool { dst, val } => exec_value::const_bool(frame, *dst, *val),
        Instruction::ConstChar { dst, val } => exec_value::const_char(frame, *dst, *val),
        Instruction::ConstNull { dst }      => exec_value::const_null(frame, *dst),
        Instruction::Copy      { dst, src } => exec_value::copy(frame, *dst, *src)?,

        // ── Arithmetic ───────────────────────────────────────────────────────
        Instruction::Add { dst, a, b } => exec_value::add(frame, *dst, *a, *b)?,
        Instruction::Sub { dst, a, b } => exec_value::sub(frame, *dst, *a, *b)?,
        Instruction::Mul { dst, a, b } => exec_value::mul(frame, *dst, *a, *b)?,
        Instruction::Div { dst, a, b } => exec_value::div(frame, *dst, *a, *b)?,
        Instruction::Rem { dst, a, b } => exec_value::rem(frame, *dst, *a, *b)?,

        // ── Comparison ───────────────────────────────────────────────────────
        Instruction::Eq { dst, a, b } => exec_value::eq(frame, *dst, *a, *b)?,
        Instruction::Ne { dst, a, b } => exec_value::ne(frame, *dst, *a, *b)?,
        Instruction::Lt { dst, a, b } => exec_value::lt(frame, *dst, *a, *b)?,
        Instruction::Le { dst, a, b } => exec_value::le(frame, *dst, *a, *b)?,
        Instruction::Gt { dst, a, b } => exec_value::gt(frame, *dst, *a, *b)?,
        Instruction::Ge { dst, a, b } => exec_value::ge(frame, *dst, *a, *b)?,

        // ── Logical ──────────────────────────────────────────────────────────
        Instruction::And { dst, a, b } => exec_value::and(frame, *dst, *a, *b)?,
        Instruction::Or  { dst, a, b } => exec_value::or(frame, *dst, *a, *b)?,
        Instruction::Not { dst, src }  => exec_value::not(frame, *dst, *src)?,

        // ── Unary ────────────────────────────────────────────────────────────
        Instruction::Neg { dst, src } => exec_value::neg(frame, *dst, *src)?,

        // ── Bitwise ──────────────────────────────────────────────────────────
        Instruction::BitAnd { dst, a, b } => exec_value::bit_and(frame, *dst, *a, *b)?,
        Instruction::BitOr  { dst, a, b } => exec_value::bit_or(frame, *dst, *a, *b)?,
        Instruction::BitXor { dst, a, b } => exec_value::bit_xor(frame, *dst, *a, *b)?,
        Instruction::BitNot { dst, src }  => exec_value::bit_not(frame, *dst, *src)?,
        Instruction::Shl    { dst, a, b } => exec_value::shl(frame, *dst, *a, *b)?,
        Instruction::Shr    { dst, a, b } => exec_value::shr(frame, *dst, *a, *b)?,

        // ── String formation ─────────────────────────────────────────────────
        Instruction::StrConcat { dst, a, b } => exec_value::str_concat(frame, *dst, *a, *b)?,
        Instruction::ToStr     { dst, src }  => exec_value::to_str(ctx, module, frame, *dst, *src)?,

        // ── Address-load (spec impl-ref-out-in-runtime) ─────────────────────
        Instruction::LoadLocalAddr { dst, slot } => exec_address::load_local_addr(ctx, frame, *dst, *slot),
        Instruction::LoadElemAddr  { dst, arr, idx } => exec_address::load_elem_addr(frame, *dst, *arr, *idx)?,
        Instruction::LoadFieldAddr { dst, obj, field_name } => exec_address::load_field_addr(frame, *dst, *obj, field_name)?,

        // ── Generic default(T) at runtime (D-8b-3 Phase 2) ──────────────────
        Instruction::DefaultOf { dst, param_index } => exec_address::default_of(frame, *dst, *param_index),

        // ── Numeric cast (fix-numeric-cast-lowering, 2026-05-13) ────────────
        Instruction::Convert { dst, src, to_tag } => exec_value::convert(frame, *dst, *src, *to_tag)?,

        // ── Calls ────────────────────────────────────────────────────────────
        Instruction::Call { dst, func: fname, args } => {
            // 2026-05-10 exception-stack-trace: stamp current site's source
            // line on this frame's FrameInfo before descending into the
            // callee, so a downstream `throw` snapshot shows our call site.
            update_caller_line(ctx, func, block_idx, instr_idx);

            // Hot path: pre-resolved MethodId direct-indexes module.functions.
            // Cross-zpkg cache (UNRESOLVED at load) backfills on first hit.
            let method_token = resolved
                .filter(|_| _site_idx != UNRESOLVED)
                .and_then(|r| r.method_tokens.get(_site_idx as usize));
            if let Some(thrown) = exec_call::call(ctx, module, frame, *dst, fname, args, method_token)? {
                return Ok(Some(thrown));
            }
        }
        Instruction::Builtin { dst, name, args } => {
            // Hot path: resolver populates Function.resolved.builtin_tokens
            // with BuiltinId per site at load time (closed set, all hits).
            // Fallback to name lookup when resolver hasn't run.
            let builtin_id = resolved
                .filter(|_| _site_idx != UNRESOLVED)
                .and_then(|r| r.builtin_tokens.get(_site_idx as usize).copied());
            exec_call::builtin(ctx, frame, *dst, name, args, builtin_id)?;
        }
        Instruction::LoadFn { dst, func } => exec_call::load_fn(frame, *dst, func),
        Instruction::LoadFnCached { dst, func, slot_id } => exec_call::load_fn_cached(ctx, frame, *dst, func, *slot_id),
        Instruction::CallIndirect { dst, callee, args } => {
            update_caller_line(ctx, func, block_idx, instr_idx);
            if let Some(thrown) = exec_call::call_indirect(ctx, module, frame, *dst, *callee, args)? {
                return Ok(Some(thrown));
            }
        }
        Instruction::MkClos { dst, fn_name, captures, stack_alloc } => {
            exec_call::mk_clos(ctx, frame, *dst, fn_name, captures, *stack_alloc)?
        }

        // ── Arrays ───────────────────────────────────────────────────────────
        Instruction::ArrayNew    { dst, size }      => exec_array::array_new(ctx, frame, *dst, *size)?,
        Instruction::ArrayNewLit { dst, elems }     => exec_array::array_new_lit(ctx, frame, *dst, elems)?,
        Instruction::ArrayGet    { dst, arr, idx }  => exec_array::array_get(frame, *dst, *arr, *idx)?,
        Instruction::ArraySet    { arr, idx, val }  => exec_array::array_set(frame, *arr, *idx, *val)?,
        Instruction::ArrayLen    { dst, arr }       => exec_array::array_len(frame, *dst, *arr)?,

        // ── Objects ──────────────────────────────────────────────────────────
        Instruction::ObjNew { dst, class_name, ctor_name, args, type_args } => {
            // Hot path: pass type_token cache for repopulation. Dispatch via
            // type_registry / lazy_loader unchanged.
            let type_token = resolved
                .filter(|_| _site_idx != UNRESOLVED)
                .and_then(|r| r.type_tokens.get(_site_idx as usize));
            exec_object::obj_new(ctx, module, frame, *dst, class_name, ctor_name, args, type_args, type_token)?
        }
        Instruction::FieldGet { dst, obj, field_name } => {
            let field_ic = resolved
                .filter(|_| _site_idx != UNRESOLVED)
                .and_then(|r| r.field_ic.get(_site_idx as usize));
            exec_object::field_get(frame, *dst, *obj, field_name, field_ic)?;
        }
        Instruction::FieldSet { obj, field_name, val } => {
            let field_ic = resolved
                .filter(|_| _site_idx != UNRESOLVED)
                .and_then(|r| r.field_ic.get(_site_idx as usize));
            exec_object::field_set(frame, *obj, field_name, *val, field_ic)?;
        }
        Instruction::VCall { dst, obj, method, args } => {
            update_caller_line(ctx, func, block_idx, instr_idx);
            // Hot path: monomorphic inline cache fires when receiver TypeId
            // matches the cached one at this site (same site + same recv type).
            // Polymorphic sites overwrite the slot each time (Phase 1 mono IC).
            let vcall_ic = resolved
                .filter(|_| _site_idx != UNRESOLVED)
                .and_then(|r| r.vcall_ic.get(_site_idx as usize));
            if let Some(thrown) = exec_vcall::vcall(ctx, module, frame, *dst, *obj, method, args, vcall_ic)? {
                return Ok(Some(thrown));
            }
        }
        Instruction::IsInstance { dst, obj, class_name } => exec_object::is_instance(ctx, module, frame, *dst, *obj, class_name)?,
        Instruction::AsCast     { dst, obj, class_name } => exec_object::as_cast(ctx, module, frame, *dst, *obj, class_name)?,
        Instruction::StaticGet  { dst, field } => {
            // Hot path: pre-resolved StaticFieldId → direct Vec index.
            use std::sync::atomic::Ordering;
            let field_id = resolved
                .filter(|_| _site_idx != UNRESOLVED)
                .and_then(|r| r.static_field_tokens.get(_site_idx as usize))
                .map(|atom| atom.load(Ordering::Relaxed))
                .filter(|&id| id != UNRESOLVED);
            exec_object::static_get(ctx, frame, *dst, field, field_id);
        }
        Instruction::StaticSet  { field, val } => {
            use std::sync::atomic::Ordering;
            let field_id = resolved
                .filter(|_| _site_idx != UNRESOLVED)
                .and_then(|r| r.static_field_tokens.get(_site_idx as usize))
                .map(|atom| atom.load(Ordering::Relaxed))
                .filter(|&id| id != UNRESOLVED);
            exec_object::static_set(ctx, frame, field, *val, field_id)?;
        }

        // ── Native interop ───────────────────────────────────────────────────
        // 2026-05-12 add-platform-wasm Stage 0: feature `native-interop`
        // gates all four opcodes. wasm builds bail with a clear message
        // (these opcodes shouldn't appear in a wasm-targeted .zbc anyway,
        // but malformed input shouldn't UAF the interp either).
        #[cfg(feature = "native-interop")]
        Instruction::CallNative { dst, module: m, type_name, symbol, args } => {
            // 2026-05-11 retire-z-codes: marshal failures throw
            // Std.InvalidMarshalException via Ok(Some(exc)).
            if let Some(thrown) = exec_native::call_native(ctx, module, frame, *dst, m, type_name, symbol, args)? {
                return Ok(Some(thrown));
            }
        }
        #[cfg(feature = "native-interop")]
        Instruction::CallNativeVtable { vtable_slot, .. } => exec_native::call_native_vtable(*vtable_slot)?,
        #[cfg(feature = "native-interop")]
        Instruction::PinPtr   { dst, src }   => {
            if let Some(thrown) = exec_native::pin_ptr(ctx, module, frame, *dst, *src)? {
                return Ok(Some(thrown));
            }
        }
        #[cfg(feature = "native-interop")]
        Instruction::UnpinPtr { pinned }     => exec_native::unpin_ptr(ctx, frame, *pinned)?,
        #[cfg(not(feature = "native-interop"))]
        Instruction::CallNative { .. }
        | Instruction::CallNativeVtable { .. }
        | Instruction::PinPtr { .. }
        | Instruction::UnpinPtr { .. } => {
            anyhow::bail!(
                "native interop opcode encountered in a build with `native-interop` feature disabled"
            );
        }
    }
    Ok(None)
}

/// 2026-05-10 exception-stack-trace: stamp the current source line of a
/// call-class instruction onto the executing frame's `FrameInfo` so a
/// downstream `throw` can format the call site (not 0). Cheap — one line
/// table linear scan + Cell::set.
#[inline]
fn update_caller_line(ctx: &VmContext, func: &Function, block_idx: usize, instr_idx: usize) {
    let (line, column) = super::resolve_line(&func.line_table, block_idx as u32, instr_idx as u32);
    ctx.update_top_frame_pos(line, column);
}
