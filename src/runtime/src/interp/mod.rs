/// Interpreter backend — tree-walking bytecode execution.
///
/// Implementation is split across submodules:
/// • mod.rs      — public API, Frame, core execution loop
/// • exec_instr.rs — instruction dispatch (one big match)
/// • dispatch.rs — object dispatch helpers (vtable, ToString, static fields)
/// • ops.rs      — register-level helpers (int_binop, numeric_lt, collect_args, …)

mod dispatch;
pub(crate) mod exec_instr;
mod ops;

pub use crate::corelib::convert::value_to_str;
use crate::metadata::{Function, Module, Terminator, Value};
use anyhow::{bail, Context, Result};
use std::cell::RefCell;
use std::collections::HashMap;

// ── Execution outcome ────────────────────────────────────────────────────────

/// Outcome of executing a function.
/// User exceptions are value-based (no heap allocation), not anyhow errors.
#[derive(Debug)]
pub(crate) enum ExecOutcome {
    /// Normal return (with optional return value).
    Returned(Option<Value>),
    /// User exception thrown and not caught within this function.
    Thrown(Value),
}

// ── User exception machinery (kept for JIT compatibility) ────────────────────

// Thread-local slot: used by JIT helpers that need extern "C" ABI.
// The interpreter path no longer uses this; it propagates via ExecOutcome::Thrown.
thread_local! {
    static PENDING_EXCEPTION: RefCell<Option<Value>> = const { RefCell::new(None) };
}

/// Lightweight sentinel for JIT path: `Send + Sync + 'static`.
#[derive(Debug)]
struct UserException;

impl std::fmt::Display for UserException {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let msg = PENDING_EXCEPTION.with(|p| {
            p.borrow().as_ref().map(value_to_str).unwrap_or_default()
        });
        write!(f, "uncaught exception: {msg}")
    }
}

impl std::error::Error for UserException {}

/// JIT-only: store exception value and return anyhow sentinel.
#[allow(dead_code)]
pub(crate) fn user_throw(val: Value) -> anyhow::Error {
    PENDING_EXCEPTION.with(|p| *p.borrow_mut() = Some(val));
    anyhow::Error::new(UserException)
}

pub(crate) fn user_exception_take() -> Option<Value> {
    PENDING_EXCEPTION.with(|p| p.borrow_mut().take())
}

// ── Public entry points ──────────────────────────────────────────────────────

/// Entry point: run a function with the given arguments.
pub fn run(module: &Module, func: &Function, args: &[Value]) -> Result<()> {
    match exec_function(module, func, args)? {
        ExecOutcome::Returned(_) => Ok(()),
        ExecOutcome::Thrown(val) => bail!("uncaught exception: {}", value_to_str(&val)),
    }
}

/// Run with static init: clears static fields, runs __static_init__ if present, then runs `func`.
pub fn run_with_static_init(module: &Module, func: &Function) -> Result<()> {
    dispatch::static_fields_clear();
    let init_name = format!("{}.__static_init__", module.name);
    if let Some(init_fn) = module.functions.iter().find(|f| f.name == init_name) {
        match exec_function(module, init_fn, &[])? {
            ExecOutcome::Returned(_) => {}
            ExecOutcome::Thrown(val) => bail!("uncaught exception in static init: {}", value_to_str(&val)),
        }
    }
    match exec_function(module, func, &[])? {
        ExecOutcome::Returned(_) => Ok(()),
        ExecOutcome::Thrown(val) => bail!("uncaught exception: {}", value_to_str(&val)),
    }
}

// ── Frame ────────────────────────────────────────────────────────────────────

pub(crate) struct Frame {
    pub regs: Vec<Value>,
}

impl Frame {
    pub fn new(args: &[Value], max_reg: u32) -> Self {
        let size = if max_reg > 0 { max_reg as usize } else { args.len() };
        let mut regs = vec![Value::Null; size.max(args.len())];
        for (i, v) in args.iter().enumerate() {
            regs[i] = v.clone();
        }
        Frame { regs }
    }

    pub fn set(&mut self, reg: u32, val: Value) {
        let idx = reg as usize;
        if idx >= self.regs.len() {
            self.regs.resize(idx + 1, Value::Null);
        }
        self.regs[idx] = val;
    }

    #[inline(always)]
    pub fn get(&self, reg: u32) -> Result<&Value> {
        let idx = reg as usize;
        if idx < self.regs.len() {
            Ok(&self.regs[idx])
        } else {
            anyhow::bail!("undefined register %{reg}")
        }
    }
}

