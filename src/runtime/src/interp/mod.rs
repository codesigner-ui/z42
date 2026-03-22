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
use crate::types::{ObjectData, Value};
use anyhow::{bail, Context, Result};
use helpers::{bool_val, collect_args, int_binop, int_bitop, numeric_lt, str_val, to_usize};
use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;

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
            p.borrow().as_ref().map(helpers::value_to_str).unwrap_or_default()
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

    'exec: loop {
        let block = func
            .blocks
            .get(block_idx)
            .with_context(|| format!("block index {block_idx} out of range"))?;

        for instr in &block.instructions {
            if let Err(e) = exec_instr(module, &mut frame, instr) {
                if e.is::<UserException>() {
                    if let Some(entry_idx) = find_handler(func, block_idx) {
                        let thrown_val = user_exception_take().unwrap_or(Value::Null);
                        let entry = &func.exception_table[entry_idx];
                        frame.set(entry.catch_reg, thrown_val);
                        block_idx = find_block(func, &entry.catch_label)?;
                        continue 'exec;
                    }
                }
                return Err(e);
            }
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
            Terminator::Throw { reg } => {
                let val = frame.get(*reg)?.clone();
                if let Some(entry_idx) = find_handler(func, block_idx) {
                    let entry = &func.exception_table[entry_idx];
                    frame.set(entry.catch_reg, val);
                    block_idx = find_block(func, &entry.catch_label)?;
                } else {
                    return Err(user_throw(val));
                }
            }
        }
    }
}

/// Find the index into `func.exception_table` that covers the given block index.
fn find_handler(func: &Function, block_idx: usize) -> Option<usize> {
    for (i, entry) in func.exception_table.iter().enumerate() {
        let start_idx = func.blocks.iter().position(|b| b.label == entry.try_start)?;
        let end_idx   = func.blocks.iter().position(|b| b.label == entry.try_end)?;
        if block_idx >= start_idx && block_idx < end_idx {
            return Some(i);
        }
    }
    None
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

        // ── Bitwise ──────────────────────────────────────────────────────────
        Instruction::BitAnd { dst, a, b } => {
            frame.set(*dst, int_bitop(&frame.regs, *a, *b, |x, y| x & y)?);
        }
        Instruction::BitOr { dst, a, b } => {
            frame.set(*dst, int_bitop(&frame.regs, *a, *b, |x, y| x | y)?);
        }
        Instruction::BitXor { dst, a, b } => {
            frame.set(*dst, int_bitop(&frame.regs, *a, *b, |x, y| x ^ y)?);
        }
        Instruction::BitNot { dst, src } => {
            let res = match frame.get(*src)? {
                Value::I32(n) => Value::I32(!n),
                Value::I64(n) => Value::I64(!n),
                other => bail!("BitNot: expected integral, got {:?}", other),
            };
            frame.set(*dst, res);
        }
        Instruction::Shl { dst, a, b } => {
            frame.set(*dst, int_bitop(&frame.regs, *a, *b, |x, y| x << (y & 63))?);
        }
        Instruction::Shr { dst, a, b } => {
            frame.set(*dst, int_bitop(&frame.regs, *a, *b, |x, y| x >> (y & 63))?);
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
            // Clone the Rc so the immutable borrow of `frame` is released before `frame.set`.
            let result = match frame.get(*arr)? {
                Value::Array(rc) => {
                    let rc = rc.clone();
                    let i = to_usize(frame.get(*idx)?, "ArrayGet index")?;
                    let borrowed = rc.borrow();
                    if i >= borrowed.len() {
                        bail!("array index {} out of bounds (len={})", i, borrowed.len());
                    }
                    borrowed[i].clone()
                }
                Value::Map(rc) => {
                    let rc = rc.clone();
                    let key = value_to_str(frame.get(*idx)?);
                    let borrowed = rc.borrow();
                    borrowed.get(&key).cloned().unwrap_or(Value::Null)
                }
                other => bail!("ArrayGet: expected array or map, got {:?}", other),
            };
            frame.set(*dst, result);
        }

        Instruction::ArraySet { arr, idx, val } => {
            let v = frame.get(*val)?.clone();
            // Clone the Rc so the immutable borrow of `frame` is released before interior mutation.
            match frame.get(*arr)? {
                Value::Array(rc) => {
                    let rc = rc.clone();
                    let i = to_usize(frame.get(*idx)?, "ArraySet index")?;
                    let mut borrowed = rc.borrow_mut();
                    if i >= borrowed.len() {
                        bail!("array index {} out of bounds (len={})", i, borrowed.len());
                    }
                    borrowed[i] = v;
                }
                Value::Map(rc) => {
                    let rc = rc.clone();
                    let key = value_to_str(frame.get(*idx)?);
                    rc.borrow_mut().insert(key, v);
                }
                other => bail!("ArraySet: expected array or map, got {:?}", other),
            }
        }

        Instruction::ArrayLen { dst, arr } => {
            let len = match frame.get(*arr)? {
                Value::Array(rc) => rc.borrow().len() as i32,
                other => bail!("ArrayLen: expected array, got {:?}", other),
            };
            frame.set(*dst, Value::I32(len));
        }

        // ── Objects ──────────────────────────────────────────────────────────
        Instruction::ObjNew { dst, class_name, args } => {
            // Find the class descriptor to get field names.
            let field_names: Vec<String> = module
                .classes
                .iter()
                .find(|c| &c.name == class_name)
                .map(|c| c.fields.iter().map(|f| f.name.clone()).collect())
                .unwrap_or_default();

            // Allocate the object with all fields null-initialised.
            let fields: HashMap<String, Value> =
                field_names.into_iter().map(|n| (n, Value::Null)).collect();
            let obj_rc = Rc::new(RefCell::new(ObjectData {
                class_name: class_name.clone(),
                fields,
            }));
            let obj_val = Value::Object(obj_rc);

            // If a constructor function exists, call it with [this, ...args].
            // Convention: ctor name = "{qualified_class_name}.{simple_class_name}"
            // e.g., "Point" → "Point.Point"; "Demo.Point" → "Demo.Point.Point"
            let simple_name = class_name.split('.').last().unwrap_or(class_name.as_str());
            let ctor_name = format!("{}.{}", class_name, simple_name);
            if let Some(ctor) = module.functions.iter().find(|f| f.name == ctor_name) {
                let mut ctor_args = vec![obj_val.clone()];
                ctor_args.extend(collect_args(&frame.regs, args)?);
                exec_function(module, ctor, &ctor_args)?;
            }

            frame.set(*dst, obj_val);
        }

        Instruction::FieldGet { dst, obj, field_name } => {
            let val = match frame.get(*obj)? {
                Value::Object(rc) => rc
                    .borrow()
                    .fields
                    .get(field_name)
                    .cloned()
                    .unwrap_or(Value::Null),
                other => bail!("FieldGet: expected object, got {:?}", other),
            };
            frame.set(*dst, val);
        }

        Instruction::FieldSet { obj, field_name, val } => {
            let v = frame.get(*val)?.clone();
            match frame.get(*obj)? {
                Value::Object(rc) => {
                    rc.borrow_mut().fields.insert(field_name.clone(), v);
                }
                other => bail!("FieldSet: expected object, got {:?}", other),
            }
        }
    }
    Ok(())
}
