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
        // Generic type parameter T: compatible with anything (checked at instantiation site)
        if (source is Z42GenericParamType || target is Z42GenericParamType) return true;
        // L3-G4g: Array element of generic parameter — same-named T with different
        // constraint sets (field-stored `T[]` may have no constraints; method-local
        // `T[]` may have active where-clause constraints). Compare element name only.
        if (source is Z42ArrayType sArr && target is Z42ArrayType tArr)
        {
            if (sArr.Element is Z42GenericParamType sg && tArr.Element is Z42GenericParamType tg
                && sg.Name == tg.Name)
                return true;
            // Fall back to element-wise IsAssignableTo for non-generic arrays
            if (IsAssignableTo(tArr.Element, sArr.Element)) return true;
        }
        // null is assignable to any reference type or optional type
        if (source is Z42NullType && (IsReferenceType(target) || target is Z42OptionType)) return true;
        // Same-name class types are compatible (handles two-pass TypeChecker where stubs
        // may be different instances from fully-resolved types of the same class).
        if (source is Z42ClassType srcCt && target is Z42ClassType tgtCt && srcCt.Name == tgtCt.Name)
            return true;
        // L3-G4a: instantiated generic types — compare underlying definition + type args
        if (source is Z42InstantiatedType si && target is Z42InstantiatedType ti
            && si.Definition.Name == ti.Definition.Name
            && si.TypeArgs.Count == ti.TypeArgs.Count
            && Enumerable.Range(0, si.TypeArgs.Count).All(i => IsAssignableTo(ti.TypeArgs[i], si.TypeArgs[i])))
            return true;
        // Instantiated → base definition (ignoring type args) and vice-versa
        if (source is Z42InstantiatedType si2 && target is Z42ClassType tc && si2.Definition.Name == tc.Name)
            return true;
        if (source is Z42ClassType sc && target is Z42InstantiatedType ti2 && sc.Name == ti2.Definition.Name)
            return true;
        // T is assignable to T? (implicit wrap)
        if (target is Z42OptionType opt && IsAssignableTo(opt.Inner, source)) return true;
        // Everything is assignable to `object` (universal base type — primitives auto-box at runtime).
        // VM Value enum carries the original type through; no compiler-side boxing needed.
        if (target == Object && source is not Z42VoidType) return true;
        // Numeric widening: int → long → float → double
        if (target == Long   && source == Int) return true;
        if (target == Float  && source == Int) return true;
        if (target == Double && source is Z42PrimType { Name: "int" or "long" or "float" }) return true;
        // long → double (common in C#)
        if (target == Double && source == Long) return true;
        return false;
    }

    // ── Primitive type metadata (now sourced from TypeRegistry) ──────────────

    private static (bool IsNumeric, bool IsIntegral, bool IsReference, (long Min, long Max)? Range)? LookupPrim(Z42Type t) =>
        t is Z42PrimType { Name: var n } ? TypeRegistry.GetPrimMetadata(n) : null;

    // ── Type predicates (all delegate to TypeRegistry) ──────────────────────

    public static bool IsNumeric(Z42Type t)  => LookupPrim(t)?.IsNumeric == true;
    public static bool IsIntegral(Z42Type t) => LookupPrim(t)?.IsIntegral == true;
    public static bool IsBool(Z42Type t)     => t == Bool;

    public static (long Min, long Max)? IntLiteralRange(Z42Type t) => LookupPrim(t)?.Range;

    public static bool IsReferenceType(Z42Type t) =>
        (LookupPrim(t)?.IsReference == true)
        || t is Z42ArrayType or Z42ClassType or Z42InterfaceType or Z42OptionType
           or Z42GenericParamType or Z42InstantiatedType;

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

/// Uninstantiated generic type parameter (e.g., T in Identity<T>).
/// During type checking of generic function/class bodies, T is bound to this type.
/// At call sites, T is substituted with a concrete type (e.g., int).
///
/// Constraints (L3-G2, L3-G2.5):
/// - `InterfaceConstraints`: interfaces T must implement (multi-allowed via `+`).
/// - `BaseClassConstraint`: at most one base class T must inherit from (or equal).
/// Inside a generic body, `t.Method()` resolves against base class first, then interfaces.
public sealed record Z42GenericParamType(
    string Name,
    IReadOnlyList<Z42InterfaceType>? InterfaceConstraints = null,
    Z42ClassType? BaseClassConstraint = null) : Z42Type
{
    public override string ToString() => Name;
}

/// Resolved constraints for one type parameter. (L3-G2, L3-G2.5)
///
/// `TypeParamConstraint` (L3-G2.5 bare-typeparam) records the name of another
/// type parameter in the same decl that this one must be a subtype of.
public sealed record GenericConstraintBundle(
    Z42ClassType? BaseClass,
    IReadOnlyList<Z42InterfaceType> Interfaces,
    bool RequiresClass = false,
    bool RequiresStruct = false,
    string? TypeParamConstraint = null)
{
    public static readonly GenericConstraintBundle Empty = new(null, []);
    public bool IsEmpty => BaseClass is null && Interfaces.Count == 0
                           && !RequiresClass && !RequiresStruct
                           && TypeParamConstraint is null;
}

/// User-defined class or struct type.
public sealed record Z42ClassType(
    string Name,
    IReadOnlyDictionary<string, Z42Type>      Fields,
    IReadOnlyDictionary<string, Z42FuncType>  Methods,
    IReadOnlyDictionary<string, Z42Type>      StaticFields,
    IReadOnlyDictionary<string, Z42FuncType>  StaticMethods,
    IReadOnlyDictionary<string, Visibility>   MemberVisibility,
    string? BaseClassName = null,
    IReadOnlyList<string>? TypeParams = null,
    bool IsStruct = false) : Z42Type
{
    public override string ToString() => Name;
}

/// Instantiated generic class type. (L3-G4a)
///
/// Produced by `new GenericClass<A, B>(...)` or explicit `GenericClass<A, B>` type expressions.
/// Holds a reference to the generic definition (`Z42ClassType` with `TypeParams`) and the
/// concrete type arguments aligned by index with those type params.
///
/// TypeChecker substitutes type params with these args when binding member access or method
/// calls; IR layer erases the distinction (code sharing — one IR serves all instantiations).
public sealed record Z42InstantiatedType(
    Z42ClassType Definition,
    IReadOnlyList<Z42Type> TypeArgs) : Z42Type
{
    public string Name => Definition.Name;
    public override string ToString() =>
        $"{Definition.Name}<{string.Join(", ", TypeArgs)}>";
}
