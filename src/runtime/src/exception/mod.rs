//! Exception runtime — z42 exception object layout, propagation model,
//! and stack-trace capture.
//!
//! ## Propagation model
//!
//! - **interp**: exceptions flow as `ExecOutcome::Thrown(Value)`; no
//!   thread_local intermediary.
//! - **JIT**: in-flight exception lives in `VmContext::pending_exception`,
//!   reached from helper extern "C" via `(*jit_ctx).vm_ctx`.
//!
//! All previously-existing thread_local exception slots
//! (`interp::PENDING_EXCEPTION`, `jit::helpers::PENDING_EXCEPTION`) and
//! the `UserException` sentinel + `user_throw` / `user_exception_take` /
//! `sync_in_from_ctx` / `sync_out_to_ctx` bridges are gone (review2 §5.5
//! closed; review2 §3 fully tackled).
//!
//! ## Stack-trace capture (2026-05-10 exception-stack-trace)
//!
//! `VmContext.call_stack` holds one [`FrameInfo`] per active script frame,
//! pushed by `interp::exec_function` (paired with `frame_states` via the
//! existing `FrameGuard`). Caller frames record the line of the call site
//! before they invoke a callee, so a snapshot at throw time produces a
//! complete `<func> at <file>:<line>` chain.
//!
//! When a thrown value is an instance of `Std.Exception` (or subclass) and
//! its `StackTrace` field is `Value::Null`, the throw site populates the
//! field with the formatted trace. Re-thrown exceptions keep their
//! original trace (the null-check is the deduplication mechanism).

use std::cell::Cell;

use crate::metadata::types::TypeDesc;
use crate::metadata::{Module, Value};
use crate::vm_context::VmContext;

/// Per-frame metadata recorded for stack-trace formatting.
///
/// `line` is mutable via [`Cell`] so a caller can record the source line
/// of its call site just before it invokes a callee, without re-borrowing
/// the surrounding `RefCell<Vec<FrameInfo>>`.
#[derive(Debug)]
pub struct FrameInfo {
    pub func_name: String,
    pub file:      String,
    pub line:      Cell<u32>,
}

impl FrameInfo {
    pub fn new(func_name: String, file: String) -> Self {
        Self { func_name, file, line: Cell::new(0) }
    }

    /// Snapshot used at throw time (no Cell — value is frozen).
    pub fn snapshot(&self) -> FrameSnapshot {
        FrameSnapshot {
            func_name: self.func_name.clone(),
            file:      self.file.clone(),
            line:      self.line.get(),
        }
    }
}

/// Frozen view of a [`FrameInfo`] suitable for formatting / passing across
/// borrow scopes.
#[derive(Debug, Clone)]
pub struct FrameSnapshot {
    pub func_name: String,
    pub file:      String,
    pub line:      u32,
}

/// Format a captured trace as a multi-line string.
///
/// Frames are presented in caller-to-throw order — the throwing function
/// is **last**, matching .NET / Java convention (most-recent at the bottom).
/// Each line: `  at <func> (<file>:<line>)`.
pub fn format_stack_trace(frames: &[FrameSnapshot]) -> String {
    let mut out = String::new();
    for f in frames.iter().rev() {
        out.push_str("  at ");
        out.push_str(&f.func_name);
        if !f.file.is_empty() {
            out.push_str(" (");
            out.push_str(&f.file);
            if f.line > 0 {
                out.push(':');
                out.push_str(&f.line.to_string());
            }
            out.push(')');
        } else if f.line > 0 {
            out.push_str(" (line ");
            out.push_str(&f.line.to_string());
            out.push(')');
        }
        out.push('\n');
    }
    out.trim_end().to_string()
}

/// Walk the runtime base-class chain and decide whether `desc` is
/// `Std.Exception` or a subclass thereof. Used to gate StackTrace
/// population — only Exception-derived classes have the field.
pub fn is_exception_subclass(desc: &TypeDesc, module: &Module) -> bool {
    if desc.name == "Std.Exception" { return true; }
    let mut cur = desc.base_name.as_deref();
    while let Some(name) = cur {
        if name == "Std.Exception" { return true; }
        match module.type_registry.get(name) {
            Some(parent) => cur = parent.base_name.as_deref(),
            None => return false,
        }
    }
    false
}

