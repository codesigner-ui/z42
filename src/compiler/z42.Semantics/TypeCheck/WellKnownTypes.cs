namespace Z42.Semantics.TypeCheck;

/// <summary>
/// Centralized table of well-known (primitive) Z42 types + canonical
/// name → singleton resolution.
///
/// <para><b>Roslyn parallel</b>: <c>Microsoft.CodeAnalysis.WellKnownTypes</c> /
/// <c>SpecialType</c> registry. Keeps the "list of language primitives"
/// in one place so adding a new primitive (e.g. <c>i128</c>) is a single
/// edit, not a hunt across <see cref="Z42Type"/>, SymbolCollector,
/// FunctionEmitter, and TypeRegistry.</para>
///
/// <para>The actual <see cref="Z42PrimType"/> singletons still live on
/// <see cref="Z42Type"/> for backwards compatibility with ~180 callsites
/// (<c>Z42Type.Int</c> etc.); this class exposes the name → singleton
/// lookup that was previously inlined in <see cref="SymbolCollector"/>.</para>
///
/// <para>docs/review.md Part 6 F5 #1 (2026-05-25).</para>
/// </summary>
public static class WellKnownTypes
{
    /// <summary>
    /// All language-primitive name aliases → canonical singleton.
    /// Both modern (<c>i32 / i64 / u8 ...</c>) and legacy
    /// (<c>int / long / byte / ulong ...</c>) names map to the same instance.
    ///
    /// <para>Used by name resolution (NamedType → Z42Type) at the lexical
    /// boundary; downstream code uses singleton identity.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Z42Type> ByName =
        new Dictionary<string, Z42Type>(StringComparer.Ordinal)
        {
            // Integer aliases
            ["int"]     = Z42Type.Int,
            ["i32"]     = Z42Type.Int,
            ["long"]    = Z42Type.Long,
            ["i64"]     = Z42Type.Long,
            ["sbyte"]   = Z42Type.I8,
            ["i8"]      = Z42Type.I8,
            ["short"]   = Z42Type.I16,
            ["i16"]     = Z42Type.I16,
            ["byte"]    = Z42Type.U8,
            ["u8"]      = Z42Type.U8,
            ["ushort"]  = Z42Type.U16,
            ["u16"]     = Z42Type.U16,
            ["uint"]    = Z42Type.U32,
            ["u32"]     = Z42Type.U32,
            ["ulong"]   = Z42Type.U64,
            ["u64"]     = Z42Type.U64,

            // Floating point aliases
            ["float"]   = Z42Type.Float,
            ["f32"]     = Z42Type.Float,
            ["double"]  = Z42Type.Double,
            ["f64"]     = Z42Type.Double,

            // Other primitives
            ["bool"]    = Z42Type.Bool,
            ["string"]  = Z42Type.String,
            ["char"]    = Z42Type.Char,
            ["object"]  = Z42Type.Object,
            ["void"]    = Z42Type.Void,

            // Type-inference placeholder
            ["var"]     = Z42Type.Unknown,
        };

    /// <summary>
    /// Try to resolve a simple name to a well-known primitive type.
    /// Returns <c>true</c> for any alias listed in <see cref="ByName"/>.
    /// </summary>
    public static bool TryResolve(string name, out Z42Type type)
    {
        if (ByName.TryGetValue(name, out var t)) { type = t; return true; }
        type = Z42Type.Unknown;
        return false;
    }

    /// <summary>
    /// True if <paramref name="name"/> is a known primitive type alias
    /// (any spelling — modern or legacy).
    /// </summary>
    public static bool IsPrimitiveName(string name) => ByName.ContainsKey(name);

    /// <summary>
    /// All distinct primitive singletons (no aliases, no duplicates).
    /// Useful for reflection / completion / docgen.
    /// </summary>
    public static readonly IReadOnlyList<Z42Type> AllPrimitives = new[]
    {
        Z42Type.Int,
        Z42Type.Long,
        Z42Type.I8,
        Z42Type.I16,
        Z42Type.U8,
        Z42Type.U16,
        Z42Type.U32,
        Z42Type.U64,
        Z42Type.Float,
        Z42Type.Double,
        Z42Type.Bool,
        Z42Type.String,
        Z42Type.Char,
        Z42Type.Object,
    };
}
