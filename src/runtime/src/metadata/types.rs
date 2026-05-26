use std::collections::HashMap;
use std::sync::Arc;

use crate::gc::GcRef;

// ── TypeDesc — runtime type descriptor ──────────────────────────────────────
//
// Equivalent to CoreCLR's MethodTable: pre-built at module load time,
// shared across all instances of a class via Arc.

/// A single field slot in a class layout (runtime representation).
///
/// review.md E2.P2 Step 1 (2026-05-27): `Box<str>` (16 B per field) instead
/// of `String` (24 B; the `cap` word is dead weight — slot fields are
/// immutable after `build_type_registry`). Saves 16 B per FieldSlot
/// (48 B → 32 B). Full E2.P2 target (48 B → 16 B with `name_id: StringId`
/// + `type_id: TypeId` + `offset` + `flags`) waits on StringId Phase B+
/// migration and a zbc minor bump.
#[derive(Debug, Clone)]
pub struct FieldSlot {
    pub name: Box<str>,
    /// Type tag from zbc (e.g. `"int"`, `"long"`, `"bool"`, `"f64"`, `"str"`,
    /// `"Demo.Box"`, …). Used by `ObjNew` to pick a per-type default `Value`
    /// for fields that have no explicit initializer.
    /// 2026-05-02 fix-class-field-default-init.
    pub type_tag: Box<str>,
}

/// Returns the default `Value` for a field whose declared type tag is
/// `type_tag`. Mirrors the C# `EmitStaticInit` defaults. Used by `ObjNew`
/// (interp + JIT) to initialise fields without an explicit initializer.
///
/// Reference / unknown types fall back to `Null`. `char` follows the existing
/// "char-as-i64" representation (no separate `Value::Char` variant).
pub fn default_value_for(type_tag: &str) -> Value {
    match type_tag {
        "int" | "long" | "short" | "byte" | "sbyte" | "ushort" | "uint" | "ulong"
        | "i8" | "i16" | "i32" | "i64" | "u8" | "u16" | "u32" | "u64"
        | "isize" | "usize" => Value::I64(0),
        "double" | "float" | "f32" | "f64" => Value::F64(0.0),
        "bool" => Value::Bool(false),
        "char" => Value::Char('\0'),
        _ => Value::Null,
    }
}

// ── zbc TypeTag bytes (mirror of C# Opcodes.TypeTags) ────────────────────────
//
// Single source of truth for the 1-byte type tag carried in instruction
// headers / extra fields. Keep these in sync with
// src/compiler/z42.IR/BinaryFormat/Opcodes.cs `TypeTags`.

pub const TAG_UNKNOWN: u8 = 0x00;
pub const TAG_BOOL:    u8 = 0x01;
pub const TAG_I8:      u8 = 0x02;
pub const TAG_I16:     u8 = 0x03;
pub const TAG_I32:     u8 = 0x04;
pub const TAG_I64:     u8 = 0x05;
pub const TAG_U8:      u8 = 0x06;
pub const TAG_U16:     u8 = 0x07;
pub const TAG_U32:     u8 = 0x08;
pub const TAG_U64:     u8 = 0x09;
pub const TAG_F32:     u8 = 0x0A;
pub const TAG_F64:     u8 = 0x0B;
pub const TAG_CHAR:    u8 = 0x0C;
pub const TAG_STR:     u8 = 0x0D;
pub const TAG_OBJECT:  u8 = 0x20;
pub const TAG_ARRAY:   u8 = 0x21;

/// Returns the default `Value` for a slot whose declared element type tag
/// is `tag`. Mirrors `default_value_for(&str)` but keyed on the wire byte
/// directly (no string lookup). Used by `ArrayNew` (interp + JIT) to
/// initialise array elements without an explicit literal.
///
/// fix-array-default-init, 2026-05-18.
pub fn default_value_for_tag(tag: u8) -> Value {
    match tag {
        TAG_BOOL => Value::Bool(false),
        TAG_I8 | TAG_I16 | TAG_I32 | TAG_I64
      | TAG_U8 | TAG_U16 | TAG_U32 | TAG_U64 => Value::I64(0),
        TAG_F32 | TAG_F64 => Value::F64(0.0),
        TAG_CHAR => Value::Char('\0'),
        _ => Value::Null,
    }
}

