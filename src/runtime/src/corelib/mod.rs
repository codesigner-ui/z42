/// Core library — native function implementations backing the z42 standard library.
///
/// All functions are reachable via a single stable entry point `exec_builtin(name, args)`
/// which is called by:
///   • the interpreter  (`Instruction::Builtin` in interp/mod.rs)
///   • the JIT backend  (`jit_builtin` extern "C" helper in jit/helpers.rs)
///
/// Submodules are organised by functional category (≈ CoreCLR `classlibnative/`):
///   `convert`     — value_to_str, require_str/usize, parse/to_str
///   `io`          — println, print, readline, concat, len, contains
///   `string`      — str_substring/contains/split/join/format …
///   `math`        — abs/max/min/pow/sqrt/trig …
///   `fs`          — file_* / path_* / env_* / process_exit / time_now_ms
///   `object`      — obj_get_type / obj_ref_eq / obj_hash_code / assert_*
///
/// 2026-04-26 script-first-stringbuilder: removed `string_builder` module —
/// `Std.Text.StringBuilder` is now a pure z42 script in `z42.text`,
/// backed by `List<string>` + `String.FromChars` (no VM intrinsic needed).
///
/// 2026-04-26 extern-audit-wave0: removed `collections` module (13 builtins)
/// — `Std.Collections.List<T>` / `Dictionary<K,V>` are pure z42 scripts atop
/// `T[]`; compiler stopped emitting `__list_*` / `__dict_*` after L3-G4h step3.
///
/// 2026-04-27 wave1-assert-script: removed 6 `__assert_*` builtins —
/// `Std.Assert` methods are now pure z42 scripts (`if (!cond) throw new
/// Exception(...)`), matching BCL `Debug.Assert` / Rust `assert!`.

pub mod convert;
pub mod io;
pub mod string;
pub mod math;
pub mod fs;
pub mod object;
pub mod char;

use crate::metadata::Value;
use anyhow::Result;
use std::collections::HashMap;
use std::sync::OnceLock;

/// Function pointer type for all native builtins.
pub type NativeFn = fn(&[Value]) -> Result<Value>;

static DISPATCH: OnceLock<HashMap<&'static str, NativeFn>> = OnceLock::new();

