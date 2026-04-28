//! Well-known string constants — qualified stdlib type names + a small set of
//! special builtin / method identifiers consumed in multiple call sites.
//!
//! Centralising these literals lets us rename a stdlib class (e.g.
//! `Std.int` → `Std.Primitives.Int`) by changing one location instead of
//! grep-replacing across `interp/`, `jit/`, `corelib/`.
//!
//! The C# compiler has a counterpart at `z42.IR/WellKnownNames.cs`; both
//! sides should agree on these strings (any change here likely needs a mirror
//! change there).

// ── Qualified stdlib class names ──────────────────────────────────────────

/// Stdlib qualified name for the `int` primitive's struct definition
/// (`struct int : ...` declared in z42.core/src/Int.z42).
pub const STD_INT: &str = "Std.int";

/// Stdlib qualified name for the `long` primitive struct.
pub const STD_LONG: &str = "Std.long";

/// Stdlib qualified name for the `double` primitive struct.
pub const STD_DOUBLE: &str = "Std.double";

/// Stdlib qualified name for the `float` primitive struct.
pub const STD_FLOAT: &str = "Std.float";

/// Stdlib qualified name for the `bool` primitive struct.
pub const STD_BOOL: &str = "Std.bool";

/// Stdlib qualified name for the `char` primitive struct.
pub const STD_CHAR: &str = "Std.char";

/// Stdlib qualified name for the `String` primitive class. Note: capitalised
/// because stdlib retains `class String` (lowercase `string` is the source
/// keyword that lexes to this class).
pub const STD_STRING: &str = "Std.String";

/// Root class of the type hierarchy. Every user class implicitly inherits.
pub const STD_OBJECT: &str = "Std.Object";

/// Stdlib's reified-type class returned by `__obj_get_type`.
pub const STD_TYPE: &str = "Std.Type";

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
