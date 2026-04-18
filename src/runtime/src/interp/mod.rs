/// Interpreter backend — tree-walking bytecode execution.
///
/// Implementation is split across submodules:
/// • mod.rs      — public API, Frame, core execution loop
/// • exec_instr.rs — instruction dispatch (one big match)
/// • dispatch.rs — object dispatch helpers (vtable, ToString, static fields)
/// • ops.rs      — register-level helpers (int_binop, numeric_lt, collect_args, …)

mod dispatch;
mod exec_instr;
mod ops;

pub use crate::corelib::convert::value_to_str;
use crate::metadata::{Function, Module, Terminator, Value};
use anyhow::{bail, Context, Result};
use std::cell::RefCell;
use std::collections::HashMap;

// ── User exception machinery ──────────────────────────────────────────────────

// Thread-local slot: holds the currently-in-flight user exception value.
// Populated by `user_throw`, consumed by exception table handler lookup.
thread_local! {
    static PENDING_EXCEPTION: RefCell<Option<Value>> = const { RefCell::new(None) };
}

/// Lightweight sentinel: `Send + Sync + 'static` because it carries no payload.
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

fn user_throw(val: Value) -> anyhow::Error {
    PENDING_EXCEPTION.with(|p| *p.borrow_mut() = Some(val));
    anyhow::Error::new(UserException)
}

fn user_exception_take() -> Option<Value> {
    PENDING_EXCEPTION.with(|p| p.borrow_mut().take())
}

// ── Public entry points ──────────────────────────────────────────────────────

/// Entry point: run a function with the given arguments.
pub fn run(module: &Module, func: &Function, args: &[Value]) -> Result<()> {
    exec_function(module, func, args)?;
    Ok(())
}

/// Run with static init: clears static fields, runs __static_init__ if present, then runs `func`.
pub fn run_with_static_init(module: &Module, func: &Function) -> Result<()> {
    dispatch::static_fields_clear();
    // Call __static_init__ if it exists
    let init_name = format!("{}.__static_init__", module.name);
    if let Some(init_fn) = module.functions.iter().find(|f| f.name == init_name) {
        exec_function(module, init_fn, &[])?;
    }
    exec_function(module, func, &[])?;
    Ok(())
}

// ── Frame ────────────────────────────────────────────────────────────────────

pub(crate) struct Frame {
    pub regs: Vec<Value>,
}

impl Frame {
    pub fn new(args: &[Value], max_reg: u32) -> Self {
        // Pre-allocate based on max_reg hint; fall back to args length if unknown (max_reg == 0).
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

    pub fn get(&self, reg: u32) -> Result<&Value> {
        self.regs
            .get(reg as usize)
            .with_context(|| format!("undefined register %{reg}"))
    }
}

// ── Debug: source line resolution ─────────────────────────────────────────────

/// Look up the source line number for a given (block, instruction) position
/// from the function's line_table. Returns the last entry whose position is ≤ current.
fn resolve_line(table: &[crate::metadata::bytecode::LineEntry], block: u32, instr: u32) -> u32 {
    let mut line = 0u32;
    for entry in table {
        if entry.block > block || (entry.block == block && entry.instr > instr) { break; }
        line = entry.line;
    }
    line
}

// ── Core execution loop ──────────────────────────────────────────────────────

pub(crate) fn exec_function(module: &Module, func: &Function, args: &[Value]) -> Result<Option<Value>> {
    let mut frame = Frame::new(args, func.max_reg);
    // O(1) block lookup: build label → index map once per function call.
    let block_map: HashMap<&str, usize> = func.blocks.iter().enumerate()
        .map(|(i, b)| (b.label.as_str(), i))
        .collect();
    let mut block_idx = 0usize;

    'exec: loop {
        let block = func
            .blocks
            .get(block_idx)
            .with_context(|| format!("block index {block_idx} out of range"))?;

        for (instr_idx, instr) in block.instructions.iter().enumerate() {
            if let Err(e) = exec_instr::exec_instr(module, &mut frame, instr) {
                if e.is::<UserException>() {
                    if let Some(entry_idx) = find_handler(func, block_idx, &block_map) {
                        let thrown_val = user_exception_take().unwrap_or(Value::Null);
                        let entry = &func.exception_table[entry_idx];
                        frame.set(entry.catch_reg, thrown_val);
                        block_idx = *block_map.get(entry.catch_label.as_str())
                            .with_context(|| format!("undefined block `{}`", entry.catch_label))?;
                        continue 'exec;
                    }
                }
                // Enrich error with source location from line table
                let loc = resolve_line(&func.line_table, block_idx as u32, instr_idx as u32);
                return Err(e.context(format!("  at {} (line {})", func.name, loc)));
            }
        }

        match &block.terminator {
            Terminator::Ret { reg: None }      => return Ok(None),
            Terminator::Ret { reg: Some(r) }   => return Ok(Some(frame.get(*r)?.clone())),
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
                if let Some(entry_idx) = find_handler(func, block_idx, &block_map) {
                    let entry = &func.exception_table[entry_idx];
                    frame.set(entry.catch_reg, val);
                    block_idx = *block_map.get(entry.catch_label.as_str())
                        .with_context(|| format!("undefined block `{}`", entry.catch_label))?;
                } else {
                    return Err(user_throw(val));
                }
            }
        }
    }
}

/// Find the index into `func.exception_table` that covers the given block index.
fn find_handler(func: &Function, block_idx: usize, block_map: &HashMap<&str, usize>) -> Option<usize> {
    for (i, entry) in func.exception_table.iter().enumerate() {
        let start_idx = *block_map.get(entry.try_start.as_str())?;
        let end_idx   = *block_map.get(entry.try_end.as_str())?;
        if block_idx >= start_idx && block_idx < end_idx {
            return Some(i);
        }
    }
    None
}
