using Z42.Core.Text;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

// ── Semantic type hierarchy ────────────────────────────────────────────────────

public abstract record Z42Type
{
    // ── Well-known singleton instances ──────────────────────────────────────
    public static readonly Z42PrimType Int    = new("int");
    public static readonly Z42PrimType Long   = new("long");
    public static readonly Z42PrimType Float  = new("float");
    public static readonly Z42PrimType Double = new("double");
    public static readonly Z42PrimType Bool   = new("bool");
    public static readonly Z42PrimType String = new("string");
    public static readonly Z42PrimType Char   = new("char");
    public static readonly Z42PrimType Object = new("object");

    // Explicit-size integer singletons
    public static readonly Z42PrimType I8  = new("i8");
    public static readonly Z42PrimType I16 = new("i16");
    public static readonly Z42PrimType U8  = new("u8");
    public static readonly Z42PrimType U16 = new("u16");
    public static readonly Z42PrimType U32 = new("u32");
    public static readonly Z42PrimType U64 = new("u64");

    public static readonly Z42VoidType  Void    = new();
    public static readonly Z42NullType  Null    = new();
    /// Sentinel: propagated after a type error to suppress cascading diagnostics.
    public static readonly Z42ErrorType Error   = new();
    /// Sentinel: type not yet resolved (e.g. from unknown builtin class).
    public static readonly Z42UnknownType Unknown = new();

    // ── Compatibility helpers ────────────────────────────────────────────────

    /// True if a value of type <paramref name="source"/> can be assigned to
    /// a slot of type <paramref name="target"/> (considers numeric widening).
    public static bool IsAssignableTo(Z42Type target, Z42Type source)
    {
        if (target == source) return true;
        // Error sentinel: never cascade errors
        if (source is Z42ErrorType || target is Z42ErrorType) return true;
        // Unknown: we can't check, so allow
        if (source is Z42UnknownType || target is Z42UnknownType) return true;
        // null is assignable to any reference type or optional type
        if (source is Z42NullType && (IsReferenceType(target) || target is Z42OptionType)) return true;
        // Same-name class types are compatible (handles two-pass TypeChecker where stubs
        // may be different instances from fully-resolved types of the same class).
        if (source is Z42ClassType srcCt && target is Z42ClassType tgtCt && srcCt.Name == tgtCt.Name)
            return true;
        // T is assignable to T? (implicit wrap)
        if (target is Z42OptionType opt && IsAssignableTo(opt.Inner, source)) return true;
        // Numeric widening: int → long → float → double
        if (target == Long   && source == Int) return true;
        if (target == Float  && source == Int) return true;
        if (target == Double && source is Z42PrimType { Name: "int" or "long" or "float" }) return true;
        // long → double (common in C#)
        if (target == Double && source == Long) return true;
        return false;
    }

    public static bool IsNumeric(Z42Type t) =>
        t is Z42PrimType { Name: "int" or "long" or "float" or "double"
                                or "i8" or "i16" or "u8" or "u16" or "u32" or "u64"
                                or "f32" or "f64" };

    /// True for integer types that support bitwise operations.
    public static bool IsIntegral(Z42Type t) =>
        t is Z42PrimType { Name: "int" or "long" or "i8" or "i16" or "u8" or "u16" or "u32" or "u64" };

    /// Returns the valid [min, max] range for an integer literal assigned to <paramref name="t"/>.
    /// Returns null if the type has no constrained integer range (e.g. float, long, unknown).
    public static (long Min, long Max)? IntLiteralRange(Z42Type t) => t switch
    {
        Z42PrimType { Name: "i8"  } => (sbyte.MinValue,  sbyte.MaxValue),
        Z42PrimType { Name: "i16" } => (short.MinValue,  short.MaxValue),
        Z42PrimType { Name: "int" or "i32" } => (int.MinValue, int.MaxValue),
        Z42PrimType { Name: "u8"  } => (0, byte.MaxValue),
        Z42PrimType { Name: "u16" } => (0, ushort.MaxValue),
        Z42PrimType { Name: "u32" } => (0, uint.MaxValue),
        // u64: stored as signed long in AST; permit non-negative values up to long.MaxValue
        Z42PrimType { Name: "u64" } => (0, long.MaxValue),
        // long/i64: no range check needed (long covers the full signed 64-bit range)
        _ => null
    };

    public static bool IsBool(Z42Type t) => t == Bool;

    public static bool IsReferenceType(Z42Type t) =>
        t is Z42PrimType { Name: "string" or "object" or "List" or "Dictionary" }
        or Z42ArrayType
        or Z42ClassType
        or Z42InterfaceType
        or Z42OptionType;

    /// For a binary arithmetic operation, returns the "wider" of two numeric types.
    public static Z42Type ArithmeticResult(Z42Type l, Z42Type r)
    {
        if (l == Double || r == Double) return Double;
        if (l == Float  || r == Float)  return Float;
        if (l == Long   || r == Long)   return Long;
        return Int;
    }
}

public sealed record Z42PrimType(string Name) : Z42Type
{
    public override string ToString() => Name;
}

public sealed record Z42VoidType : Z42Type
{
    public override string ToString() => "void";
}

public sealed record Z42NullType : Z42Type
{
    public override string ToString() => "null";
}

public sealed record Z42ErrorType : Z42Type
{
    public override string ToString() => "<error>";
}

public sealed record Z42UnknownType : Z42Type
{
    public override string ToString() => "<unknown>";
}

/// Function / method type.
public sealed record Z42FuncType(
    IReadOnlyList<Z42Type> Params,
    Z42Type Ret,
    int RequiredCount = -1          // -1 means all params required (no defaults)
) : Z42Type
{
    /// Number of parameters that must be supplied at every call site.
    public int MinArgCount => RequiredCount < 0 ? Params.Count : RequiredCount;
    public override string ToString() =>
        $"({string.Join(", ", Params)}) -> {Ret}";
}

/// `T[]`
public sealed record Z42ArrayType(Z42Type Element) : Z42Type
{
    public override string ToString() => $"{Element}[]";
}

/// `T?`
public sealed record Z42OptionType(Z42Type Inner) : Z42Type
{
    public override string ToString() => $"{Inner}?";
}

/// Interface type (e.g. `IShape`).
public sealed record Z42InterfaceType(
    string Name,
    IReadOnlyDictionary<string, Z42FuncType> Methods) : Z42Type
{
    public override string ToString() => Name;
}

/// User-defined class or struct type.
public sealed record Z42ClassType(
    string Name,
    IReadOnlyDictionary<string, Z42Type>      Fields,
    IReadOnlyDictionary<string, Z42FuncType>  Methods,
    IReadOnlyDictionary<string, Z42Type>      StaticFields,
    IReadOnlyDictionary<string, Z42FuncType>  StaticMethods,
    IReadOnlyDictionary<string, Visibility>   MemberVisibility,
    string? BaseClassName = null) : Z42Type
{
    public override string ToString() => Name;
}
