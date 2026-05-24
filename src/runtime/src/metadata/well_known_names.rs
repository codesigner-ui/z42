//! Well-known string constants — qualified stdlib type names + a small set of
//! special builtin / method identifiers consumed in multiple call sites.
//!
//! Centralising these literals lets us rename a stdlib class (e.g.
//! `Std.Int32` → `Std.Primitives.Int32`) by changing one location instead of
//! grep-replacing across `interp/`, `jit/`, `corelib/`.
//!
//! The C# compiler has a counterpart at `z42.IR/WellKnownNames.cs`; both
//! sides should agree on these strings (any change here likely needs a mirror
//! change there).

// ── Qualified stdlib class names ──────────────────────────────────────────
//
// rename-primitives-to-pascal-case (2026-05-24): primitives migrated to BCL
// PascalCase struct names (`Std.Int32` / `Std.Boolean` / `Std.SByte` / ...).
// Source keyword (`int / bool / i8 / ...`) is preserved as an alias resolved
// by the C# TypeChecker via `TypeRegistry.StdlibClassName`.
//
// Narrow integer / unsigned BCL names (`Std.Int16` / `Std.SByte` / `Std.Byte` /
// `Std.UInt16` / `Std.UInt32` / `Std.UInt64`) are NOT registered here — they
// have no `Value` variant to map from (all stored as `Value::I64`) and are
// reached via compile-time-emitted class FQN strings in VCall instructions.

/// Stdlib qualified name for the `int` keyword's BCL struct
/// (`struct Int32 : ...` in z42.core/src/Primitives/Int32.z42).
pub const STD_INT32: &str = "Std.Int32";

/// Stdlib qualified name for the `long` keyword's BCL struct.
pub const STD_INT64: &str = "Std.Int64";

/// Stdlib qualified name for the `double` keyword's BCL struct.
pub const STD_DOUBLE: &str = "Std.Double";

/// Stdlib qualified name for the `float` keyword's BCL struct.
pub const STD_SINGLE: &str = "Std.Single";

/// Stdlib qualified name for the `bool` keyword's BCL struct.
pub const STD_BOOLEAN: &str = "Std.Boolean";

/// Stdlib qualified name for the `char` keyword's BCL struct.
pub const STD_CHAR: &str = "Std.Char";

/// Stdlib qualified name for the `String` primitive class. Note: capitalised
/// because stdlib retains `class String` (lowercase `string` is the source
/// keyword that lexes to this class).
pub const STD_STRING: &str = "Std.String";

/// Root class of the type hierarchy. Every user class implicitly inherits.
pub const STD_OBJECT: &str = "Std.Object";

/// Stdlib's reified-type class returned by `__obj_get_type`.
pub const STD_TYPE: &str = "Std.Type";

/// 2026-05-07 add-array-base-class: runtime base of all `T[]`. `Value::Array`
/// 不携带 TypeDesc 引用，VM 端 `is_instance` / `as_cast` 硬编码识别 `STD_ARRAY`
/// / `STD_OBJECT` 子类型。
pub const STD_ARRAY: &str = "Std.Array";

// ── Well-known builtin names (used outside corelib::dispatch_table) ──────

/// Builtin invoked as the fallback in `dispatch.rs::obj_to_string` when an
/// object's vtable doesn't override `ToString`. Returns the simple class
/// name (e.g. `Foo{...}`).
pub const BUILTIN_OBJ_TO_STR: &str = "__obj_to_str";

// ── Well-known method names (vtable + dispatch lookup keys) ──────────────

/// `Std.Object.ToString()` — vtable key + IR-emitted method name.
pub const METHOD_TO_STRING: &str = "ToString";

/// Per-module static initialiser suffix. Every `__static_init__` function
/// (one per file with non-trivial static fields) ends with this suffix —
/// VM scans `module.func_index` for `*.{METHOD_STATIC_INIT}` to run them
/// before the entry point.
pub const METHOD_STATIC_INIT: &str = "__static_init__";