/// Pre-computed runtime type descriptor (CoreCLR MethodTable equivalent).
///
/// Built once per class at module load time; instances reference it via `Arc`.
/// Includes the flattened inheritance chain for both fields and virtual methods.
#[derive(Debug)]
pub struct TypeDesc {
    /// Fully-qualified class name (e.g. `"Demo.Point"`).
    pub name: String,
    /// Runtime token assigned by `metadata::resolver::resolve_module` (introduce-method-token,
    /// 2026-05-08). Stable for the lifetime of the loaded module; used by VCallIC / FieldIC
    /// for receiver-type comparison without name hash. Default `TypeId::UNRESOLVED` until
    /// resolver runs (back-compat — pre-resolver code doesn't depend on this).
    pub id: super::tokens::TypeId,
    /// Fully-qualified base class name, if any.
    pub base_name: Option<String>,
    /// Field slots in order (base fields first, then derived).
    ///
    /// **Cross-zpkg subclass note** (fix-cross-pkg-subclass-fields, 2026-05-14):
    /// `build_type_registry` populates this with base fields from the local
    /// module's registry only — cross-zpkg base classes contribute nothing
    /// until [`crate::metadata::loader::try_fixup_inheritance`] runs at
    /// lazy-load time, which rebuilds this vector to include inherited slots.
    pub fields: Vec<FieldSlot>,
    /// `field_name → slot index` — O(1) field lookup. Same cross-zpkg
    /// fixup semantics as `fields`.
    pub field_index: HashMap<String, usize>,
    /// Virtual method table: slot → (simple_method_name, qualified_func_name).
    /// Derived class overrides replace base entries at the same slot index.
    /// Same cross-zpkg fixup semantics as `fields`.
    pub vtable: Vec<(String, String)>,
    /// `method_name → vtable slot index` — O(1) virtual dispatch.
    pub vtable_index: HashMap<String, usize>,
    /// fix-cross-pkg-subclass-fields (2026-05-14): the fields **this class
    /// itself declares** (excluding inherited). Preserved so the cross-zpkg
    /// fixup pass can rebuild `fields` = base.fields ++ own_fields once the
    /// base class becomes resolvable via the global type registry.
    ///
    /// review.md E5.3 follow-up (2026-05-27): these five fields are
    /// immutable after `build_type_registry` / `try_fixup_inheritance`
    /// finishes (the latter rewrites `fields` / `vtable` from `own_*` +
    /// base, not these). Stored as `Box<[T]>` (16 B) instead of `Vec<T>`
    /// (24 B) — saves 8 B/field × 5 ≈ 40 B per TypeDesc. `fields`,
    /// `field_index`, `vtable`, `vtable_index` stay growable since fixup
    /// reassigns them.
    pub own_fields: Box<[FieldSlot]>,
    /// fix-cross-pkg-subclass-fields (2026-05-14): the (simple_method_name,
    /// qualified_func_name) pairs **this class itself defines**, in the
    /// order they were discovered by `build_type_registry`. Used by fixup
    /// to rebuild `vtable` (preserving override-vs-append semantics) once
    /// the base class becomes resolvable.
    pub own_methods: Box<[(String, String)]>,
    /// Generic type parameter names: ["T"], ["K", "V"]. Empty for non-generic classes.
    pub type_params: Box<[String]>,
    /// Concrete type arguments for an instantiated generic class: ["int"], ["string", "int"].
    /// Empty for non-generic classes and uninstantiated generic definitions.
    pub type_args: Box<[String]>,
    /// L3-G3a: constraint bundle per type parameter (aligned by index with `type_params`).
    /// Empty for non-generic classes; inner bundle may be empty for unconstrained params.
    pub type_param_constraints: Box<[super::bytecode::ConstraintBundle]>,
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
    /// 2026-05-04 expose-weak-ref-builtin (D-1a)：包装 GC 弱引用句柄。
    /// 由 `__obj_make_weak` builtin 创建；`__obj_upgrade_weak` 升格回原对象。
    /// 用户视角是 `Std.WeakHandle` 类（无字段）。
    WeakRef(crate::gc::WeakRef),
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
    /// 2026-05-07 add-default-generic-typeparam (D-8b-3 Phase 2): per-instance
    /// generic type-arguments. For `new Foo<int, string>()` this is
    /// `["int", "string"]`. Empty for non-generic classes and uninstantiated
    /// generic definitions. Index aligns with `type_desc.type_params`.
    /// Read by `DefaultOf` opcode and any future runtime type-args queries.
    ///
    /// review.md E5.4 follow-up (2026-05-27): `Box<[String]>` instead of
    /// `Vec<String>` — written exactly once at `obj.new` time, then
    /// read-only for the object's lifetime. Saves 8 B/ScriptObject vs
    /// `Vec`. StringId migration deferred to Phase B+.
    pub type_args: Box<[String]>,
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
    ///
    /// review.md C1+C3 (2026-05-27): `Arc<str>` instead of `String`. Saves
    /// 8 B/instance (Arc<str> = 16 B vs String = 24 B; no `cap` word) AND
    /// turns clone from O(n) byte copy into O(1) atomic refcount — the
    /// hot-path win for string-heavy interp / format / concat loops.
    /// Arc not Rc because `Value: Send + Sync` (see
    /// `gc/arc_heap_tests/send_sync.rs::assert_send_sync::<Value>()`).
    Str(Arc<str>),
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
    /// literals (see docs/design/language/closure.md §6). Indirect call dispatches
    /// to the named function in the loaded module.
    FuncRef(String),
    /// L3 capturing closure value: pairs a heap-allocated env (Vec<Value>)
    /// with the lifted function's qualified name. CallIndirect on a Closure
    /// passes `env` as the callee's first implicit parameter and copies user
    /// args after it. See docs/design/language/closure.md §6 + impl-closure-l3-core.
    Closure { env: GcRef<Vec<Value>>, fn_name: String },
    /// 2026-05-02 impl-closure-l3-escape-stack: 栈分配的 capturing closure 值。
    /// `env_idx` 索引创建该 closure 的 frame 的 `env_arena: Vec<Vec<Value>>`；
    /// CallIndirect 时由 dispatch 端通过当前帧的 arena 解 env。compiler 经
    /// escape 分析证明 closure 不离开创建 frame 时才发射该 variant；逃逸
    /// 场景仍走 `Value::Closure`。详见
    /// `docs/spec/archive/2026-05-02-impl-closure-l3-escape-stack/`。
    StackClosure { env_idx: u32, fn_name: String },
    /// Spec impl-ref-out-in-runtime: `ref` / `out` / `in` 参数运行时表达。
    /// 持有该 Value 的寄存器在 frame.get/set 时被透明 deref（单点 dispatch，
    /// 见 `interp/mod.rs::Frame::get`）。引用永远不离开调用栈帧（前置 spec
    /// design Decision 9 + R1），因此 Stack kind 的 frame_idx 不会 stale。
    Ref { kind: RefKind },
}

