namespace Z42.Compiler.TypeCheck;

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
        // null is assignable to any reference type
        if (source is Z42NullType && IsReferenceType(target)) return true;
        // Numeric widening: int → long → float → double
        if (target == Long   && source == Int) return true;
        if (target == Float  && source == Int) return true;
        if (target == Double && source is Z42PrimType { Name: "int" or "long" or "float" }) return true;
        // long → double (common in C#)
        if (target == Double && source == Long) return true;
        return false;
    }

    public static bool IsNumeric(Z42Type t) =>
        t is Z42PrimType { Name: "int" or "long" or "float" or "double" };

    public static bool IsBool(Z42Type t) => t == Bool;

    public static bool IsReferenceType(Z42Type t) =>
        t is Z42PrimType { Name: "string" or "object" }
        or Z42ArrayType
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
public sealed record Z42FuncType(IReadOnlyList<Z42Type> Params, Z42Type Ret) : Z42Type
{
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
