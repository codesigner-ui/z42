use crate::bytecode::{Function, Instruction, Module, Terminator};
use crate::types::Value;
use anyhow::{bail, Result};
use std::collections::HashMap;

/// Bytecode interpreter — executes IR instructions directly.
pub fn run(module: &Module, func: &Function) -> Result<()> {
    let mut frame = Frame::new();
    exec_function(module, func, &mut frame)?;
    Ok(())
}

struct Frame {
    regs: HashMap<u32, Value>,
}

impl Frame {
    fn new() -> Self {
        Frame { regs: HashMap::new() }
    }

    fn set(&mut self, reg: u32, val: Value) {
        self.regs.insert(reg, val);
    }

    fn get(&self, reg: u32) -> Result<&Value> {
        self.regs.get(&reg).ok_or_else(|| anyhow::anyhow!("undefined register %{reg}"))
    }
}

fn exec_function(module: &Module, func: &Function, frame: &mut Frame) -> Result<Option<Value>> {
    let mut block_idx = 0usize;

    loop {
        let block = &func.blocks[block_idx];

        for instr in &block.instructions {
            exec_instr(module, frame, instr)?;
        }

        match &block.terminator {
            Terminator::Ret(None) => return Ok(None),
            Terminator::Ret(Some(reg)) => return Ok(Some(frame.get(*reg)?.clone())),
            Terminator::Br(label) => {
                block_idx = find_block(func, label)?;
            }
            Terminator::BrCond(reg, t, f) => {
                let cond = match frame.get(*reg)? {
                    Value::Bool(b) => *b,
                    other => bail!("BrCond expects bool, got {:?}", other),
                };
                let label = if cond { t } else { f };
                block_idx = find_block(func, label)?;
            }
        }
    }
}

fn find_block(func: &Function, label: &str) -> Result<usize> {
    func.blocks
        .iter()
        .position(|b| b.label == label)
        .ok_or_else(|| anyhow::anyhow!("undefined block {label}"))
}

fn exec_instr(module: &Module, frame: &mut Frame, instr: &Instruction) -> Result<()> {
    match instr {
        Instruction::ConstI32(dst, v) => frame.set(*dst, Value::I32(*v)),
        Instruction::ConstI64(dst, v) => frame.set(*dst, Value::I64(*v)),
        Instruction::ConstF64(dst, v) => frame.set(*dst, Value::F64(*v)),
        Instruction::ConstBool(dst, v) => frame.set(*dst, Value::Bool(*v)),
        Instruction::ConstStr(dst, idx) => {
            let s = module
                .string_pool
                .get(*idx as usize)
                .ok_or_else(|| anyhow::anyhow!("string index {idx} out of range"))?;
            frame.set(*dst, Value::Str(s.clone()));
        }

        Instruction::Add(dst, a, b) => {
            let v = numeric_binop(frame, *a, *b, |x, y| x + y, |x, y| x + y)?;
            frame.set(*dst, v);
        }
        Instruction::Sub(dst, a, b) => {
            let v = numeric_binop(frame, *a, *b, |x, y| x - y, |x, y| x - y)?;
            frame.set(*dst, v);
        }
        Instruction::Mul(dst, a, b) => {
            let v = numeric_binop(frame, *a, *b, |x, y| x * y, |x, y| x * y)?;
            frame.set(*dst, v);
        }
        Instruction::Div(dst, a, b) => {
            let v = numeric_binop(frame, *a, *b, |x, y| x / y, |x, y| x / y)?;
            frame.set(*dst, v);
        }

        Instruction::Eq(dst, a, b) => {
            let result = frame.get(*a)? == frame.get(*b)?;
            frame.set(*dst, Value::Bool(result));
        }
        Instruction::Lt(dst, a, b) => {
            let result = numeric_cmp(frame, *a, *b)?;
            frame.set(*dst, Value::Bool(result));
        }

        Instruction::Call(dst, name, args) => {
            // Builtin: io.println
            if name == "io.println" {
                if let Some(reg) = args.first() {
                    println!("{:?}", frame.get(*reg)?);
                }
                frame.set(*dst, Value::Null);
                return Ok(());
            }
            // TODO: dispatch to other functions in module
            bail!("call to unknown function `{name}` not yet implemented");
        }
    }
    Ok(())
}

fn numeric_binop(
    frame: &Frame,
    a: u32,
    b: u32,
    int_op: impl Fn(i64, i64) -> i64,
    float_op: impl Fn(f64, f64) -> f64,
) -> Result<Value> {
    Ok(match (frame.get(a)?, frame.get(b)?) {
        (Value::I32(x), Value::I32(y)) => Value::I32(int_op(*x as i64, *y as i64) as i32),
        (Value::I64(x), Value::I64(y)) => Value::I64(int_op(*x, *y)),
        (Value::F64(x), Value::F64(y)) => Value::F64(float_op(*x, *y)),
        (a, b) => bail!("type mismatch in arithmetic: {:?} vs {:?}", a, b),
    })
}

fn numeric_cmp(frame: &Frame, a: u32, b: u32) -> Result<bool> {
    Ok(match (frame.get(a)?, frame.get(b)?) {
        (Value::I32(x), Value::I32(y)) => x < y,
        (Value::I64(x), Value::I64(y)) => x < y,
        (Value::F64(x), Value::F64(y)) => x < y,
        (a, b) => bail!("type mismatch in comparison: {:?} vs {:?}", a, b),
    })
}
