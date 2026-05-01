use std::collections::HashMap;
use std::sync::Arc;

use crate::gc::GcRef;

// ── TypeDesc — runtime type descriptor ──────────────────────────────────────
//
// Equivalent to CoreCLR's MethodTable: pre-built at module load time,
// shared across all instances of a class via Arc.

/// A single field slot in a class layout (runtime representation).
#[derive(Debug, Clone)]
pub struct FieldSlot {
    pub name: String,
}

/// Pre-computed runtime type descriptor (CoreCLR MethodTable equivalent).
///
/// Built once per class at module load time; instances reference it via `Arc`.
/// Includes the flattened inheritance chain for both fields and virtual methods.
#[derive(Debug)]
pub struct TypeDesc {
    /// Fully-qualified class name (e.g. `"Demo.Point"`).
    pub name: String,
    /// Fully-qualified base class name, if any.
    pub base_name: Option<String>,
    /// Field slots in order (base fields first, then derived).
    pub fields: Vec<FieldSlot>,
    /// `field_name → slot index` — O(1) field lookup.
    pub field_index: HashMap<String, usize>,
    /// Virtual method table: slot → (simple_method_name, qualified_func_name).
    /// Derived class overrides replace base entries at the same slot index.
    pub vtable: Vec<(String, String)>,
    /// `method_name → vtable slot index` — O(1) virtual dispatch.
    pub vtable_index: HashMap<String, usize>,
    /// Generic type parameter names: ["T"], ["K", "V"]. Empty for non-generic classes.
    pub type_params: Vec<String>,
    /// Concrete type arguments for an instantiated generic class: ["int"], ["string", "int"].
    /// Empty for non-generic classes and uninstantiated generic definitions.
    pub type_args: Vec<String>,
    /// L3-G3a: constraint bundle per type parameter (aligned by index with `type_params`).
    /// Empty for non-generic classes; inner bundle may be empty for unconstrained params.
    pub type_param_constraints: Vec<super::bytecode::ConstraintBundle>,
}

// ── NativeData — native backing for built-in class types ────────────────────
//
// Analogous to CoreCLR's inline data in String/Array objects.
// Provides a native backing store for classes that wrap VM primitives.

/// Native backing data for built-in classes.
///
/// Used by `ScriptObject` to hold VM-managed state that should not be
/// directly accessible as a z42 field (i.e. not visible in `slots`).
#[derive(Debug, Clone)]
pub enum NativeData {
    /// No native backing — ordinary user-defined class.
    None,
    // 2026-04-26 script-first-stringbuilder: removed `StringBuilder(String)` —
    // `Std.Text.StringBuilder` is now a pure z42 script. Variant slot kept open
    // for future native-backed types (Stream / FileHandle / etc.).
}

// ── ScriptObject — unified managed object ───────────────────────────────────
//
// Replaces the old `ObjectData`. Every class instance is represented as a
// `ScriptObject`, which combines:
//   1. A type descriptor pointer (Arc<TypeDesc>) — the class identity
//   2. A flat slot array (Vec<Value>)            — instance fields by index
//   3. Optional native backing (NativeData)      — for built-in types

/// Heap-allocated managed object with reference semantics (CoreCLR Object equivalent).
#[derive(Debug)]
pub struct ScriptObject {
    /// Type descriptor shared across all instances of this class.
    pub type_desc: Arc<TypeDesc>,
    /// Field storage indexed by slot (see `TypeDesc.field_index`).
    pub slots: Vec<Value>,
    /// Native backing for built-in types (e.g. StringBuilder buffer).
    pub native: NativeData,
}

// ── Value ────────────────────────────────────────────────────────────────────

/// Primitive and heap value types that the VM operates on at runtime.
///
/// Integer types are unified as I64 (all integer arithmetic is 64-bit internally).
/// The compiler emits ConstI32/ConstI64 which the VM widens to I64.
/// Floating-point is unified as F64 (double precision).
///
/// `Array` / `Object` 用 [`GcRef<T>`] 作为不透明堆引用句柄。Phase 3a backing
/// 是 `Rc<RefCell<T>>`（行为等价历史 `Rc<RefCell<...>>` 直构）；Phase 3b 切到
/// 自定义堆 + mark-sweep 时，本 enum 与所有 callsite 保持不变。
///
/// `Value::Str` remains a primitive for performance; member access on strings
/// is handled via virtual field dispatch in the interpreter.
///
/// 2026-04-29 remove-dead-value-map: 删除了 `Value::Map` variant —— 自从
/// 2026-04-26 extern-audit-wave0 把 `Std.Collections.Dictionary` 改为纯 z42
/// 脚本类（基于 `T[]`），Map variant 已无创建路径，作为 dead variant 一并清理。
#[derive(Debug, Clone)]
pub enum Value {
    I64(i64),
    F64(f64),
    Bool(bool),
    Char(char),
    /// Immutable string primitive.  `s.Length` → virtual field dispatch in FieldGet.
    Str(String),
    Null,
    /// Heap-allocated dynamic array with reference semantics.
    Array(GcRef<Vec<Value>>),
    /// Heap-allocated managed class instance with reference semantics.
    Object(GcRef<ScriptObject>),
    /// Spec C4 — borrowed view of a `String` / `Array<u8>` for native FFI.
    /// Created by `PinPtr`, released by `UnpinPtr`. The `ptr` is an
    /// untyped raw address — consumers must know the source `kind` to
    /// interpret it. Field access (`.ptr` / `.len`) goes through the
    /// regular `FieldGet` instruction.
    PinnedView { ptr: u64, len: u64, kind: PinSourceKind },
    /// Function reference value. Currently used by L2 no-capture lambda
    /// literals (see docs/design/closure.md §6). Indirect call dispatches
    /// to the named function in the loaded module.
    FuncRef(String),
}

/// Origin of a [`Value::PinnedView`]. Recorded for diagnostics; both kinds
/// share the same wire form (raw bytes + length).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PinSourceKind {
    Str,
    ArrayU8,
}

impl PartialEq for Value {
    fn eq(&self, other: &Self) -> bool {
        match (self, other) {
            (Value::I64(a),  Value::I64(b))  => a == b,
            (Value::F64(a),  Value::F64(b))  => a == b,
            (Value::Bool(a), Value::Bool(b)) => a == b,
            (Value::Char(a), Value::Char(b)) => a == b,
            (Value::Str(a),  Value::Str(b))  => a == b,
            (Value::Null,    Value::Null)    => true,
            // Array/Object equality is reference equality (same as C# reference semantics)
            (Value::Array(a),  Value::Array(b))  => GcRef::ptr_eq(a, b),
            (Value::Object(a), Value::Object(b)) => GcRef::ptr_eq(a, b),
            (
                Value::PinnedView { ptr: ap, len: al, kind: ak },
                Value::PinnedView { ptr: bp, len: bl, kind: bk },
            ) => ap == bp && al == bl && ak == bk,
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

// ── Backward compatibility alias ─────────────────────────────────────────────

/// Deprecated alias kept so external code using `ObjectData` by name continues
/// to compile during the transition.  New code should use `ScriptObject`.
#[deprecated(note = "use ScriptObject instead")]
pub type ObjectData = ScriptObject;