/// Populate `value.StackTrace` with a snapshot of the current call stack
/// **iff** `value` is an instance of `Std.Exception` (or subclass) and
/// the field is currently `Value::Null`. Re-thrown exceptions keep their
/// original trace because the field is non-null after the first populate.
///
/// Phase 1: throws of non-Exception values (`throw "raw string"`) are a
/// no-op — they have no `StackTrace` field to populate.
pub fn populate_stack_trace(value: &Value, ctx: &VmContext, module: &Module) {
    let rc = match value {
        Value::Object(rc) => rc,
        _ => return,
    };

    // Step 1: read-only borrow to check shape + decide whether to populate.
    let (is_exc, slot_opt, is_null) = {
        let borrowed = rc.borrow();
        let is_exc = is_exception_subclass(&borrowed.type_desc, module);
        let slot   = borrowed.type_desc.field_index.get("StackTrace").copied();
        let is_null = match (is_exc, slot) {
            (true, Some(s)) => matches!(borrowed.slots.get(s), Some(Value::Null)),
            _               => false,
        };
        (is_exc, slot, is_null)
    };
    if !(is_exc && is_null) { return; }
    let slot = match slot_opt { Some(s) => s, None => return };

    // Step 2: snapshot stack outside the borrow (reads call_stack RefCell).
    let frames = ctx.snapshot_call_stack();
    let trace  = format_stack_trace(&frames);

    // Step 3: write-only borrow to set the field.
    let mut bm = rc.borrow_mut();
    if let Some(slot_val) = bm.slots.get_mut(slot) {
        if matches!(slot_val, Value::Null) {
            *slot_val = Value::Str(trace);
        }
    }
}

/// Read `Std.Exception.StackTrace` from a thrown value, if present and non-null.
/// Used by uncaught-exception output formatting.
pub fn read_stack_trace(value: &Value, module: &Module) -> Option<String> {
    let rc = match value {
        Value::Object(rc) => rc,
        _ => return None,
    };
    let borrowed = rc.borrow();
    if !is_exception_subclass(&borrowed.type_desc, module) { return None; }
    let slot = borrowed.type_desc.field_index.get("StackTrace").copied()?;
    match borrowed.slots.get(slot) {
        Some(Value::Str(s)) if !s.is_empty() => Some(s.clone()),
        _ => None,
    }
}

/// Format an uncaught exception value for top-level display. Combines the
/// thrown value's display form with its `StackTrace` field if it's an
/// Exception subclass that has one populated.
///
/// Used by [`crate::interp::run`] / [`crate::interp::run_returning`] /
/// [`crate::interp::run_with_static_init`] in their `Thrown` arm so all
/// three entry points produce consistent uncaught output.
pub fn format_uncaught(value: &Value, module: &Module) -> String {
    let header = match read_message(value, module) {
        Some(msg) => format!("uncaught exception: {}", msg),
        None      => format!("uncaught exception: {}", crate::corelib::convert::value_to_str(value)),
    };
    match read_stack_trace(value, module) {
        Some(trace) => format!("{header}\n{trace}"),
        None        => header,
    }
}

/// Read `Std.Exception.Message` from a thrown value, falling back to a
/// generic representation if the value isn't an Exception subclass.
pub fn read_message(value: &Value, module: &Module) -> Option<String> {
    let rc = match value {
        Value::Object(rc) => rc,
        _ => return None,
    };
    let borrowed = rc.borrow();
    if !is_exception_subclass(&borrowed.type_desc, module) { return None; }
    let slot = borrowed.type_desc.field_index.get("Message").copied()?;
    match borrowed.slots.get(slot) {
        Some(Value::Str(s)) => Some(s.clone()),
        _ => None,
    }
}

#[cfg(test)]
mod tests;