/// Spec impl-ref-out-in-runtime: 描述 `Value::Ref` 指向的底层位置类型。
#[derive(Debug, Clone)]
pub enum RefKind {
    /// 指向 caller 调用栈第 `frame_idx` 层 frame 的 reg[`slot`]。
    /// `frame_idx` 是 `VmContext.frame_state_at` 列表索引。
    Stack { frame_idx: u32, slot: u32 },
    /// 指向 caller 数组对象的 `idx` 元素。GcRef 持有数组，让 GC 跟随。
    Array { gc_ref: GcRef<Vec<Value>>, idx: usize },
    /// 指向 caller 对象的命名字段。
    Field { gc_ref: GcRef<ScriptObject>, field_name: String },
}

/// Origin of a [`Value::PinnedView`]. Recorded for diagnostics; both kinds
/// share the same wire form (raw bytes + length).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PinSourceKind {
    Str,
    ArrayU8,
}

impl Value {
    /// **add-write-barriers (2026-05-21)**: returns `true` iff writing
    /// this value into a heap slot must dispatch a GC write barrier.
    /// Heap-ref variants: `Object` / `Array` / `Closure` (Closure.env is a
    /// `GcRef<Vec<Value>>`) / `Ref` with `RefKind::Array` or `RefKind::Field`
    /// (the inner `gc_ref` is a real heap edge). All primitives, plus
    /// `FuncRef` (string-keyed func table) / `PinnedView` (raw ptr) /
    /// `StackClosure` (stack arena env) / `Ref::Stack` (stack location)
    /// return `false` — none of them create a strong heap → heap edge
    /// that card-marking or SATB collectors would care about.
    ///
    /// Mirrors the variant selection of [`Value::trace_children`] —
    /// `is_heap_ref` is the predicate, `trace_children` is the traversal.
    #[inline]
    pub fn is_heap_ref(&self) -> bool {
        matches!(
            self,
            Value::Object(_)
                | Value::Array(_)
                | Value::Closure { .. }
                | Value::Ref { kind: RefKind::Array { .. } | RefKind::Field { .. } }
        )
    }

