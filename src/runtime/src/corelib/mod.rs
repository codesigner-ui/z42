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
pub mod array;
pub mod char;
pub mod gc;
pub mod bench;
pub mod process;
pub mod platform;
pub mod system;
pub mod threading;
pub mod sync;

use crate::metadata::tokens::BuiltinId;
use crate::metadata::Value;
use crate::vm_context::VmContext;
use anyhow::Result;
use std::collections::HashMap;
use std::sync::OnceLock;

/// Function pointer type for all native builtins.
///
/// Carries `&VmContext` so builtins can access the GC heap (e.g. allocate
/// `Std.Type` objects via `ctx.heap().alloc_object(...)`) and other runtime
/// state. **2026-04-29 extend-native-fn-signature** added `&VmContext` —
/// previously `fn(&[Value]) -> Result<Value>`, which forced corelib allocation
/// callsites to bypass the heap interface.
pub type NativeFn = fn(&VmContext, &[Value]) -> Result<Value>;

/// Single source of truth for all corelib builtins. Each entry's index
/// (position in this slice) is its **stable `BuiltinId`** for the lifetime
/// of the process — the resolver assigns IDs by walking this slice.
///
/// `introduce-method-token` (2026-05-08): replaces ad-hoc `HashMap` with an
/// indexed array so dispatch hot path can `BUILTINS[id.0 as usize].1(ctx, args)`
/// without hashing. The HashMap-based `exec_builtin(name, args)` entry point
/// remains as fallback for paths that haven't threaded `BuiltinId` yet (e.g.
/// JIT helpers Phase 1).
const BUILTINS: &[(&str, NativeFn)] = &[
    // ── I/O ────────────────────────────────────────────────────────────────────
    ("__println",  io::builtin_println),
    ("__print",    io::builtin_print),
    ("__eprintln", io::builtin_eprintln),
    ("__eprint",   io::builtin_eprint),
    ("__readline", io::builtin_readline),
    ("__concat",   io::builtin_concat),
    ("__len",      io::builtin_len),
    ("__contains", io::builtin_contains),

    // ── TestIO sinks (R2 完整版) ──────────────────────────────────────────────
    ("__test_io_install_stdout_sink", io::builtin_test_io_install_stdout_sink),
    ("__test_io_take_stdout_buffer",  io::builtin_test_io_take_stdout_buffer),
    ("__test_io_install_stderr_sink", io::builtin_test_io_install_stderr_sink),
    ("__test_io_take_stderr_buffer",  io::builtin_test_io_take_stderr_buffer),

    // ── Bencher helpers (R2 完整版) ───────────────────────────────────────────
    ("__bench_now_ns",     bench::builtin_bench_now_ns),
    ("__bench_black_box",  bench::builtin_bench_black_box),

    // ── String (minimal intrinsic core; most methods are script-side now) ────
    ("__str_length",     string::builtin_str_length),
    ("__str_char_at",    string::builtin_str_char_at),
    ("__str_from_chars", string::builtin_str_from_chars),
    ("__str_to_string",  string::builtin_str_to_string),
    ("__str_equals",     string::builtin_str_equals),
    ("__str_hash_code",  string::builtin_str_hash_code),

    // ── Char ──────────────────────────────────────────────────────────────────
    ("__char_is_whitespace", char::builtin_char_is_whitespace),
    ("__char_to_lower",      char::builtin_char_to_lower),
    ("__char_to_upper",      char::builtin_char_to_upper),

    // ── Parse / convert ───────────────────────────────────────────────────────
    ("__long_parse",   convert::builtin_long_parse),
    ("__int_parse",    convert::builtin_int_parse),
    // add-narrow-int-primitives (2026-05-15): per-type Parse with range
    // validation. Underlying Value is still I64; these only differ from
    // __int_parse in the [min, max] check.
    ("__i8_parse",     convert::builtin_i8_parse),
    ("__i16_parse",    convert::builtin_i16_parse),
    ("__u8_parse",     convert::builtin_u8_parse),
    ("__u16_parse",    convert::builtin_u16_parse),
    ("__u32_parse",    convert::builtin_u32_parse),
    ("__u64_parse",    convert::builtin_u64_parse),
    ("__double_parse", convert::builtin_double_parse),
    ("__to_str",       convert::builtin_to_str),

    // ── Primitive IComparable / IEquatable (L3-G4b) ───────────────────────────
    ("__int_equals",        convert::builtin_int_equals),
    ("__int_hash_code",     convert::builtin_int_hash_code),
    ("__int_to_string",     convert::builtin_int_to_string),
    ("__double_equals",     convert::builtin_double_equals),
    ("__double_hash_code",  convert::builtin_double_hash_code),
    ("__double_to_string",  convert::builtin_double_to_string),
    ("__char_equals",       convert::builtin_char_equals),
    ("__char_hash_code",    convert::builtin_char_hash_code),
    ("__char_to_string",    convert::builtin_char_to_string),
    ("__str_compare_to",    convert::builtin_str_compare_to),

    // ── Math ──────────────────────────────────────────────────────────────────
    ("__math_pow",     math::builtin_math_pow),
    ("__math_sqrt",    math::builtin_math_sqrt),
    ("__math_floor",   math::builtin_math_floor),
    ("__math_ceiling", math::builtin_math_ceiling),
    ("__math_round",   math::builtin_math_round),
    ("__math_log",     math::builtin_math_log),
    ("__math_log10",   math::builtin_math_log10),
    ("__math_sin",     math::builtin_math_sin),
    ("__math_cos",     math::builtin_math_cos),
    ("__math_tan",     math::builtin_math_tan),
    ("__math_atan2",   math::builtin_math_atan2),
    ("__math_exp",     math::builtin_math_exp),

    // ── File I/O ──────────────────────────────────────────────────────────────
    ("__file_read_text",   fs::builtin_file_read_text),
    ("__file_write_text",  fs::builtin_file_write_text),
    ("__file_append_text", fs::builtin_file_append_text),
    ("__file_exists",      fs::builtin_file_exists),
    ("__file_delete",      fs::builtin_file_delete),

    // ── Directory（add-std-io-directory，2026-05-13）──────────────────────────
    ("__dir_exists",              fs::builtin_dir_exists),
    ("__dir_create",              fs::builtin_dir_create),
    ("__dir_delete",              fs::builtin_dir_delete),
    ("__dir_enumerate",           fs::builtin_dir_enumerate),
    ("__dir_enumerate_recursive", fs::builtin_dir_enumerate_recursive),

    // ── Glob + Temp（extend-z42-io-glob-temp，2026-05-16）─────────────────────
    ("__path_glob",             fs::builtin_path_glob),
    ("__file_create_temp_dir",  fs::builtin_file_create_temp_dir),
    ("__file_create_temp_file", fs::builtin_file_create_temp_file),

    // ── Script helpers（extend-z42-io-script-helpers, 2026-05-16）────────────
    ("__file_make_executable",      fs::builtin_file_make_executable),
    ("__file_link",                 fs::builtin_file_link),
    ("__file_symlink",              fs::builtin_file_symlink),
    ("__file_get_size",             fs::builtin_file_get_size),
    ("__console_is_terminal",       fs::builtin_console_is_terminal),
    ("__console_error_is_terminal", fs::builtin_console_error_is_terminal),
    ("__env_get_cwd",               fs::builtin_env_get_cwd),
    ("__env_set_cwd",               fs::builtin_env_set_cwd),

    // ── Environment / Process ─────────────────────────────────────────────────
    ("__env_get",      fs::builtin_env_get),
    ("__env_args",     fs::builtin_env_args),
    ("__process_exit", fs::builtin_process_exit),
    ("__time_now_ms",  fs::builtin_time_now_ms),

    // ── Object protocol ───────────────────────────────────────────────────────
    ("__obj_get_type",  object::builtin_obj_get_type),
    ("__obj_ref_eq",    object::builtin_obj_ref_eq),
    ("__obj_hash_code", object::builtin_obj_hash_code),
    ("__obj_equals",    object::builtin_obj_equals),
    ("__obj_to_str",    object::builtin_obj_to_str),
    ("__delegate_eq",   object::builtin_delegate_eq),
    ("__delegate_target", object::builtin_delegate_target),
    ("__delegate_fn_name", object::builtin_delegate_fn_name),
    ("__make_closure", object::builtin_make_closure),
    ("__obj_make_weak", object::builtin_obj_make_weak),
    ("__obj_upgrade_weak", object::builtin_obj_upgrade_weak),

    // ── Array protocol（add-array-base-class，2026-05-07）─────────────────────
    ("__array_clone", array::builtin_array_clone),

    // ── GC control（Phase 3d.2 expose-gc-to-scripts） ────────────────────────
    ("__gc_collect",       gc::builtin_gc_collect),
    ("__gc_used_bytes",    gc::builtin_gc_used_bytes),
    ("__gc_force_collect", gc::builtin_gc_force_collect),

    // ── GCHandle struct + HeapStats（reorganize-gc-stdlib，2026-05-07）───────
    ("__gc_handle_alloc",    gc::builtin_gc_handle_alloc),
    ("__gc_handle_target",   gc::builtin_gc_handle_target),
    ("__gc_handle_is_alloc", gc::builtin_gc_handle_is_alloc),
    ("__gc_handle_kind",     gc::builtin_gc_handle_kind),
    ("__gc_handle_free",     gc::builtin_gc_handle_free),
    ("__gc_stats",           gc::builtin_gc_stats),

    // ── add-std-io-polish (2026-05-12) — appended to preserve existing BuiltinIds ──
    ("__file_copy",  fs::builtin_file_copy),
    ("__file_move",  fs::builtin_file_move),
    ("__env_set",    fs::builtin_env_set),

    // ── add-std-process (2026-05-13) — appended to preserve existing BuiltinIds ──
    ("__process_run",                 process::builtin_process_run),
    ("__process_spawn",               process::builtin_process_spawn),
    ("__process_handle_wait",         process::builtin_process_handle_wait),
    ("__process_handle_try_wait",     process::builtin_process_handle_try_wait),
    ("__process_handle_kill",         process::builtin_process_handle_kill),
    ("__process_handle_write_stdin",  process::builtin_process_handle_write_stdin),
    ("__process_handle_close_stdin",  process::builtin_process_handle_close_stdin),
    ("__process_handle_pid",          process::builtin_process_handle_pid),
    ("__process_handle_drop",         process::builtin_process_handle_drop),

    // ── add-platform-os-stdlib (2026-05-14) — appended to preserve existing BuiltinIds ──
    ("__platform_os",         platform::builtin_platform_os),
    ("__platform_arch",       platform::builtin_platform_arch),
    ("__platform_family",     platform::builtin_platform_family),
    ("__platform_os_kind",    platform::builtin_platform_os_kind),
    ("__platform_arch_kind",  platform::builtin_platform_arch_kind),
    ("__system_pid",          system::builtin_system_pid),
    ("__system_exe_path",     system::builtin_system_exe_path),
    ("__system_cwd",          system::builtin_system_cwd),
    ("__system_set_cwd",      system::builtin_system_set_cwd),
    ("__system_hostname",     system::builtin_system_hostname),
    ("__system_cpu_count",    system::builtin_system_cpu_count),
    ("__system_os_version",   system::builtin_system_os_version),
    ("__env_unset",           fs::builtin_env_unset),
    ("__env_vars",            fs::builtin_env_vars),

    // ── add-threading-stdlib (2026-05-20) — appended to preserve existing BuiltinIds ──
    ("__thread_spawn",        threading::builtin_thread_spawn),
    ("__thread_join",         threading::builtin_thread_join),

    // ── add-sync-primitives (2026-05-20) — appended to preserve existing BuiltinIds ──
    ("__mutex_new",           sync::builtin_mutex_new),
    ("__mutex_lock_acquire",  sync::builtin_mutex_lock_acquire),
    ("__mutex_store",         sync::builtin_mutex_store),
    ("__mutex_unlock",        sync::builtin_mutex_unlock),
    ("__channel_new",         sync::builtin_channel_new),
    ("__channel_send",        sync::builtin_channel_send),
    ("__channel_recv",        sync::builtin_channel_recv),
    ("__channel_try_recv",    sync::builtin_channel_try_recv),
    ("__channel_close",       sync::builtin_channel_close),

    // ── add-sync-primitives-bounded-channel (2026-05-20) — appended to preserve existing BuiltinIds ──
    ("__channel_new_bounded", sync::builtin_channel_new_bounded),

    // ── add-sync-primitives-rwlock (2026-05-20) — appended to preserve existing BuiltinIds ──
    ("__rwlock_new",           sync::builtin_rwlock_new),
    ("__rwlock_read_acquire",  sync::builtin_rwlock_read_acquire),
    ("__rwlock_read_release",  sync::builtin_rwlock_read_release),
    ("__rwlock_write_acquire", sync::builtin_rwlock_write_acquire),
    ("__rwlock_write_store",   sync::builtin_rwlock_write_store),
    ("__rwlock_write_release", sync::builtin_rwlock_write_release),
];

