/// Interpreter backend — tree-walking bytecode execution.
///
/// Implementation is split across submodules:
/// • mod.rs       — public API, Frame, core execution loop
/// • builtins.rs  — builtin function dispatch (__println, __len, __str_*, …)
/// • helpers.rs   — value helpers (value_to_str, int_binop, numeric_lt, …)

mod builtins;
mod helpers;

pub use helpers::value_to_str;

use crate::bytecode::{Function, Instruction, Module, Terminator};
use crate::types::Value;
use anyhow::{bail, Context, Result};
use helpers::{bool_val, collect_args, int_binop, numeric_lt, str_val, to_usize, expect_array};
use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;

/// Entry point: run a function with the given arguments.
pub fn run(module: &Module, func: &Function, args: &[Value]) -> Result<()> {
    exec_function(module, func, args)?;
    Ok(())
}

struct Frame {
    regs: HashMap<u32, Value>,
    vars: HashMap<String, Value>,  // mutable named variable slots
}

impl Frame {
    fn new(args: &[Value]) -> Self {
        let mut regs = HashMap::new();
        for (i, v) in args.iter().enumerate() {
            regs.insert(i as u32, v.clone());
        }
        Frame { regs, vars: HashMap::new() }
    }

    fn set(&mut self, reg: u32, val: Value) {
        self.regs.insert(reg, val);
    }

    fn get(&self, reg: u32) -> Result<&Value> {
        self.regs
            .get(&reg)
            .with_context(|| format!("undefined register %{reg}"))
    }

    fn store_var(&mut self, name: &str, reg: u32) -> Result<()> {
        let val = self.get(reg)?.clone();
        self.vars.insert(name.to_string(), val);
        Ok(())
    }

    fn load_var(&mut self, dst: u32, name: &str) -> Result<()> {
        let val = self.vars
            .get(name)
            .with_context(|| format!("undefined variable `{name}`"))?
            .clone();
        self.regs.insert(dst, val);
        Ok(())
    }
}

fn exec_function(module: &Module, func: &Function, args: &[Value]) -> Result<Option<Value>> {
    let mut frame = Frame::new(args);
    let mut block_idx = 0usize;

    loop {
        let block = func
            .blocks
            .get(block_idx)
            .with_context(|| format!("block index {block_idx} out of range"))?;

        for instr in &block.instructions {
            exec_instr(module, &mut frame, instr)?;
        }

        match &block.terminator {
            Terminator::Ret { reg: None }      => return Ok(None),
            Terminator::Ret { reg: Some(r) }   => return Ok(Some(frame.get(*r)?.clone())),
            Terminator::Br  { label }          => block_idx = find_block(func, label)?,
            Terminator::BrCond { cond, true_label, false_label } => {
                let go_true = match frame.get(*cond)? {
                    Value::Bool(b) => *b,
                    other => bail!("BrCond expects bool, got {:?}", other),
                };
                let label = if go_true { true_label } else { false_label };
                block_idx = find_block(func, label)?;
            }
        }
    }
}

fn find_block(func: &Function, label: &str) -> Result<usize> {
    func.blocks
        .iter()
        .position(|b| b.label == label)
        .with_context(|| format!("undefined block `{label}`"))
}