    /// **add-mark-sweep-collector P1 (2026-05-21)**: visit every direct
    /// child `Value` reachable from `self`. Used by the mark phase BFS
    /// to extend reachability through reference-bearing variants
    /// (Object slots, Array elements, Closure env, Ref::Array/Field
    /// inner `GcRef`).
    ///
    /// Primitives (I64 / F64 / Bool / Char / Str / Null / FuncRef /
    /// PinnedView / StackClosure / Ref::Stack) yield no children.
    /// Mirrors `ArcMagrGC::scan_object_refs` (will become its
    /// authoritative source once trial-deletion is removed in P3).
    pub fn trace_children(&self, visit: &mut dyn FnMut(&Value)) {
        match self {
            Value::Object(rc) => {
                let obj = rc.borrow();
                for slot in &obj.slots { visit(slot); }
            }
            Value::Array(rc) => {
                let arr = rc.borrow();
                for elem in arr.iter() { visit(elem); }
            }
            Value::Closure { env, .. } => {
                let arr = env.borrow();
                for elem in arr.iter() { visit(elem); }
            }
            Value::Ref { kind } => match kind {
                RefKind::Stack { .. } => {}
                RefKind::Array { gc_ref, .. } => {
                    let arr = gc_ref.borrow();
                    for elem in arr.iter() { visit(elem); }
                }
                RefKind::Field { gc_ref, .. } => {
                    let obj = gc_ref.borrow();
                    for slot in &obj.slots { visit(slot); }
                }
            },
            // Primitives — no children.
            Value::I64(_) | Value::F64(_) | Value::Bool(_) | Value::Char(_)
            | Value::Str(_) | Value::Null | Value::FuncRef(_)
            | Value::PinnedView { .. } | Value::StackClosure { .. } => {}
        }
    }
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
            // Spec impl-ref-out-in-runtime: Ref 比较按 RefKind 字段；
            // Array/Field kind 用 GcRef::ptr_eq（同 Object/Array 引用语义）；
            // Stack kind 比 frame_idx + slot（指向同一栈位置）。
            (Value::Ref { kind: a }, Value::Ref { kind: b }) => match (a, b) {
                (RefKind::Stack { frame_idx: f1, slot: s1 },
                 RefKind::Stack { frame_idx: f2, slot: s2 }) => f1 == f2 && s1 == s2,
                (RefKind::Array { gc_ref: g1, idx: i1 },
                 RefKind::Array { gc_ref: g2, idx: i2 }) => GcRef::ptr_eq(g1, g2) && i1 == i2,
                (RefKind::Field { gc_ref: g1, field_name: n1 },
                 RefKind::Field { gc_ref: g2, field_name: n2 }) => GcRef::ptr_eq(g1, g2) && n1 == n2,
                _ => false,
            },
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
