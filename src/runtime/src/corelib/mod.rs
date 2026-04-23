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
///   `collections` — list_* / dict_*
///   `fs`          — file_* / path_* / env_* / process_exit / time_now_ms
///   `string_builder` — sb_new/append/append_line/append_newline/length/to_string
///   `object`      — obj_get_type / obj_ref_eq / obj_hash_code / assert_*

pub mod convert;
pub mod io;
pub mod string;
pub mod string_builder;
pub mod math;
pub mod collections;
pub mod fs;
pub mod object;

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

        // ── String ────────────────────────────────────────────────────────────
        m.insert("__str_length",                string::builtin_str_length);
        m.insert("__str_substring",             string::builtin_str_substring);
        m.insert("__str_contains",              string::builtin_str_contains);
        m.insert("__str_starts_with",           string::builtin_str_starts_with);
        m.insert("__str_ends_with",             string::builtin_str_ends_with);
        m.insert("__str_index_of",              string::builtin_str_index_of);
        m.insert("__str_replace",               string::builtin_str_replace);
        m.insert("__str_to_lower",              string::builtin_str_to_lower);
        m.insert("__str_to_upper",              string::builtin_str_to_upper);
        m.insert("__str_trim",                  string::builtin_str_trim);
        m.insert("__str_trim_start",            string::builtin_str_trim_start);
        m.insert("__str_trim_end",              string::builtin_str_trim_end);
        m.insert("__str_is_null_or_empty",      string::builtin_str_is_null_or_empty);
        m.insert("__str_is_null_or_whitespace", string::builtin_str_is_null_or_whitespace);
        m.insert("__str_split",                 string::builtin_str_split);
        m.insert("__str_join",                  string::builtin_str_join);
        m.insert("__str_concat",                string::builtin_str_concat);
        m.insert("__str_format",                string::builtin_str_format);
        m.insert("__str_to_string",             string::builtin_str_to_string);
        m.insert("__str_equals",                string::builtin_str_equals);
        m.insert("__str_hash_code",             string::builtin_str_hash_code);

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
        m.insert("__bool_equals",       convert::builtin_bool_equals);
        m.insert("__bool_hash_code",    convert::builtin_bool_hash_code);
        m.insert("__bool_to_string",    convert::builtin_bool_to_string);
        m.insert("__char_compare_to",   convert::builtin_char_compare_to);
        m.insert("__char_equals",       convert::builtin_char_equals);
        m.insert("__char_hash_code",    convert::builtin_char_hash_code);
        m.insert("__char_to_string",    convert::builtin_char_to_string);
        m.insert("__str_compare_to",    convert::builtin_str_compare_to);

        // ── Primitive INumber arithmetic (L3-G2.5 iteration 1) ───────────────
        m.insert("__int_op_add",        convert::builtin_int_op_add);
        m.insert("__int_op_subtract",   convert::builtin_int_op_subtract);
        m.insert("__int_op_multiply",   convert::builtin_int_op_multiply);
        m.insert("__int_op_divide",     convert::builtin_int_op_divide);
        m.insert("__int_op_modulo",     convert::builtin_int_op_modulo);
        m.insert("__double_op_add",        convert::builtin_double_op_add);
        m.insert("__double_op_subtract",   convert::builtin_double_op_subtract);
        m.insert("__double_op_multiply",   convert::builtin_double_op_multiply);
        m.insert("__double_op_divide",     convert::builtin_double_op_divide);
        m.insert("__double_op_modulo",     convert::builtin_double_op_modulo);

        // ── Assert ────────────────────────────────────────────────────────────
        m.insert("__assert_eq",       object::builtin_assert_eq);
        m.insert("__assert_true",     object::builtin_assert_true);
        m.insert("__assert_false",    object::builtin_assert_false);
        m.insert("__assert_null",     object::builtin_assert_null);
        m.insert("__assert_not_null", object::builtin_assert_not_null);
        m.insert("__assert_contains", object::builtin_assert_contains);

        // ── Math ──────────────────────────────────────────────────────────────
        m.insert("__math_abs",     math::builtin_math_abs);
        m.insert("__math_max",     math::builtin_math_max);
        m.insert("__math_min",     math::builtin_math_min);
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

        // ── List ──────────────────────────────────────────────────────────────
        m.insert("__list_new",       collections::builtin_list_new);
        m.insert("__list_add",       collections::builtin_list_add);
        m.insert("__list_remove_at", collections::builtin_list_remove_at);
        m.insert("__list_contains",  collections::builtin_list_contains);
        m.insert("__list_clear",     collections::builtin_list_clear);
        m.insert("__list_insert",    collections::builtin_list_insert);
        m.insert("__list_sort",      collections::builtin_list_sort);
        m.insert("__list_reverse",   collections::builtin_list_reverse);

        // ── Dictionary ────────────────────────────────────────────────────────
        m.insert("__dict_new",          collections::builtin_dict_new);
        m.insert("__dict_contains_key", collections::builtin_dict_contains_key);
        m.insert("__dict_remove",       collections::builtin_dict_remove);
        m.insert("__dict_keys",         collections::builtin_dict_keys);
        m.insert("__dict_values",       collections::builtin_dict_values);

        // ── File I/O ──────────────────────────────────────────────────────────
        m.insert("__file_read_text",   fs::builtin_file_read_text);
        m.insert("__file_write_text",  fs::builtin_file_write_text);
        m.insert("__file_append_text", fs::builtin_file_append_text);
        m.insert("__file_exists",      fs::builtin_file_exists);
        m.insert("__file_delete",      fs::builtin_file_delete);

        // ── Path ──────────────────────────────────────────────────────────────
        m.insert("__path_join",                     fs::builtin_path_join);
        m.insert("__path_get_extension",            fs::builtin_path_get_extension);
        m.insert("__path_get_filename",             fs::builtin_path_get_filename);
        m.insert("__path_get_directory",            fs::builtin_path_get_directory);
        m.insert("__path_get_filename_without_ext", fs::builtin_path_get_filename_without_ext);

        // ── Environment / Process ─────────────────────────────────────────────
        m.insert("__env_get",      fs::builtin_env_get);
        m.insert("__env_args",     fs::builtin_env_args);
        m.insert("__process_exit", fs::builtin_process_exit);
        m.insert("__time_now_ms",  fs::builtin_time_now_ms);

        // ── StringBuilder ─────────────────────────────────────────────────────
        m.insert("__sb_new",            string_builder::builtin_sb_new);
        m.insert("__sb_append",         string_builder::builtin_sb_append);
        m.insert("__sb_append_line",    string_builder::builtin_sb_append_line);
        m.insert("__sb_append_newline", string_builder::builtin_sb_append_newline);
        m.insert("__sb_length",         string_builder::builtin_sb_length);
        m.insert("__sb_to_string",      string_builder::builtin_sb_to_string);

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
