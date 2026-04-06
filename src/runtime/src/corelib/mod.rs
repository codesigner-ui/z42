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
///   `object`      — obj_get_type / obj_ref_eq / obj_hash_code / assert_*

pub mod convert;
pub mod io;
pub mod string;
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

        // ── Parse / convert ───────────────────────────────────────────────────
        m.insert("__long_parse",   convert::builtin_long_parse);
        m.insert("__int_parse",    convert::builtin_int_parse);
        m.insert("__double_parse", convert::builtin_double_parse);
        m.insert("__to_str",       convert::builtin_to_str);

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

// ── Tests ──────────────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;
    use crate::metadata::{ObjectData, Value};

    fn s(v: &str) -> Value { Value::Str(v.into()) }
    fn i(n: i32) -> Value { Value::I32(n) }
    fn i64(n: i64) -> Value { Value::I64(n) }
    fn obj(class_name: &str) -> Value {
        Value::Object(std::rc::Rc::new(std::cell::RefCell::new(ObjectData {
            class_name: class_name.to_string(),
            fields: std::collections::HashMap::new(),
        })))
    }

    // ── __len ─────────────────────────────────────────────────────────────────

    #[test]
    fn len_of_string_is_utf8_bytes() {
        assert_eq!(exec_builtin("__len", &[s("hello")]).unwrap(), i64(5));
    }

    #[test]
    fn len_of_empty_string() {
        assert_eq!(exec_builtin("__len", &[s("")]).unwrap(), i64(0));
    }

    #[test]
    fn len_missing_arg_errors() {
        assert!(exec_builtin("__len", &[]).is_err());
    }

    // ── __str_substring ───────────────────────────────────────────────────────

    #[test]
    fn substring_one_arg() {
        assert_eq!(
            exec_builtin("__str_substring", &[s("Hello, World!"), i(7)]).unwrap(),
            s("World!")
        );
    }

    #[test]
    fn substring_two_args() {
        assert_eq!(
            exec_builtin("__str_substring", &[s("Hello, World!"), i(7), i(5)]).unwrap(),
            s("World")
        );
    }

    #[test]
    fn substring_out_of_range_errors() {
        assert!(exec_builtin("__str_substring", &[s("hi"), i(10)]).is_err());
    }

    // ── __str_contains ────────────────────────────────────────────────────────

    #[test]
    fn contains_true() {
        assert_eq!(
            exec_builtin("__str_contains", &[s("Hello, World!"), s("World")]).unwrap(),
            Value::Bool(true)
        );
    }

    #[test]
    fn contains_false() {
        assert_eq!(
            exec_builtin("__str_contains", &[s("Hello"), s("world")]).unwrap(),
            Value::Bool(false)
        );
    }

    // ── __str_starts_with ─────────────────────────────────────────────────────

    #[test]
    fn starts_with_true() {
        assert_eq!(
            exec_builtin("__str_starts_with", &[s("Hello, World!"), s("Hello")]).unwrap(),
            Value::Bool(true)
        );
    }

    #[test]
    fn starts_with_false() {
        assert_eq!(
            exec_builtin("__str_starts_with", &[s("Hello"), s("World")]).unwrap(),
            Value::Bool(false)
        );
    }

    // ── __str_ends_with ───────────────────────────────────────────────────────

    #[test]
    fn ends_with_true() {
        assert_eq!(
            exec_builtin("__str_ends_with", &[s("Hello, World!"), s("!")]).unwrap(),
            Value::Bool(true)
        );
    }

    #[test]
    fn ends_with_false() {
        assert_eq!(
            exec_builtin("__str_ends_with", &[s("Hello"), s("World")]).unwrap(),
            Value::Bool(false)
        );
    }

    // ── dispatch table coverage ───────────────────────────────────────────────

    #[test]
    fn unknown_builtin_errors() {
        assert!(exec_builtin("__nonexistent", &[]).is_err());
    }

    #[test]
    fn println_via_dispatch_table() {
        assert!(exec_builtin("__println", &[s("test")]).is_ok());
    }

    // ── assert ────────────────────────────────────────────────────────────────

    #[test]
    fn assert_eq_success() {
        assert!(exec_builtin("__assert_eq", &[i64(42), i64(42)]).is_ok());
    }

    #[test]
    fn assert_eq_failure() {
        assert!(exec_builtin("__assert_eq", &[i64(1), i64(2)]).is_err());
    }

    // ── __obj_get_type ────────────────────────────────────────────────────────

    #[test]
    fn obj_get_type_returns_type_object() {
        let result = exec_builtin("__obj_get_type", &[obj("Foo")]).unwrap();
        match result {
            Value::Object(rc) => assert_eq!(rc.borrow().class_name, "z42.core.Type"),
            other => panic!("expected Object, got {:?}", other),
        }
    }

    #[test]
    fn obj_get_type_simple_name_no_namespace() {
        let result = exec_builtin("__obj_get_type", &[obj("Foo")]).unwrap();
        let Value::Object(rc) = result else { panic!("expected Object") };
        let borrow = rc.borrow();
        assert_eq!(borrow.fields["__name"],     Value::Str("Foo".into()));
        assert_eq!(borrow.fields["__fullName"], Value::Str("Foo".into()));
    }

    #[test]
    fn obj_get_type_namespaced_class_splits_name() {
        let result = exec_builtin("__obj_get_type", &[obj("geometry.Circle")]).unwrap();
        let Value::Object(rc) = result else { panic!("expected Object") };
        let borrow = rc.borrow();
        assert_eq!(borrow.fields["__name"],     Value::Str("Circle".into()));
        assert_eq!(borrow.fields["__fullName"], Value::Str("geometry.Circle".into()));
    }

    #[test]
    fn obj_get_type_null_errors() {
        assert!(exec_builtin("__obj_get_type", &[Value::Null]).is_err());
    }

    #[test]
    fn obj_get_type_non_object_errors() {
        assert!(exec_builtin("__obj_get_type", &[i(42)]).is_err());
    }

    // ── __obj_ref_eq ──────────────────────────────────────────────────────────

    #[test]
    fn obj_ref_eq_same_rc_is_true() {
        let a = obj("Foo");
        assert_eq!(
            exec_builtin("__obj_ref_eq", &[a.clone(), a]).unwrap(),
            Value::Bool(true)
        );
    }

    #[test]
    fn obj_ref_eq_different_allocs_is_false() {
        assert_eq!(
            exec_builtin("__obj_ref_eq", &[obj("Foo"), obj("Foo")]).unwrap(),
            Value::Bool(false)
        );
    }

    #[test]
    fn obj_ref_eq_both_null_is_true() {
        assert_eq!(
            exec_builtin("__obj_ref_eq", &[Value::Null, Value::Null]).unwrap(),
            Value::Bool(true)
        );
    }

    #[test]
    fn obj_ref_eq_one_null_is_false() {
        assert_eq!(
            exec_builtin("__obj_ref_eq", &[obj("Foo"), Value::Null]).unwrap(),
            Value::Bool(false)
        );
        assert_eq!(
            exec_builtin("__obj_ref_eq", &[Value::Null, obj("Foo")]).unwrap(),
            Value::Bool(false)
        );
    }

    // ── __obj_hash_code ───────────────────────────────────────────────────────

    #[test]
    fn obj_hash_code_returns_i32() {
        let result = exec_builtin("__obj_hash_code", &[obj("Foo")]).unwrap();
        assert!(matches!(result, Value::I32(_)));
    }

    #[test]
    fn obj_hash_code_same_object_is_consistent() {
        let a = obj("Foo");
        let h1 = exec_builtin("__obj_hash_code", &[a.clone()]).unwrap();
        let h2 = exec_builtin("__obj_hash_code", &[a]).unwrap();
        assert_eq!(h1, h2);
    }

    #[test]
    fn obj_hash_code_null_is_zero() {
        assert_eq!(
            exec_builtin("__obj_hash_code", &[Value::Null]).unwrap(),
            Value::I32(0)
        );
    }

    #[test]
    fn obj_hash_code_non_object_errors() {
        assert!(exec_builtin("__obj_hash_code", &[i(1)]).is_err());
    }
}