fn dispatch_table() -> &'static HashMap<&'static str, NativeFn> {
    DISPATCH.get_or_init(|| {
        let mut m: HashMap<&'static str, NativeFn> = HashMap::new();

        // ── I/O ───────────────────────────────────────────────────────────────
        m.insert("__println",  io::builtin_println);
        m.insert("__print",    io::builtin_print);
        m.insert("__readline", io::builtin_readline);
        m.insert("__concat",   io::builtin_concat);
        m.insert("__len",      io::builtin_len);
        m.insert("__contains", io::builtin_contains);

        // ── String (minimal intrinsic core; most methods are script-side now) ─
        // 2026-04-24 simplify-string-stdlib: removed 11 str builtins (contains /
        // starts_with / ends_with / index_of / replace / to_lower / to_upper /
        // trim / trim_start / trim_end / is_null_or_empty / is_null_or_whitespace /
        // substring). Script methods in Std.String.z42 now use char_at + from_chars.
        m.insert("__str_length",     string::builtin_str_length);
        m.insert("__str_char_at",    string::builtin_str_char_at);
        m.insert("__str_from_chars", string::builtin_str_from_chars);
        // 2026-04-27 wave1-string-script: removed __str_split / __str_join —
        // `Std.String.Split` / `Join` 现在是脚本（基于 CharAt + Substring）。
        m.insert("__str_concat",     string::builtin_str_concat);
        m.insert("__str_format",     string::builtin_str_format);
        m.insert("__str_to_string",  string::builtin_str_to_string);
        m.insert("__str_equals",     string::builtin_str_equals);
        m.insert("__str_hash_code",  string::builtin_str_hash_code);

        // ── Char (L3 char primitive helpers for script-side string ops) ──────
        m.insert("__char_is_whitespace", char::builtin_char_is_whitespace);
        m.insert("__char_to_lower",      char::builtin_char_to_lower);
        m.insert("__char_to_upper",      char::builtin_char_to_upper);

        // ── Parse / convert ───────────────────────────────────────────────────
        m.insert("__long_parse",   convert::builtin_long_parse);
        m.insert("__int_parse",    convert::builtin_int_parse);
        m.insert("__double_parse", convert::builtin_double_parse);
        m.insert("__to_str",       convert::builtin_to_str);

        // ── Primitive IComparable / IEquatable (L3-G4b) ───────────────────────
        m.insert("__int_compare_to",    convert::builtin_int_compare_to);
        m.insert("__int_equals",        convert::builtin_int_equals);
        m.insert("__int_hash_code",     convert::builtin_int_hash_code);
        m.insert("__int_to_string",     convert::builtin_int_to_string);
        m.insert("__double_compare_to", convert::builtin_double_compare_to);
        m.insert("__double_equals",     convert::builtin_double_equals);
        m.insert("__double_hash_code",  convert::builtin_double_hash_code);
        m.insert("__double_to_string",  convert::builtin_double_to_string);
        // 2026-04-27 wave1-bool-script: removed __bool_equals / _hash_code /
        // _to_string — `Std.bool` methods are now pure z42 script.
        m.insert("__char_compare_to",   convert::builtin_char_compare_to);
        m.insert("__char_equals",       convert::builtin_char_equals);
        m.insert("__char_hash_code",    convert::builtin_char_hash_code);
        m.insert("__char_to_string",    convert::builtin_char_to_string);
        m.insert("__str_compare_to",    convert::builtin_str_compare_to);

        // ── Math ──────────────────────────────────────────────────────────────
        // 2026-04-27 wave1-math-script: removed __math_abs / _max / _min —
        // `Std.Math.Math.Abs/Max/Min` 现在是脚本（int + double overload）。
        m.insert("__math_pow",     math::builtin_math_pow);
        m.insert("__math_sqrt",    math::builtin_math_sqrt);
        m.insert("__math_floor",   math::builtin_math_floor);
        m.insert("__math_ceiling", math::builtin_math_ceiling);
        m.insert("__math_round",   math::builtin_math_round);
        m.insert("__math_log",     math::builtin_math_log);
        m.insert("__math_log10",   math::builtin_math_log10);
        m.insert("__math_sin",     math::builtin_math_sin);
        m.insert("__math_cos",     math::builtin_math_cos);
        m.insert("__math_tan",     math::builtin_math_tan);
        m.insert("__math_atan2",   math::builtin_math_atan2);
        m.insert("__math_exp",     math::builtin_math_exp);

        // ── File I/O ──────────────────────────────────────────────────────────
        m.insert("__file_read_text",   fs::builtin_file_read_text);
        m.insert("__file_write_text",  fs::builtin_file_write_text);
        m.insert("__file_append_text", fs::builtin_file_append_text);
        m.insert("__file_exists",      fs::builtin_file_exists);
        m.insert("__file_delete",      fs::builtin_file_delete);

        // ── Path ──────────────────────────────────────────────────────────────
        // 2026-04-27 wave1-path-script: removed 5 __path_* — `Std.IO.Path`
        // 现在是脚本实现（Unix `/` 语义；详见 src/libraries/z42.io/src/Path.z42）。

        // ── Environment / Process ─────────────────────────────────────────────
        m.insert("__env_get",      fs::builtin_env_get);
        m.insert("__env_args",     fs::builtin_env_args);
        m.insert("__process_exit", fs::builtin_process_exit);
        m.insert("__time_now_ms",  fs::builtin_time_now_ms);

        // ── Object protocol ───────────────────────────────────────────────────
        m.insert("__obj_get_type",  object::builtin_obj_get_type);
        m.insert("__obj_ref_eq",    object::builtin_obj_ref_eq);
        m.insert("__obj_hash_code", object::builtin_obj_hash_code);
        m.insert("__obj_equals",    object::builtin_obj_equals);
        m.insert("__obj_to_str",    object::builtin_obj_to_str);

        m
    })
}

/// Stable public entry point — called by the interpreter and JIT `jit_builtin`.
pub fn exec_builtin(name: &str, args: &[Value]) -> Result<Value> {
    dispatch_table()
        .get(name)
        .ok_or_else(|| anyhow::anyhow!("unknown builtin `{name}`"))?
        (args)
}

#[cfg(test)]
mod tests;
