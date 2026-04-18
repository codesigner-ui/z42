using Z42.IR;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// Central registry of all primitive type definitions.
/// Single source of truth for type metadata, names, aliases, and conversions.
///
/// All three places that define type mappings (SymbolTable.ResolveType, FunctionEmitter.IrTypeByName, Z42Type.PrimTable)
/// now derive their lookups from this registry to ensure consistency.
/// </summary>
public static class TypeRegistry
{
    /// <summary>
    /// Metadata for a single primitive type definition.
    /// </summary>
    public sealed record TypeEntry(
        /// The canonical name (e.g. "int", not "i32")
        string CanonicalName,

        /// Alternative names that map to the same type (e.g. "i32" → "int")
        string[] Aliases,

        /// IR type tag
        IrType IrType,

        /// Singleton Z42Type instance (or null if derived lazily)
        Z42Type? Z42Type,

        /// True if this is a numeric type (int, float, etc.)
        bool IsNumeric,

        /// True if this is an integral type (int, long, i8, u16, etc.)
        bool IsIntegral,

        /// True if this is a reference type (string, object)
        bool IsReference,

        /// Literal range check: (min, max) or null if unbounded
        (long Min, long Max)? LiteralRange)
    {
        /// All names that refer to this type (canonical + aliases)
        public IEnumerable<string> AllNames => new[] { CanonicalName }.Concat(Aliases);
    }

    /// All type definitions, in canonical order
    private static readonly TypeEntry[] AllTypes = new[]
    {
        // Signed integers
        new TypeEntry("int",    ["i32"],             IrType.I32, Z42Type.Int,    true, true, false, ((long)int.MinValue,   (long)int.MaxValue)),
        new TypeEntry("long",   ["i64"],             IrType.I64, Z42Type.Long,   true, true, false, null),
        new TypeEntry("i8",     ["sbyte"],           IrType.I8,  Z42Type.I8,     true, true, false, ((long)sbyte.MinValue, (long)sbyte.MaxValue)),
        new TypeEntry("i16",    ["short"],           IrType.I16, Z42Type.I16,    true, true, false, ((long)short.MinValue, (long)short.MaxValue)),

        // Unsigned integers
        new TypeEntry("u8",     ["byte"],            IrType.U8,  Z42Type.U8,     true, true, false, (0L, (long)byte.MaxValue)),
        new TypeEntry("u16",    ["ushort"],          IrType.U16, Z42Type.U16,    true, true, false, (0L, (long)ushort.MaxValue)),
        new TypeEntry("u32",    ["uint"],            IrType.U32, Z42Type.U32,    true, true, false, (0L, (long)uint.MaxValue)),
        new TypeEntry("u64",    ["ulong"],           IrType.U64, Z42Type.U64,    true, true, false, (0L, long.MaxValue)),

        // Floating point
        new TypeEntry("float",  ["f32"],             IrType.F32, Z42Type.Float,  true, false, false, null),
        new TypeEntry("double", ["f64"],             IrType.F64, Z42Type.Double, true, false, false, null),

        // Boolean & character
        new TypeEntry("bool",   [],                  IrType.Bool, Z42Type.Bool,   false, false, false, null),
        new TypeEntry("char",   [],                  IrType.Char, Z42Type.Char,   false, false, false, null),

        // String & object
        new TypeEntry("string", [],                  IrType.Str,  Z42Type.String, false, false, true, null),
        new TypeEntry("object", [],                  IrType.Ref,  Z42Type.Object, false, false, true, null),
        new TypeEntry("void",   [],                  IrType.Void, Z42Type.Void,   false, false, false, null),
    };

    /// Lazy-initialized mappings to avoid circular dependencies at module load time
    private static Dictionary<string, TypeEntry>? _byName;
    private static Dictionary<string, IrType>? _irTypeByName;

    /// Get the TypeEntry by canonical or alias name
    public static TypeEntry? GetTypeEntry(string name)
    {
        _byName ??= BuildNameLookup();
        return _byName.TryGetValue(name, out var entry) ? entry : null;
    }

    /// Get the Z42Type by name (canonical or alias)
    public static Z42Type? GetZ42Type(string name)
    {
        var entry = GetTypeEntry(name);
        return entry?.Z42Type;
    }

    /// Get the IrType by name (canonical or alias)
    public static IrType GetIrType(string name)
    {
        _irTypeByName ??= BuildIrTypeLookup();
        return _irTypeByName.TryGetValue(name, out var ir) ? ir : IrType.Unknown;
    }

    /// Get metadata (numeric, integral, reference, range) by type name
    public static (bool IsNumeric, bool IsIntegral, bool IsReference, (long Min, long Max)? Range)? GetPrimMetadata(string name)
    {
        var entry = GetTypeEntry(name);
        return entry != null
            ? (entry.IsNumeric, entry.IsIntegral, entry.IsReference, entry.LiteralRange)
            : null;
    }

    /// Check if a type name is a known primitive type
    public static bool IsPrimitiveType(string name) => GetTypeEntry(name) != null;

    // ── Lookup table builders ─────────────────────────────────────────────────

    private static Dictionary<string, TypeEntry> BuildNameLookup()
    {
        var lookup = new Dictionary<string, TypeEntry>(StringComparer.Ordinal);
        foreach (var entry in AllTypes)
        {
            lookup[entry.CanonicalName] = entry;
            foreach (var alias in entry.Aliases)
                lookup[alias] = entry;
        }
        return lookup;
    }

    private static Dictionary<string, IrType> BuildIrTypeLookup()
    {
        var lookup = new Dictionary<string, IrType>(StringComparer.Ordinal);
        foreach (var entry in AllTypes)
        {
            lookup[entry.CanonicalName] = entry.IrType;
            foreach (var alias in entry.Aliases)
                lookup[alias] = entry.IrType;
        }
        return lookup;
    }
}