// ── Debug: source line resolution ─────────────────────────────────────────────

fn resolve_line(table: &[crate::metadata::bytecode::LineEntry], block: u32, instr: u32) -> u32 {
    let mut line = 0u32;
    for entry in table {
        if entry.block > block || (entry.block == block && entry.instr > instr) { break; }
        line = entry.line;
    }
    line
}

// ── Core execution loop ──────────────────────────────────────────────────────

pub(crate) fn exec_function(module: &Module, func: &Function, args: &[Value]) -> Result<ExecOutcome> {
    let mut frame = Frame::new(args, func.max_reg);
    let block_map = &func.block_index;
    let mut block_idx = 0usize;

    'exec: loop {
        let block = func
            .blocks
            .get(block_idx)
            .with_context(|| format!("block index {block_idx} out of range"))?;

        for (instr_idx, instr) in block.instructions.iter().enumerate() {
            // exec_instr returns:
            //   Ok(None)       — normal instruction completion
            //   Ok(Some(val))  — a callee threw an exception (value-based propagation)
            //   Err(e)         — internal VM error
            match exec_instr::exec_instr(module, &mut frame, instr) {
                Ok(None) => {}
                Ok(Some(thrown_val)) => {
                    // User exception from a callee — try to find a local handler
                    if let Some(entry_idx) = find_handler(func, block_idx, block_map) {
                        let entry = &func.exception_table[entry_idx];
                        frame.set(entry.catch_reg, thrown_val);
                        block_idx = *block_map.get(entry.catch_label.as_str())
                            .with_context(|| format!("undefined block `{}`", entry.catch_label))?;
                        continue 'exec;
                    }
                    // No handler — propagate up as ExecOutcome::Thrown (no anyhow allocation)
                    return Ok(ExecOutcome::Thrown(thrown_val));
                }
                Err(e) => {
                    // JIT path: check if this is a UserException from JIT helpers
                    if e.is::<UserException>() {
                        let thrown_val = user_exception_take().unwrap_or(Value::Null);
                        if let Some(entry_idx) = find_handler(func, block_idx, block_map) {
                            let entry = &func.exception_table[entry_idx];
                            frame.set(entry.catch_reg, thrown_val);
                            block_idx = *block_map.get(entry.catch_label.as_str())
                                .with_context(|| format!("undefined block `{}`", entry.catch_label))?;
                            continue 'exec;
                        }
                        return Ok(ExecOutcome::Thrown(thrown_val));
                    }
                    // Internal error — enrich with source location
                    let loc = resolve_line(&func.line_table, block_idx as u32, instr_idx as u32);
                    return Err(e.context(format!("  at {} (line {})", func.name, loc)));
                }
            }
        }

        match &block.terminator {
            Terminator::Ret { reg: None }      => return Ok(ExecOutcome::Returned(None)),
            Terminator::Ret { reg: Some(r) }   => return Ok(ExecOutcome::Returned(Some(frame.get(*r)?.clone()))),
            Terminator::Br  { label }          => {
                block_idx = *block_map.get(label.as_str())
                    .with_context(|| format!("undefined block `{label}`"))?;
            }
            Terminator::BrCond { cond, true_label, false_label } => {
                let go_true = match frame.get(*cond)? {
                    Value::Bool(b) => *b,
                    other => bail!("BrCond expects bool, got {:?}", other),
                };
                let label = if go_true { true_label } else { false_label };
                block_idx = *block_map.get(label.as_str())
                    .with_context(|| format!("undefined block `{label}`"))?;
            }
            Terminator::Throw { reg } => {
                let val = frame.get(*reg)?.clone();
                if let Some(entry_idx) = find_handler(func, block_idx, block_map) {
                    let entry = &func.exception_table[entry_idx];
                    frame.set(entry.catch_reg, val);
                    block_idx = *block_map.get(entry.catch_label.as_str())
                        .with_context(|| format!("undefined block `{}`", entry.catch_label))?;
                } else {
                    // No local handler — propagate via value, not anyhow
                    return Ok(ExecOutcome::Thrown(val));
                }
            }
        }
    }
}

/// Find the index into `func.exception_table` that covers the given block index.
fn find_handler(func: &Function, block_idx: usize, block_map: &HashMap<String, usize>) -> Option<usize> {
    for (i, entry) in func.exception_table.iter().enumerate() {
        let start_idx = *block_map.get(&entry.try_start)?;
        let end_idx   = *block_map.get(&entry.try_end)?;
        if block_idx >= start_idx && block_idx < end_idx {
            return Some(i);
        }
    }
    None
}
