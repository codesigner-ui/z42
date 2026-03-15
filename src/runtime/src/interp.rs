use crate::bytecode::{Function, Instruction, Module, Terminator};
use crate::types::Value;
use anyhow::{bail, Context, Result};
use std::collections::HashMap;

/// Entry point: run a function with the given arguments.
pub fn run(module: &Module, func: &Function, args: &[Value]) -> Result<()> {
    exec_function(module, func, args)?;
    Ok(())
}

struct Frame {
    regs: HashMap<u32, Value>,
}

impl Frame {
    fn new(args: &[Value]) -> Self {
        let mut regs = HashMap::new();
        for (i, v) in args.iter().enumerate() {
            regs.insert(i as u32, v.clone());
        }
        Frame { regs }
    }

    fn set(&mut self, reg: u32, val: Value) {
        self.regs.insert(reg, val);
    }

    fn get(&self, reg: u32) -> Result<&Value> {
        self.regs
            .get(&reg)
            .with_context(|| format!("undefined register %{reg}"))
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

        // ── Arithmetic ───────────────────────────────────────────────────────
        Instruction::Add { dst, a, b } => {
            frame.set(*dst, int_binop(frame, *a, *b, |x, y| x + y, |x, y| x + y)?);
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
    }
    Ok(())
}

fn exec_builtin(name: &str, args: &[Value]) -> Result<Value> {
    match name {
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
            // kept for compatibility
            let a = args.first().map(value_to_str).unwrap_or_default();
            let b = args.get(1).map(value_to_str).unwrap_or_default();
            Ok(Value::Str(format!("{}{}", a, b)))
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

fn value_to_str(v: &Value) -> String {
    match v {
        Value::I32(n)  => n.to_string(),
        Value::I64(n)  => n.to_string(),
        Value::F64(f)  => f.to_string(),
        Value::Bool(b) => b.to_string(),
        Value::Str(s)  => s.clone(),
        Value::Null    => "null".to_string(),
        other          => format!("{:?}", other),
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
        (Value::F64(x), Value::F64(y)) => Value::F64(float_op(*x, *y)),
        (a, b) => bail!("type mismatch in arithmetic: {:?} vs {:?}", a, b),
    })
}

fn numeric_lt(frame: &Frame, a: u32, b: u32) -> Result<bool> {
    Ok(match (frame.get(a)?, frame.get(b)?) {
        (Value::I32(x), Value::I32(y)) => x < y,
        (Value::I64(x), Value::I64(y)) => x < y,
        (Value::F64(x), Value::F64(y)) => x < y,
        (a, b) => bail!("type mismatch in comparison: {:?} vs {:?}", a, b),
    })
}
