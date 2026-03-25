namespace Z42.Compiler.TypeCheck;

/// A single entry in the builtin method registry.
/// One entry drives BOTH compiler phases:
///   TypeChecker  — uses Params + Ret to validate call sites
///   IrGen        — uses IrName to emit the correct BuiltinInstr
///
/// Params = null means variadic / no argument-count checking.
internal sealed record BuiltinMethod(
    IReadOnlyList<Z42Type>? Params,
    Z42Type                 Ret,
    string                  IrName);

/// Unified registry of all pseudo-class static and instance built-in methods.
///
/// Design: to add a new builtin, add ONE line to Static or Instance below.
/// No other files need to change — both the TypeChecker and IrGen read from
/// the same table, ensuring type information and IR names are always in sync.
///
/// Key format for Static:  "ClassName.MethodName"
/// Key format for Instance: "MethodName"   (receiver is not a key dimension)
internal static class BuiltinTable
{
    // Shorthand aliases used in the table literals below.
    private static Z42Type Any  => Z42Type.Unknown;
    private static Z42Type Void => Z42Type.Void;
    private static Z42Type Int  => Z42Type.Int;
    private static Z42Type Dbl  => Z42Type.Double;
    private static Z42Type Bool => Z42Type.Bool;
    private static Z42Type Str  => Z42Type.String;
    private static Z42ArrayType StrArr => new(Z42Type.String);

    /// Static (pseudo-class) calls keyed by "ClassName.MethodName".
    internal static readonly IReadOnlyDictionary<string, BuiltinMethod> Static =
        new Dictionary<string, BuiltinMethod>
        {
            // ── Assert ────────────────────────────────────────────────────────
            ["Assert.Equal"]    = new([Any,  Any],   Void, "__assert_eq"),
            ["Assert.True"]     = new([Bool],         Void, "__assert_true"),
            ["Assert.False"]    = new([Bool],         Void, "__assert_false"),
            ["Assert.Contains"] = new([Any,  Any],   Void, "__assert_contains"),
            ["Assert.Null"]     = new([Any],          Void, "__assert_null"),
            ["Assert.NotNull"]  = new([Any],          Void, "__assert_not_null"),

            // ── Console (variadic: null Params = no arg-count check) ──────────
            ["Console.WriteLine"] = new(null, Void, "__println"),
            ["Console.Write"]     = new(null, Void, "__print"),

            // ── String static (capital S — not a keyword, usable as identifier) ─
            ["String.IsNullOrEmpty"]      = new([Str], Bool, "__str_is_null_or_empty"),
            ["String.IsNullOrWhiteSpace"] = new([Str], Bool, "__str_is_null_or_whitespace"),

            // ── Convert ───────────────────────────────────────────────────────
            ["Convert.ToInt32"]   = new([Any], Int,  "__int_parse"),
            ["Convert.ToDouble"]  = new([Any], Dbl,  "__double_parse"),
            ["Convert.ToString"]  = new([Any], Str,  "__to_str"),

            // ── Math ──────────────────────────────────────────────────────────
            ["Math.Abs"]     = new([Dbl],      Dbl,  "__math_abs"),
            ["Math.Max"]     = new([Dbl, Dbl], Dbl,  "__math_max"),
            ["Math.Min"]     = new([Dbl, Dbl], Dbl,  "__math_min"),
            ["Math.Pow"]     = new([Dbl, Dbl], Dbl,  "__math_pow"),
            ["Math.Sqrt"]    = new([Dbl],      Dbl,  "__math_sqrt"),
            ["Math.Floor"]   = new([Dbl],      Dbl,  "__math_floor"),
            ["Math.Ceiling"] = new([Dbl],      Dbl,  "__math_ceiling"),
            ["Math.Round"]   = new([Dbl],      Dbl,  "__math_round"),
        };

    /// Instance calls keyed by method name.
    /// Receiver is the first arg at IR-emit time; Params here covers only
    /// the explicit arguments (not the receiver).
    internal static readonly IReadOnlyDictionary<string, BuiltinMethod> Instance =
        new Dictionary<string, BuiltinMethod>
        {
            // ── String ────────────────────────────────────────────────────────
            ["Substring"]  = new(null,         Str,    "__str_substring"),  // 1-arg or 2-arg
            ["StartsWith"] = new([Str],        Bool,   "__str_starts_with"),
            ["EndsWith"]   = new([Str],        Bool,   "__str_ends_with"),
            ["IndexOf"]    = new([Str],        Int,    "__str_index_of"),
            ["Replace"]    = new([Str, Str],   Str,    "__str_replace"),
            ["ToLower"]    = new([],            Str,    "__str_to_lower"),
            ["ToUpper"]    = new([],            Str,    "__str_to_upper"),
            ["Trim"]       = new([],            Str,    "__str_trim"),
            ["TrimStart"]  = new([],            Str,    "__str_trim_start"),
            ["TrimEnd"]    = new([],            Str,    "__str_trim_end"),
            ["Split"]      = new([Str],        StrArr, "__str_split"),
            // ── Shared: string + List<T> ──────────────────────────────────────
            ["Contains"]   = new([Any],        Bool,   "__contains"),
            // ── List<T> ───────────────────────────────────────────────────────
            ["Add"]        = new([Any],        Void,   "__list_add"),
            ["RemoveAt"]   = new([Int],        Void,   "__list_remove_at"),
            ["Clear"]      = new([],            Void,   "__list_clear"),
            ["Insert"]     = new([Int, Any],   Void,   "__list_insert"),
            ["Sort"]       = new([],            Void,   "__list_sort"),
            ["Reverse"]    = new([],            Void,   "__list_reverse"),
            // ── Dictionary<K,V> ───────────────────────────────────────────────
            ["ContainsKey"]= new([Any],        Bool,   "__dict_contains_key"),
            ["Remove"]     = new([Any],        Bool,   "__dict_remove"),
            ["Keys"]       = new([],            Any,    "__dict_keys"),
            ["Values"]     = new([],            Any,    "__dict_values"),
        };
}