/// Lazy-built `name → BuiltinId` index for `exec_builtin(name, args)` and the
/// resolver's `builtin_id_of` lookup. Built once on first access from
/// `BUILTINS` (single source of truth).
static BUILTIN_INDEX: OnceLock<HashMap<&'static str, u32>> = OnceLock::new();

fn builtin_index() -> &'static HashMap<&'static str, u32> {
    BUILTIN_INDEX.get_or_init(|| {
        BUILTINS.iter().enumerate()
            .map(|(i, (name, _))| (*name, i as u32))
            .collect()
    })
}

/// Resolve a builtin name to its `BuiltinId`. Returns `None` if no builtin
/// with that name is registered. Called by `metadata::resolver` at module
/// load to populate `ResolvedTokens.builtin_tokens`.
pub fn builtin_id_of(name: &str) -> Option<BuiltinId> {
    builtin_index().get(name).copied().map(BuiltinId)
}

/// Fast-path dispatch by id (no hash, just `BUILTINS[id.0 as usize]`).
/// Caller must have validated the id (e.g. resolver assigned it).
#[inline]
pub fn exec_builtin_by_id(ctx: &VmContext, id: BuiltinId, args: &[Value]) -> Result<Value> {
    let idx = id.0 as usize;
    debug_assert!(idx < BUILTINS.len(), "BuiltinId {} out of range", id.0);
    BUILTINS[idx].1(ctx, args)
}

/// Stable public entry point — called by the interpreter and JIT `jit_builtin`.
/// Falls back to `HashMap<&'static str, _>` lookup when caller has only a name
/// (e.g. JIT helpers in Phase 1; cross-zpkg lazy paths). Hot interp path
/// after `introduce-method-token` Phase 1 prefers `exec_builtin_by_id`.
pub fn exec_builtin(ctx: &VmContext, name: &str, args: &[Value]) -> Result<Value> {
    let id = builtin_index()
        .get(name)
        .copied()
        .ok_or_else(|| anyhow::anyhow!("unknown builtin `{name}`"))?;
    BUILTINS[id as usize].1(ctx, args)
}

#[cfg(test)]
mod tests;
