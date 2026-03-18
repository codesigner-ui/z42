use std::cell::RefCell;
use std::collections::HashMap;
use std::rc::Rc;

/// Heap-allocated object with named fields and reference semantics.
#[derive(Debug)]
pub struct ObjectData {
    pub class_name: String,
    pub fields: HashMap<String, Value>,
}

/// Primitive and heap value types that the VM operates on at runtime.
///
/// `Array` uses `Rc<RefCell<Vec<Value>>>` to give reference semantics with
/// interior mutability — assigning an array copies the reference, not the data.
/// `Object` uses `Rc<RefCell<ObjectData>>` for the same reason.
#[derive(Debug, Clone)]
pub enum Value {
    I8(i8),
    I16(i16),
    I32(i32),
    I64(i64),
    U8(u8),
    U16(u16),
    U32(u32),
    U64(u64),
    F32(f32),
    F64(f64),
    Bool(bool),
    Char(char),
    Str(String),
    Null,
    /// Heap-allocated dynamic array with reference semantics.
    Array(Rc<RefCell<Vec<Value>>>),
    /// Heap-allocated user-defined class instance with reference semantics.
    Object(Rc<RefCell<ObjectData>>),
}

impl PartialEq for Value {
    fn eq(&self, other: &Self) -> bool {
        match (self, other) {
            (Value::I8(a),   Value::I8(b))   => a == b,
            (Value::I16(a),  Value::I16(b))  => a == b,
            (Value::I32(a),  Value::I32(b))  => a == b,
            (Value::I64(a),  Value::I64(b))  => a == b,
            (Value::U8(a),   Value::U8(b))   => a == b,
            (Value::U16(a),  Value::U16(b))  => a == b,
            (Value::U32(a),  Value::U32(b))  => a == b,
            (Value::U64(a),  Value::U64(b))  => a == b,
            (Value::F32(a),  Value::F32(b))  => a == b,
            (Value::F64(a),  Value::F64(b))  => a == b,
            (Value::Bool(a), Value::Bool(b)) => a == b,
            (Value::Char(a), Value::Char(b)) => a == b,
            (Value::Str(a),  Value::Str(b))  => a == b,
            (Value::Null,    Value::Null)    => true,
            // Array equality is reference equality (same as C# reference semantics)
            (Value::Array(a), Value::Array(b)) => Rc::ptr_eq(a, b),
            // Object equality is reference equality
            (Value::Object(a), Value::Object(b)) => Rc::ptr_eq(a, b),
            _ => false,
        }
    }
}

/// Execution mode for a module or function.
#[derive(Debug, Clone, Copy, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub enum ExecMode {
    /// Tree-walking / bytecode interpreter — fast startup, no warmup cost.
    Interp,
    /// Just-in-time compilation — best steady-state throughput.
    Jit,
    /// Ahead-of-time compilation — best for predictable, startup-sensitive code.
    Aot,
}

impl Default for ExecMode {
    fn default() -> Self {
        ExecMode::Interp
    }
}