fn exec_instr(module: &Module, frame: &mut Frame, instr: &Instruction) -> Result<()> {
    match instr {
        // ── Constants ────────────────────────────────────────────────────────
        Instruction::ConstStr { dst, idx } => {
            let s = module
                .string_pool
                .get(*idx as usize)
                .with_context(|| format!("string pool index {idx} out of range"))?;
            frame.set(*dst, Value::Str(s.clone()));
        }
        Instruction::ConstI32  { dst, val } => frame.set(*dst, Value::I32(*val)),
        Instruction::ConstI64  { dst, val } => frame.set(*dst, Value::I64(*val)),
        Instruction::ConstF64  { dst, val } => frame.set(*dst, Value::F64(*val)),
        Instruction::ConstBool { dst, val } => frame.set(*dst, Value::Bool(*val)),
        Instruction::ConstNull { dst }      => frame.set(*dst, Value::Null),
        Instruction::Copy      { dst, src } => frame.set(*dst, frame.get(*src)?.clone()),

        // ── Arithmetic ───────────────────────────────────────────────────────
        Instruction::Add { dst, a, b } => {
            // String concatenation: if either operand is a string, concat.
            let result = match (frame.get(*a)?, frame.get(*b)?) {
                (Value::Str(sa), Value::Str(sb)) => Value::Str(format!("{}{}", sa, sb)),
                (Value::Str(sa), vb)             => Value::Str(format!("{}{}", sa, value_to_str(vb))),
                (va, Value::Str(sb))             => Value::Str(format!("{}{}", value_to_str(va), sb)),
                _                                => int_binop(&frame.regs, *a, *b, |x, y| x + y, |x, y| x + y)?,
            };
            frame.set(*dst, result);
        }
        Instruction::Sub { dst, a, b } => {
            frame.set(*dst, int_binop(&frame.regs, *a, *b, |x, y| x - y, |x, y| x - y)?);
        }
        Instruction::Mul { dst, a, b } => {
            frame.set(*dst, int_binop(&frame.regs, *a, *b, |x, y| x * y, |x, y| x * y)?);
        }
        Instruction::Div { dst, a, b } => {
            frame.set(*dst, int_binop(&frame.regs, *a, *b, |x, y| x / y, |x, y| x / y)?);
        }
        Instruction::Rem { dst, a, b } => {
            frame.set(*dst, int_binop(&frame.regs, *a, *b, |x, y| x % y, |x, y| x % y)?);
        }

        // ── Comparison ───────────────────────────────────────────────────────
        Instruction::Eq { dst, a, b } => {
            let res = frame.get(*a)? == frame.get(*b)?;
            frame.set(*dst, Value::Bool(res));
        }
        Instruction::Ne { dst, a, b } => {
            let res = frame.get(*a)? != frame.get(*b)?;
            frame.set(*dst, Value::Bool(res));
        }
        Instruction::Lt { dst, a, b } => {
            let res = numeric_lt(&frame.regs, *a, *b)?;
            frame.set(*dst, Value::Bool(res));
        }
        Instruction::Le { dst, a, b } => {
            let res = numeric_lt(&frame.regs, *b, *a)?; // LE = NOT (b < a)
            frame.set(*dst, Value::Bool(!res));
        }
        Instruction::Gt { dst, a, b } => {
            let res = numeric_lt(&frame.regs, *b, *a)?; // GT = b < a
            frame.set(*dst, Value::Bool(res));
        }
        Instruction::Ge { dst, a, b } => {
            let res = numeric_lt(&frame.regs, *a, *b)?; // GE = NOT (a < b)
            frame.set(*dst, Value::Bool(!res));
        }

        // ── Logical ──────────────────────────────────────────────────────────
        Instruction::And { dst, a, b } => {
            let va = bool_val(&frame.regs, *a)?;
            let vb = bool_val(&frame.regs, *b)?;
            frame.set(*dst, Value::Bool(va && vb));
        }
        Instruction::Or { dst, a, b } => {
            let va = bool_val(&frame.regs, *a)?;
            let vb = bool_val(&frame.regs, *b)?;
            frame.set(*dst, Value::Bool(va || vb));
        }
        Instruction::Not { dst, src } => {
            let v = bool_val(&frame.regs, *src)?;
            frame.set(*dst, Value::Bool(!v));
        }

        // ── Unary arithmetic ─────────────────────────────────────────────────
        Instruction::Neg { dst, src } => {
            let res = match frame.get(*src)? {
                Value::I32(n) => Value::I32(-n),
                Value::I64(n) => Value::I64(-n),
                Value::F64(f) => Value::F64(-f),
                other => bail!("Neg: expected numeric, got {:?}", other),
            };
            frame.set(*dst, res);
        }

        // ── Variable slots ───────────────────────────────────────────────────
        Instruction::Store { var, src } => {
            frame.store_var(var, *src)?;
        }
        Instruction::Load { dst, var } => {
            frame.load_var(*dst, var)?;
        }

        // ── String ───────────────────────────────────────────────────────────
        Instruction::StrConcat { dst, a, b } => {
            let sa = str_val(&frame.regs, *a)?;
            let sb = str_val(&frame.regs, *b)?;
            frame.set(*dst, Value::Str(format!("{}{}", sa, sb)));
        }
        Instruction::ToStr { dst, src } => {
            let s = value_to_str(frame.get(*src)?);
            frame.set(*dst, Value::Str(s));
        }

        // ── Calls ────────────────────────────────────────────────────────────
        Instruction::Call { dst, func: fname, args } => {
            let arg_vals = collect_args(&frame.regs, args)?;
            let callee = module
                .functions
                .iter()
                .find(|f| f.name == *fname)
                .with_context(|| format!("undefined function `{fname}`"))?;
            let ret = exec_function(module, callee, &arg_vals)?;
            frame.set(*dst, ret.unwrap_or(Value::Null));
        }

        Instruction::Builtin { dst, name, args } => {
            let arg_vals = collect_args(&frame.regs, args)?;
            let result = builtins::exec_builtin(name, &arg_vals)?;
            frame.set(*dst, result);
        }

        // ── Arrays ───────────────────────────────────────────────────────────
        Instruction::ArrayNew { dst, size } => {
            let n = to_usize(frame.get(*size)?, "ArrayNew size")?;
            let arr = Rc::new(RefCell::new(vec![Value::Null; n]));
            frame.set(*dst, Value::Array(arr));
        }

        Instruction::ArrayNewLit { dst, elems } => {
            let vals: Vec<Value> = elems
                .iter()
                .map(|r| frame.get(*r).map(|v| v.clone()))
                .collect::<Result<_>>()?;
            frame.set(*dst, Value::Array(Rc::new(RefCell::new(vals))));
        }

        Instruction::ArrayGet { dst, arr, idx } => {
            let i = to_usize(frame.get(*idx)?, "ArrayGet index")?;
            let rc = expect_array(frame.get(*arr)?, "ArrayGet")?;
            let borrowed = rc.borrow();
            if i >= borrowed.len() {
                bail!("array index {} out of bounds (len={})", i, borrowed.len());
            }
            frame.set(*dst, borrowed[i].clone());
        }

        Instruction::ArraySet { arr, idx, val } => {
            let i   = to_usize(frame.get(*idx)?, "ArraySet index")?;
            let v   = frame.get(*val)?.clone();
            let rc  = expect_array(frame.get(*arr)?, "ArraySet")?;
            let mut borrowed = rc.borrow_mut();
            if i >= borrowed.len() {
                bail!("array index {} out of bounds (len={})", i, borrowed.len());
            }
            borrowed[i] = v;
        }

        Instruction::ArrayLen { dst, arr } => {
            let len = match frame.get(*arr)? {
                Value::Array(rc) => rc.borrow().len() as i32,
                other => bail!("ArrayLen: expected array, got {:?}", other),
            };
            frame.set(*dst, Value::I32(len));
        }
    }
    Ok(())
}
