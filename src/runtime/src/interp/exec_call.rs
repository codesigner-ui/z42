/// Call-related instructions: direct calls, builtins, function references,
/// indirect calls (delegate / closure dispatch), closure construction.
///
/// Helpers that may propagate a user exception from a callee return
/// `Result<Option<Value>>` (Some = thrown). Pure helpers return `Result<()>`.

use crate::metadata::{Module, Value};
use crate::vm_context::VmContext;
use anyhow::{bail, Result};

use super::ops::collect_args;
use super::{ExecOutcome, Frame};

pub(super) fn call(
    ctx: &VmContext, module: &Module, frame: &mut Frame,
    dst: u32, fname: &str, args: &[u32],
) -> Result<Option<Value>> {
    let arg_vals = collect_args(&frame.regs, args)?;
    let callee_fn = module.func_index.get(fname)
        .and_then(|&idx| module.functions.get(idx));
    let outcome = if let Some(callee) = callee_fn {
        super::exec_function(ctx, module, callee, &arg_vals)?
    } else if let Some(lazy_fn) = ctx.try_lookup_function(fname) {
        super::exec_function(ctx, module, lazy_fn.as_ref(), &arg_vals)?
    } else {
        bail!("undefined function `{fname}`");
    };
    match outcome {
        ExecOutcome::Returned(ret) => {
            frame.set(dst, ret.unwrap_or(Value::Null));
            Ok(None)
        }
        ExecOutcome::Thrown(val) => Ok(Some(val)),
    }
}

pub(super) fn builtin(
    ctx: &VmContext, frame: &mut Frame, dst: u32, name: &str, args: &[u32],
) -> Result<()> {
    let arg_vals = collect_args(&frame.regs, args)?;
    let result = crate::corelib::exec_builtin(ctx, name, &arg_vals)?;
    frame.set(dst, result);
    Ok(())
}

/// L2 no-capture lambda lifting: push a function reference value.
/// See docs/design/closure.md §6 + ir.md.
pub(super) fn load_fn(frame: &mut Frame, dst: u32, func: &str) {
    frame.set(dst, Value::FuncRef(func.to_string()));
}

/// 2026-05-02 add-method-group-conversion (D1b): cached method group
/// conversion. First execution constructs `Value::FuncRef(func)` and
/// stores it into the module-level slot; subsequent hits read the slot.
pub(super) fn load_fn_cached(
    ctx: &VmContext, frame: &mut Frame, dst: u32, func: &str, slot_id: u32,
) {
    let cached = ctx.func_ref_slot(slot_id);
    let value = if matches!(cached, Value::Null) {
        let v = Value::FuncRef(func.to_string());
        ctx.set_func_ref_slot(slot_id, v.clone());
        v
    } else {
        cached
    };
    frame.set(dst, value);
}

/// Indirect call: dispatch on FuncRef (no-capture) or Closure (capturing).
/// For Closures, env is prepended to the user args as the lifted body's
/// implicit first parameter. See closure.md §6.
pub(super) fn call_indirect(
    ctx: &VmContext, module: &Module, frame: &mut Frame,
    dst: u32, callee: u32, args: &[u32],
) -> Result<Option<Value>> {
    // env 解码：FuncRef → 无 env；Closure → heap GcRef；StackClosure
    // → 从当前 frame.env_arena 复制出 Vec（新 GcRef，callee 内 lifetime
    //   独立于 caller frame，避免 caller 弹出 arena 后 use-after-free）
    let (fname, env_vec_opt): (String, Option<Vec<Value>>) = match frame.get(callee)? {
        Value::FuncRef(name)               => (name.clone(), None),
        Value::Closure { env, fn_name }    => (fn_name.clone(), Some(env.borrow().clone())),
        Value::StackClosure { env_idx, fn_name } => {
            let idx = *env_idx as usize;
            if idx >= frame.env_arena.len() {
                bail!("CallIndirect: stack closure env_idx {} out of bounds (arena_len={})",
                      idx, frame.env_arena.len());
            }
            (fn_name.clone(), Some(frame.env_arena[idx].clone()))
        }
        other => bail!("CallIndirect: expected FuncRef / Closure / StackClosure, got {:?}", other),
    };
    let user_vals = collect_args(&frame.regs, args)?;
    let arg_vals: Vec<Value> = match env_vec_opt {
        None          => user_vals,
        Some(env_vec) => {
            // 升格为 heap GcRef 给 callee 用 —— callee 不区分 stack/heap closure。
            let env_val = ctx.heap().alloc_array(env_vec);
            let mut v = Vec::with_capacity(user_vals.len() + 1);
            v.push(env_val);
            v.extend(user_vals);
            v
        }
    };
    let callee_fn = module.func_index.get(fname.as_str())
        .and_then(|&idx| module.functions.get(idx));
    let outcome = if let Some(cfn) = callee_fn {
        super::exec_function(ctx, module, cfn, &arg_vals)?
    } else if let Some(lazy_fn) = ctx.try_lookup_function(&fname) {
        super::exec_function(ctx, module, lazy_fn.as_ref(), &arg_vals)?
    } else {
        bail!("CallIndirect: undefined function `{fname}`");
    };
    match outcome {
        ExecOutcome::Returned(ret) => {
            frame.set(dst, ret.unwrap_or(Value::Null));
            Ok(None)
        }
        ExecOutcome::Thrown(val) => Ok(Some(val)),
    }
}

/// L3 closure construction. `stack_alloc=true` 走 frame-local arena
///（impl-closure-l3-escape-stack）；否则 heap 路径（原 Tier C）。
pub(super) fn mk_clos(
    ctx: &VmContext, frame: &mut Frame,
    dst: u32, fn_name: &str, captures: &[u32], stack_alloc: bool,
) -> Result<()> {
    let mut env_vec: Vec<Value> = Vec::with_capacity(captures.len());
    for r in captures {
        env_vec.push(frame.get(*r)?.clone());
    }
    let value = if stack_alloc {
        let idx = frame.env_arena.len() as u32;
        frame.env_arena.push(env_vec);
        Value::StackClosure { env_idx: idx, fn_name: fn_name.to_string() }
    } else {
        let env_val = ctx.heap().alloc_array(env_vec);
        let env = match env_val {
            Value::Array(rc) => rc,
            _ => unreachable!("alloc_array must return Value::Array"),
        };
        Value::Closure { env, fn_name: fn_name.to_string() }
    };
    frame.set(dst, value);
    Ok(())
}
