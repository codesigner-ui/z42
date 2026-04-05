namespace Z42.IR;

/// <summary>
/// Central registry of all VM-internal (InternalCall) intrinsics.
/// Used by the TypeChecker to validate [Native("__name")] declarations.
/// ParamCount = -1 means variadic / any argument count.
/// </summary>
public record NativeEntry(string Name, int ParamCount);

public static class NativeTable
{
    public static readonly IReadOnlyDictionary<string, NativeEntry> All =
        new Dictionary<string, NativeEntry>
    {
        // ── I/O ──────────────────────────────────────────────────────────────
        ["__println"]  = new("__println",  1),
        ["__print"]    = new("__print",    1),
        ["__readline"] = new("__readline", 0),
        ["__concat"]   = new("__concat",   2),

        // ── Type conversion ───────────────────────────────────────────────────
        ["__int_parse"]    = new("__int_parse",    1),
        ["__long_parse"]   = new("__long_parse",   1),
        ["__double_parse"] = new("__double_parse", 1),
        ["__to_str"]       = new("__to_str",       1),

        // ── Assertions ────────────────────────────────────────────────────────
        ["__assert_eq"]        = new("__assert_eq",        2),
        ["__assert_true"]      = new("__assert_true",      1),
        ["__assert_false"]     = new("__assert_false",     1),
        ["__assert_null"]      = new("__assert_null",      1),
        ["__assert_not_null"]  = new("__assert_not_null",  1),
        ["__assert_contains"]  = new("__assert_contains",  2),

        // ── String ───────────────────────────────────────────────────────────
        ["__len"]                       = new("__len",                       1),
        ["__str_substring"]             = new("__str_substring",            -1), // 2 or 3 args
        ["__str_contains"]              = new("__str_contains",              2),
        ["__str_starts_with"]           = new("__str_starts_with",           2),
        ["__str_ends_with"]             = new("__str_ends_with",             2),
        ["__str_index_of"]              = new("__str_index_of",              2),
        ["__str_replace"]               = new("__str_replace",               3),
        ["__str_split"]                 = new("__str_split",                 2),
        ["__str_join"]                  = new("__str_join",                 -1), // variadic
        ["__str_format"]                = new("__str_format",               -1), // variadic
        ["__str_to_upper"]              = new("__str_to_upper",              1),
        ["__str_to_lower"]              = new("__str_to_lower",              1),
        ["__str_trim"]                  = new("__str_trim",                  1),
        ["__str_trim_start"]            = new("__str_trim_start",            1),
        ["__str_trim_end"]              = new("__str_trim_end",              1),
        ["__str_is_null_or_empty"]      = new("__str_is_null_or_empty",      1),
        ["__str_is_null_or_whitespace"] = new("__str_is_null_or_whitespace", 1),
        ["__str_concat"]                = new("__str_concat",                2),
        ["__contains"]                  = new("__contains",                  2),

        // ── Math ─────────────────────────────────────────────────────────────
        ["__math_abs"]     = new("__math_abs",     1),
        ["__math_max"]     = new("__math_max",     2),
        ["__math_min"]     = new("__math_min",     2),
        ["__math_pow"]     = new("__math_pow",     2),
        ["__math_sqrt"]    = new("__math_sqrt",    1),
        ["__math_floor"]   = new("__math_floor",   1),
        ["__math_ceiling"] = new("__math_ceiling", 1),
        ["__math_round"]   = new("__math_round",   1),
        ["__math_log"]     = new("__math_log",     1),
        ["__math_log10"]   = new("__math_log10",   1),
        ["__math_sin"]     = new("__math_sin",     1),
        ["__math_cos"]     = new("__math_cos",     1),
        ["__math_tan"]     = new("__math_tan",     1),
        ["__math_atan2"]   = new("__math_atan2",   2),
        ["__math_exp"]     = new("__math_exp",     1),

        // ── Collections ──────────────────────────────────────────────────────
        ["__list_new"]       = new("__list_new",       0),
        ["__list_add"]       = new("__list_add",       2),
        ["__list_remove_at"] = new("__list_remove_at", 2),
        ["__list_contains"]  = new("__list_contains",  2),
        ["__list_insert"]    = new("__list_insert",    3),
        ["__list_clear"]     = new("__list_clear",     1),
        ["__list_sort"]      = new("__list_sort",      1),
        ["__list_reverse"]   = new("__list_reverse",   1),
        ["__dict_new"]             = new("__dict_new",             0),
        ["__dict_contains_key"]    = new("__dict_contains_key",    2),
        ["__dict_remove"]          = new("__dict_remove",          2),
        ["__dict_keys"]            = new("__dict_keys",            1),
        ["__dict_values"]          = new("__dict_values",          1),

        // ── File I/O ─────────────────────────────────────────────────────────
        ["__file_read_text"]   = new("__file_read_text",   1),
        ["__file_write_text"]  = new("__file_write_text",  2),
        ["__file_append_text"] = new("__file_append_text", 2),
        ["__file_exists"]      = new("__file_exists",      1),
        ["__file_delete"]      = new("__file_delete",      1),

        // ── Path ─────────────────────────────────────────────────────────────
        ["__path_join"]                     = new("__path_join",                     2),
        ["__path_get_extension"]            = new("__path_get_extension",            1),
        ["__path_get_filename"]             = new("__path_get_filename",             1),
        ["__path_get_directory"]            = new("__path_get_directory",            1),
        ["__path_get_filename_without_ext"] = new("__path_get_filename_without_ext", 1),

        // ── Environment / Process ─────────────────────────────────────────────
        ["__env_get"]      = new("__env_get",      1),
        ["__env_args"]     = new("__env_args",     0),
        ["__process_exit"] = new("__process_exit", 1),
        ["__time_now_ms"]  = new("__time_now_ms",  0),

        // ── Object protocol ───────────────────────────────────────────────────
        ["__obj_get_type"]  = new("__obj_get_type",  1), // (this) -> Type
        ["__obj_ref_eq"]    = new("__obj_ref_eq",    2), // (a, b) -> bool
        ["__obj_hash_code"] = new("__obj_hash_code", 1), // (this) -> int
    };
}
