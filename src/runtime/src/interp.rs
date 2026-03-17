use crate::bytecode::{Function, Instruction, Module, Terminator};
use crate::types::Value;
use anyhow::{bail, Context, Result};
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
                _                                => int_binop(frame, *a, *b, |x, y| x + y, |x, y| x + y)?,
            };
            frame.set(*dst, result);
        }
        Instruction::Sub { dst, a, b } => {
            frame.set(*dst, int_binop(frame, *a, *b, |x, y| x - y, |x, y| x - y)?);
        }
        Instruction::Mul { dst, a, b } => {
            frame.set(*dst, int_binop(frame, *a, *b, |x, y| x * y, |x, y| x * y)?);
        }
        Instruction::Div { dst, a, b } => {
            frame.set(*dst, int_binop(frame, *a, *b, |x, y| x / y, |x, y| x / y)?);
        }
        Instruction::Rem { dst, a, b } => {
            frame.set(*dst, int_binop(frame, *a, *b, |x, y| x % y, |x, y| x % y)?);
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
            let res = numeric_lt(frame, *a, *b)?;
            frame.set(*dst, Value::Bool(res));
        }
        Instruction::Le { dst, a, b } => {
            let res = numeric_lt(frame, *b, *a)?; // LE = NOT (b < a)
            frame.set(*dst, Value::Bool(!res));
        }
        Instruction::Gt { dst, a, b } => {
            let res = numeric_lt(frame, *b, *a)?; // GT = b < a
            frame.set(*dst, Value::Bool(res));
        }
        Instruction::Ge { dst, a, b } => {
            let res = numeric_lt(frame, *a, *b)?; // GE = NOT (a < b)
            frame.set(*dst, Value::Bool(!res));
        }

        // ── Logical ──────────────────────────────────────────────────────────
        Instruction::And { dst, a, b } => {
            let va = bool_val(frame, *a)?;
            let vb = bool_val(frame, *b)?;
            frame.set(*dst, Value::Bool(va && vb));
        }
        Instruction::Or { dst, a, b } => {
            let va = bool_val(frame, *a)?;
            let vb = bool_val(frame, *b)?;
            frame.set(*dst, Value::Bool(va || vb));
        }
        Instruction::Not { dst, src } => {
            let v = bool_val(frame, *src)?;
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
            let sa = str_val(frame, *a)?;
            let sb = str_val(frame, *b)?;
            frame.set(*dst, Value::Str(format!("{}{}", sa, sb)));
        }
        Instruction::ToStr { dst, src } => {
            let s = value_to_str(frame.get(*src)?);
            frame.set(*dst, Value::Str(s));
        }

        // ── Calls ────────────────────────────────────────────────────────────
        Instruction::Call { dst, func: fname, args } => {
            let arg_vals = collect_args(frame, args)?;
            let callee = module
                .functions
                .iter()
                .find(|f| f.name == *fname)
                .with_context(|| format!("undefined function `{fname}`"))?;
            let ret = exec_function(module, callee, &arg_vals)?;
            frame.set(*dst, ret.unwrap_or(Value::Null));
        }

        Instruction::Builtin { dst, name, args } => {
            let arg_vals = collect_args(frame, args)?;
            let result = exec_builtin(name, &arg_vals)?;
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

fn exec_builtin(name: &str, args: &[Value]) -> Result<Value> {
    match name {
        // ── I/O ──────────────────────────────────────────────────────────────
        "__println" => {
            let text = args.first().map(value_to_str).unwrap_or_default();
            println!("{}", text);
            Ok(Value::Null)
        }
        "__print" => {
            let text = args.first().map(value_to_str).unwrap_or_default();
            print!("{}", text);
            Ok(Value::Null)
        }
        "__concat" => {
            let a = args.first().map(value_to_str).unwrap_or_default();
            let b = args.get(1).map(value_to_str).unwrap_or_default();
            Ok(Value::Str(format!("{}{}", a, b)))
        }

        // ── Length (works on both arrays and strings) ─────────────────────
        "__len" => match args.first() {
            Some(Value::Array(rc)) => Ok(Value::I32(rc.borrow().len() as i32)),
            Some(Value::Str(s))    => Ok(Value::I32(s.len() as i32)),  // UTF-8 byte count
            Some(other)            => bail!("__len: expected array or string, got {:?}", other),
            None                   => bail!("__len: missing argument"),
        },

        // ── String built-ins ─────────────────────────────────────────────────
        "__str_substring" => {
            let s     = require_str(args, 0, "__str_substring")?;
            let start = require_usize(args, 1, "__str_substring")?;
            if args.len() == 2 {
                if start > s.len() {
                    bail!("__str_substring: start {} out of range (len={})", start, s.len());
                }
                Ok(Value::Str(s[start..].to_string()))
            } else {
                let len = require_usize(args, 2, "__str_substring")?;
                let end = start + len;
                if end > s.len() {
                    bail!("__str_substring: range {}..{} out of range (len={})", start, end, s.len());
                }
                Ok(Value::Str(s[start..end].to_string()))
            }
        }

        "__str_contains" => {
            let s   = require_str(args, 0, "__str_contains")?;
            let sub = require_str(args, 1, "__str_contains")?;
            Ok(Value::Bool(s.contains(sub.as_str())))
        }

        "__str_starts_with" => {
            let s      = require_str(args, 0, "__str_starts_with")?;
            let prefix = require_str(args, 1, "__str_starts_with")?;
            Ok(Value::Bool(s.starts_with(prefix.as_str())))
        }

        "__str_ends_with" => {
            let s      = require_str(args, 0, "__str_ends_with")?;
            let suffix = require_str(args, 1, "__str_ends_with")?;
            Ok(Value::Bool(s.ends_with(suffix.as_str())))
        }

        other => bail!("unknown builtin `{other}`"),
    }
}

// ── Helpers ──────────────────────────────────────────────────────────────────

fn collect_args(frame: &Frame, regs: &[u32]) -> Result<Vec<Value>> {
    regs.iter().map(|r| frame.get(*r).map(|v| v.clone())).collect()
}

fn str_val(frame: &Frame, reg: u32) -> Result<String> {
    match frame.get(reg)? {
        Value::Str(s) => Ok(s.clone()),
        other => bail!("expected str in register %{reg}, got {:?}", other),
    }
}

pub fn value_to_str(v: &Value) -> String {
    match v {
        Value::I32(n)  => n.to_string(),
        Value::I64(n)  => n.to_string(),
        Value::F64(f)  => f.to_string(),
        Value::Bool(b) => b.to_string(),
        Value::Str(s)  => s.clone(),
        Value::Null    => "null".to_string(),
        Value::Array(rc) => {
            let inner: Vec<String> = rc.borrow().iter().map(value_to_str).collect();
            format!("[{}]", inner.join(", "))
        }
        other => format!("{:?}", other),
    }
}

fn int_binop(
    frame: &Frame,
    a: u32,
    b: u32,
    int_op:   impl Fn(i64, i64) -> i64,
    float_op: impl Fn(f64, f64) -> f64,
) -> Result<Value> {
    Ok(match (frame.get(a)?, frame.get(b)?) {
        (Value::I32(x), Value::I32(y)) => Value::I32(int_op(*x as i64, *y as i64) as i32),
        (Value::I64(x), Value::I64(y)) => Value::I64(int_op(*x, *y)),
        // Widen I32 to I64 for mixed operations
        (Value::I32(x), Value::I64(y)) => Value::I64(int_op(*x as i64, *y)),
        (Value::I64(x), Value::I32(y)) => Value::I64(int_op(*x, *y as i64)),
        (Value::F64(x), Value::F64(y)) => Value::F64(float_op(*x, *y)),
        // Widen integer to f64 for mixed float operations
        (Value::F64(x), Value::I64(y)) => Value::F64(float_op(*x, *y as f64)),
        (Value::I64(x), Value::F64(y)) => Value::F64(float_op(*x as f64, *y)),
        (Value::F64(x), Value::I32(y)) => Value::F64(float_op(*x, *y as f64)),
        (Value::I32(x), Value::F64(y)) => Value::F64(float_op(*x as f64, *y)),
        (a, b) => bail!("type mismatch in arithmetic: {:?} vs {:?}", a, b),
    })
}

fn bool_val(frame: &Frame, reg: u32) -> Result<bool> {
    match frame.get(reg)? {
        Value::Bool(b) => Ok(*b),
        other => bail!("expected bool in register %{reg}, got {:?}", other),
    }
}

fn numeric_lt(frame: &Frame, a: u32, b: u32) -> Result<bool> {
    Ok(match (frame.get(a)?, frame.get(b)?) {
        (Value::I32(x), Value::I32(y)) => x < y,
        (Value::I64(x), Value::I64(y)) => x < y,
        (Value::I32(x), Value::I64(y)) => (*x as i64) < *y,
        (Value::I64(x), Value::I32(y)) => *x < (*y as i64),
        (Value::F64(x), Value::F64(y)) => x < y,
        (Value::F64(x), Value::I64(y)) => *x < (*y as f64),
        (Value::I64(x), Value::F64(y)) => (*x as f64) < *y,
        (Value::F64(x), Value::I32(y)) => *x < (*y as f64),
        (Value::I32(x), Value::F64(y)) => (*x as f64) < *y,
        (a, b) => bail!("type mismatch in comparison: {:?} vs {:?}", a, b),
    })
}

/// Convert a Value to a usize index/size, rejecting negative values.
fn to_usize(v: &Value, ctx: &str) -> Result<usize> {
    match v {
        Value::I32(n) if *n >= 0 => Ok(*n as usize),
        Value::I64(n) if *n >= 0 => Ok(*n as usize),
        other => bail!("{}: expected non-negative integer, got {:?}", ctx, other),
    }
}

/// Unwrap an Array value, returning its Rc.
fn expect_array(v: &Value, ctx: &str) -> Result<Rc<RefCell<Vec<Value>>>> {
    match v {
        Value::Array(rc) => Ok(rc.clone()),
        other => bail!("{}: expected array, got {:?}", ctx, other),
    }
}

/// Extract a String argument from the args slice.
fn require_str(args: &[Value], idx: usize, ctx: &str) -> Result<String> {
    match args.get(idx) {
        Some(Value::Str(s)) => Ok(s.clone()),
        Some(other) => bail!("{}: arg {} expected string, got {:?}", ctx, idx, other),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}

/// Extract a usize argument from the args slice.
fn require_usize(args: &[Value], idx: usize, ctx: &str) -> Result<usize> {
    match args.get(idx) {
        Some(v) => to_usize(v, ctx),
        None => bail!("{}: missing arg {}", ctx, idx),
    }
}
