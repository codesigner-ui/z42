using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Syntax.Parser;
using Z42.IR;

namespace Z42.Semantics.TypeCheck;

/// Generic constraint validation (call-site) + type-parameter substitution helpers
/// — part of the TypeChecker partial class.
///
/// Pairs with `TypeChecker.GenericResolve.cs` (decl-site `where` clause parsing).
public sealed partial class TypeChecker
{
    // ── Generic constraints (L3-G2) ─────────────────────────────────────────

    /// Validate that each type argument satisfies the constraints declared in `where`
    /// on the owning decl. `declName` keys into the constraint map; `typeParams` and
    /// `typeArgs` must have matching counts. Reports `TypeMismatch` per unmet constraint.
    internal void ValidateGenericConstraints(
        string declName,
        IReadOnlyList<string> typeParams,
        IReadOnlyList<Z42Type> typeArgs,
        Dictionary<string, IReadOnlyDictionary<string, GenericConstraintBundle>> constraintMap,
        Span callSpan)
    {
        if (!constraintMap.TryGetValue(declName, out var constraints)) return;
        // Build a name→typeArg map once for bare-typeparam constraint checks.
        var typeArgByName = new Dictionary<string, Z42Type>(StringComparer.Ordinal);
        for (int i = 0; i < typeParams.Count && i < typeArgs.Count; i++)
            typeArgByName[typeParams[i]] = typeArgs[i];

        for (int i = 0; i < typeParams.Count && i < typeArgs.Count; i++)
        {
            if (!constraints.TryGetValue(typeParams[i], out var bundle)) continue;
            var typeArg = typeArgs[i];
            if (bundle.BaseClass is { } baseClass
                && !TypeSatisfiesClassConstraint(typeArg, baseClass))
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type argument `{typeArg}` for `{typeParams[i]}` does not satisfy constraint `{baseClass.Name}` on `{declName}`",
                    callSpan);
            foreach (var iface in bundle.Interfaces)
            {
                // L3-G2.5 chain: substitute cross-param type-args before checking
                // (e.g. `T: IEquatable<U>` with U=string → verify `T` satisfies `IEquatable<string>`).
                var substitutedIface = SubstituteInterfaceTypeArgs(iface, typeArgByName);
                if (!TypeSatisfiesInterface(typeArg, substitutedIface))
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"type argument `{typeArg}` for `{typeParams[i]}` does not satisfy constraint `{substitutedIface}` on `{declName}`",
                        callSpan);
            }
            if (bundle.RequiresClass && !IsClassArg(typeArg))
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type argument `{typeArg}` for `{typeParams[i]}` does not satisfy constraint `class` on `{declName}`",
                    callSpan);
            if (bundle.RequiresStruct && !IsStructArg(typeArg))
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type argument `{typeArg}` for `{typeParams[i]}` does not satisfy constraint `struct` on `{declName}`",
                    callSpan);
            // L3-G2.5 ctor: `where T: new()` — type arg must have a no-arg constructor.
            if (bundle.RequiresConstructor && !HasNoArgConstructor(typeArg))
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type argument `{typeArg}` for `{typeParams[i]}` does not satisfy constraint `new()` on `{declName}`",
                    callSpan);
            // L3-G2.5 enum: `where T: enum` — type arg must be an enum type.
            if (bundle.RequiresEnum && !IsEnumArg(typeArg))
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type argument `{typeArg}` for `{typeParams[i]}` does not satisfy constraint `enum` on `{declName}`",
                    callSpan);
            // L3-G2.5 bare-typeparam: `where U: T` — typeArg[U] must be subtype of typeArg[T].
            if (bundle.TypeParamConstraint is { } otherTp
                && typeArgByName.TryGetValue(otherTp, out var otherArg)
                && !TypeArgSubsumedBy(typeArg, otherArg))
                _diags.Error(DiagnosticCodes.TypeMismatch,
                    $"type argument `{typeArg}` for `{typeParams[i]}` is not a subtype of `{otherArg}` (required by `{typeParams[i]}: {otherTp}` on `{declName}`)",
                    callSpan);
        }
    }

    /// L3-G2.5 bare-typeparam: is `sub` a subtype of `sup`?
    /// - Same type → true; Z42ClassType via IsSubclassOf; error/unknown don't cascade.
    /// - Non-class types only satisfy via equality (primitives / interfaces / arrays).
    private bool TypeArgSubsumedBy(Z42Type sub, Z42Type sup)
    {
        if (sub == sup) return true;
        if (sub is Z42ErrorType or Z42UnknownType) return true;
        if (sup is Z42ErrorType or Z42UnknownType) return true;
        if (sub is Z42ClassType cs && sup is Z42ClassType cp)
            return cs.Name == cp.Name || _symbols.IsSubclassOf(cs.Name, cp.Name);
        return false;
    }

    /// L3-G2.5 ctor: does `typeArg` satisfy `where T: new()`?
    /// - Class: has a 0-arg constructor (ctor name matches class, with 0 params, bare or `$0`).
    /// - Struct / primitive: always OK (default-constructible).
    /// - Generic param: propagate only if T itself has the RequiresConstructor constraint.
    /// - Interface / array / option / func: rejected.
    private bool HasNoArgConstructor(Z42Type t)
    {
        switch (t)
        {
            case Z42ErrorType:
            case Z42UnknownType:
                return true; // don't cascade
            case Z42PrimType:
                return true; // default-constructible
            case Z42ClassType ct:
                if (ct.IsStruct) return true;
                return ClassHasNoArgCtor(ct.Name);
            case Z42InstantiatedType inst:
                if (inst.Definition.IsStruct) return true;
                return ClassHasNoArgCtor(inst.Definition.Name);
            case Z42GenericParamType gp:
                // If T carries `new()` transitively (via active where scope), accept.
                return _symbols.LookupEffectiveConstraints(gp.Name).RequiresConstructor;
            default:
                return false; // interfaces / arrays / options / funcs
        }
    }

    private bool ClassHasNoArgCtor(string className)
    {
        if (!_symbols.Classes.TryGetValue(className, out var ct)) return false;
        // Z42 stores constructors as methods keyed by class name; overloads get $arity suffix.
        // A no-arg ctor appears as either bare name (single ctor) or `Name$0` (overloaded).
        if (ct.Methods.TryGetValue(className, out var bare) && bare.Params.Count == 0) return true;
        if (ct.Methods.TryGetValue($"{className}$0", out _)) return true;
        // Imported classes may not have constructor in Methods map — look for any method whose
        // name starts with the class name followed by empty params (TSIG stores ctors this way).
        // For now, conservative: require explicit ctor presence.
        return false;
    }

    /// L3-G2.5 refvalue: is `typeArg` a reference type (for `where T: class`)?
    private static bool IsClassArg(Z42Type t) => t switch
    {
        Z42ClassType ct               => !ct.IsStruct,
        Z42ErrorType or Z42UnknownType => true, // don't cascade
        _                             => Z42Type.IsReferenceType(t),
    };

    /// L3-G2.5 refvalue: is `typeArg` a value type (for `where T: struct`)?
    private bool IsStructArg(Z42Type t) => t switch
    {
        Z42ClassType ct               => ct.IsStruct,
        Z42PrimType                   => !Z42Type.IsReferenceType(t), // int/bool/float/...
        Z42EnumType                   => true,                        // enum is a value type
        Z42GenericParamType gp        => _symbols.LookupEffectiveConstraints(gp.Name).RequiresStruct
                                         || _symbols.LookupEffectiveConstraints(gp.Name).RequiresEnum,
        Z42ErrorType or Z42UnknownType => true,
        _                             => false,
    };

    /// L3-G2.5 enum: is `typeArg` an enum type (for `where T: enum`)?
    /// - Z42EnumType: direct match.
    /// - Generic param: propagate only if T itself carries `RequiresEnum`.
    /// - Everything else (class / struct / primitive / interface / array): rejected.
    private bool IsEnumArg(Z42Type t) => t switch
    {
        Z42EnumType                   => true,
        Z42GenericParamType gp        => _symbols.LookupEffectiveConstraints(gp.Name).RequiresEnum,
        Z42ErrorType or Z42UnknownType => true, // don't cascade
        _                             => false,
    };

    /// Does `typeArg` satisfy the interface constraint `iface`?
    /// - Class type: via SymbolTable.ImplementsInterface (walks hierarchy).
    /// - Interface type: same-name match (interface extending not tracked yet — L3-G3).
    /// - Generic param: accept if one of its own constraints is this interface (propagate).
    /// - Primitive: routed via L3-G4b `PrimitiveImplementsInterface`. L3-G2.5 chain
    ///   additionally requires `iface.TypeArgs` (if present) to equal the primitive
    ///   itself — primitives only satisfy the self-referential form (int → IEquatable<int>).
    private bool TypeSatisfiesInterface(Z42Type typeArg, Z42InterfaceType iface) => typeArg switch
    {
        Z42ClassType ct          => ClassSatisfiesInterface(ct.Name, null, iface),
        Z42InstantiatedType inst => ClassSatisfiesInterface(inst.Definition.Name,
                                        BuildSubstitutionMap(inst), iface),
        Z42InterfaceType it      => InterfacesEqual(it, iface),
        Z42GenericParamType g    => GenericParamSatisfies(g, iface),
        Z42PrimType pt           => PrimitiveSatisfies(pt, iface),
        Z42ErrorType             => true, // don't cascade errors
        Z42UnknownType           => true,
        _                        => false,
    };

    /// L3-G2.5 chain: class-level interface check with TypeArg matching.
    /// Walks the base-class chain; for each declared `Z42InterfaceType` with a matching
    /// name, compares TypeArgs (after substituting instantiated class type params).
    /// When the constraint has no TypeArgs, name match is sufficient.
    private bool ClassSatisfiesInterface(
        string className,
        IReadOnlyDictionary<string, Z42Type>? classSubstitution,
        Z42InterfaceType constraintIface)
    {
        foreach (var declared in _symbols.ImplementedInterfacesByName(className, constraintIface.Name))
        {
            // No args on the constraint → name match is enough (backward compat).
            if (constraintIface.TypeArgs is null || constraintIface.TypeArgs.Count == 0)
                return true;
            // Declared side missing args → be lenient (likely imported w/o args tracking).
            if (declared.TypeArgs is null || declared.TypeArgs.Count != constraintIface.TypeArgs.Count)
                return true;
            bool allMatch = true;
            for (int i = 0; i < constraintIface.TypeArgs.Count; i++)
            {
                var declaredArg = classSubstitution is null
                    ? declared.TypeArgs[i]
                    : SubstituteTypeParams(declared.TypeArgs[i], classSubstitution);
                if (!TypeArgEquals(declaredArg, constraintIface.TypeArgs[i]))
                { allMatch = false; break; }
            }
            if (allMatch) return true;
        }
        return false;
    }

    /// Full interface equality including TypeArgs (length & element-wise).
    private static bool InterfacesEqual(Z42InterfaceType a, Z42InterfaceType b)
    {
        if (a.Name != b.Name) return false;
        if (a.TypeArgs is null && b.TypeArgs is null) return true;
        if (a.TypeArgs is null || b.TypeArgs is null) return true; // lenient
        if (a.TypeArgs.Count != b.TypeArgs.Count) return false;
        for (int i = 0; i < a.TypeArgs.Count; i++)
            if (!TypeArgEquals(a.TypeArgs[i], b.TypeArgs[i])) return false;
        return true;
    }

    /// L3-G2.5 chain: equality for TypeArg comparison. Z42 records use structural
    /// equality; for class types we use name-based equality because stubs vs fully
    /// collected class instances would otherwise mismatch (same `Num` at different
    /// collection phases has different inner dictionaries).
    private static bool TypeArgEquals(Z42Type a, Z42Type b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is Z42ClassType ac && b is Z42ClassType bc) return ac.Name == bc.Name;
        if (a is Z42PrimType ap && b is Z42PrimType bp)   return ap.Name == bp.Name;
        if (a is Z42GenericParamType ag && b is Z42GenericParamType bg) return ag.Name == bg.Name;
        if (a is Z42InstantiatedType ai && b is Z42InstantiatedType bi)
        {
            if (ai.Definition.Name != bi.Definition.Name) return false;
            if (ai.TypeArgs.Count != bi.TypeArgs.Count) return false;
            for (int i = 0; i < ai.TypeArgs.Count; i++)
                if (!TypeArgEquals(ai.TypeArgs[i], bi.TypeArgs[i])) return false;
            return true;
        }
        if (a is Z42ArrayType aa && b is Z42ArrayType ba) return TypeArgEquals(aa.Element, ba.Element);
        if (a is Z42OptionType ao && b is Z42OptionType bo) return TypeArgEquals(ao.Inner, bo.Inner);
        return a.Equals(b);
    }

    /// L3-G2.5 chain: a primitive `int` satisfies `IEquatable<int>` but not
    /// `IEquatable<string>` — when the constraint's TypeArgs are present, the sole
    /// type arg must equal the primitive itself (self-referential only).
    private bool PrimitiveSatisfies(Z42PrimType pt, Z42InterfaceType iface)
    {
        if (!PrimitiveImplementsInterface(pt.Name, iface.Name)) return false;
        if (iface.TypeArgs is not { Count: > 0 } args) return true;
        return args.All(a => a is Z42PrimType p && p.Name == pt.Name);
    }

    /// L3-G2.5 chain: generic param T satisfies `I<X>` if one of T's own interface
    /// constraints is `I<X>` (name AND args match after substitution).
    private static bool GenericParamSatisfies(Z42GenericParamType g, Z42InterfaceType iface)
    {
        if (g.InterfaceConstraints is null) return false;
        foreach (var c in g.InterfaceConstraints)
        {
            if (c.Name != iface.Name) continue;
            if (iface.TypeArgs is null || iface.TypeArgs.Count == 0) return true;
            if (c.TypeArgs is null || c.TypeArgs.Count != iface.TypeArgs.Count) continue;
            bool argsMatch = true;
            for (int i = 0; i < c.TypeArgs.Count; i++)
                if (!TypeArgEquals(c.TypeArgs[i], iface.TypeArgs[i]))
                { argsMatch = false; break; }
            if (argsMatch) return true;
        }
        return false;
    }

    /// L3-G2.5 chain: substitute type-param references inside an interface's
    /// TypeArgs using the current call-site `typeArg` map. `T: IEquatable<U>` with
    /// U=string becomes `IEquatable<string>` before the satisfies check.
    private static Z42InterfaceType SubstituteInterfaceTypeArgs(
        Z42InterfaceType iface, IReadOnlyDictionary<string, Z42Type> typeArgByName)
    {
        if (iface.TypeArgs is not { Count: > 0 } args) return iface;
        var substituted = new List<Z42Type>(args.Count);
        foreach (var a in args)
        {
            if (a is Z42GenericParamType gp && typeArgByName.TryGetValue(gp.Name, out var concrete))
                substituted.Add(concrete);
            else
                substituted.Add(a);
        }
        return new Z42InterfaceType(iface.Name, iface.Methods, substituted,
            iface.StaticMembers, iface.TypeParams);
    }

    /// 构造 Z42InterfaceType 实例的 TypeParams ↔ TypeArgs substitution map。
    /// 用于把 interface 内部 generic param 形式的 method signature substitute 为
    /// 具体类型（`IEquatable<int>.Equals(T)` → `Equals(int)`）。
    ///
    /// 返回 null 表示非泛型接口或缺少 TypeParams / TypeArgs，caller 应跳过 substitute.
    internal static IReadOnlyDictionary<string, Z42Type>?
    BuildInterfaceSubstitutionMap(Z42InterfaceType iface)
    {
        if (iface.TypeParams is null || iface.TypeArgs is null) return null;
        if (iface.TypeParams.Count != iface.TypeArgs.Count) return null;
        if (iface.TypeParams.Count == 0) return null;
        var map = new Dictionary<string, Z42Type>(StringComparer.Ordinal);
        for (int i = 0; i < iface.TypeParams.Count; i++)
            map[iface.TypeParams[i]] = iface.TypeArgs[i];
        return map;
    }

    /// L3-G4b primitive-as-struct: primitive types satisfy interfaces via stdlib
    /// `struct int : IComparable<int> { ... }` declarations (see z42.core/src/Int.z42
    /// etc.). No hardcoded table — consults the symbol-level `ClassInterfaces` registry.
    ///
    /// Size-alias primitive names (i32, i64, short, byte, ushort, uint, ulong, f32, ...) are
    /// normalized to their canonical stdlib struct name (int/long/double/float) before lookup,
    /// so `Max<i8>(...)` reuses `struct int`'s interface list.
    private bool PrimitiveImplementsInterface(string primName, string ifaceName)
    {
        string canonical = primName switch
        {
            "i8" or "i16" or "i32" or "u8" or "u16" or "u32"
            or "sbyte" or "short" or "byte" or "ushort" or "uint" => "int",
            "i64" or "u64" or "ulong"                             => "long",
            "f32"                                                  => "float",
            "f64"                                                  => "double",
            // stdlib retains legacy uppercase `class String`; map primitive `string` to it
            // until String is migrated to a `struct string` declaration.
            "string"                                               => "String",
            _                                                      => primName,
        };
        if (!_symbols.ClassInterfaces.TryGetValue(canonical, out var ifaces)) return false;
        foreach (var iface in ifaces)
            if (iface.Name == ifaceName) return true;
        return false;
    }

    /// Does `typeArg` satisfy the base-class constraint `baseClass`? (L3-G2.5)
    /// Accepts same class or any subclass; propagates through generic params that already
    /// carry a matching base-class constraint.
    private bool TypeSatisfiesClassConstraint(Z42Type typeArg, Z42ClassType baseClass) => typeArg switch
    {
        Z42ClassType ct       => ct.Name == baseClass.Name
                                 || _symbols.IsSubclassOf(ct.Name, baseClass.Name),
        Z42GenericParamType g => g.BaseClassConstraint != null
                                 && (g.BaseClassConstraint.Name == baseClass.Name
                                     || _symbols.IsSubclassOf(g.BaseClassConstraint.Name, baseClass.Name)),
        Z42ErrorType          => true,
        Z42UnknownType        => true,
        _                     => false,
    };

    // ── L3-G4a: type parameter substitution helpers ─────────────────────────

    /// Build substitution map from an instantiated generic type: { TypeParam[i] → TypeArgs[i] }.
    internal static IReadOnlyDictionary<string, Z42Type>? BuildSubstitutionMap(Z42InstantiatedType inst)
    {
        var tps = inst.Definition.TypeParams;
        if (tps is null || tps.Count == 0 || tps.Count != inst.TypeArgs.Count) return null;
        var map = new Dictionary<string, Z42Type>(tps.Count, StringComparer.Ordinal);
        for (int i = 0; i < tps.Count; i++)
            map[tps[i]] = inst.TypeArgs[i];
        return map;
    }

    /// Recursively substitute Z42GenericParamType references in `t` with their concrete
    /// types from `map`. Handles arrays, options, function types, and nested instantiated
    /// types. Returns the input unchanged when no substitution applies.
    internal static Z42Type SubstituteTypeParams(Z42Type t, IReadOnlyDictionary<string, Z42Type>? map)
    {
        if (map is null || map.Count == 0) return t;
        return t switch
        {
            Z42GenericParamType gp   => map.TryGetValue(gp.Name, out var concrete) ? concrete : gp,
            Z42ArrayType arr         => new Z42ArrayType(SubstituteTypeParams(arr.Element, map)),
            Z42OptionType opt        => new Z42OptionType(SubstituteTypeParams(opt.Inner, map)),
            Z42FuncType fn           => new Z42FuncType(
                                             fn.Params.Select(p => SubstituteTypeParams(p, map)).ToList(),
                                             SubstituteTypeParams(fn.Ret, map),
                                             fn.RequiredCount),
            Z42InstantiatedType inst => new Z42InstantiatedType(
                                             inst.Definition,
                                             inst.TypeArgs.Select(a => SubstituteTypeParams(a, map)).ToList()),
            _ => t,
        };
    }

    /// Convert IrType string (from DependencyIndex) to Z42Type.
    /// Maps IR type names like "Str", "I32", "Void" to semantic types.
    private static Z42Type IrTypeToZ42Type(string irType) => irType switch
    {
        "Str"     => Z42Type.String,
        "Bool"    => Z42Type.Bool,
        "Char"    => Z42Type.Char,
        "I8"      => Z42Type.I8,
        "I16"     => Z42Type.I16,
        "I32"     => Z42Type.Int,
        "I64"     => Z42Type.Long,
        "U8"      => Z42Type.U8,
        "U16"     => Z42Type.U16,
        "U32"     => Z42Type.U32,
        "U64"     => Z42Type.U64,
        "F32"     => Z42Type.Float,
        "F64"     => Z42Type.Double,
        "Void"    => Z42Type.Void,
        _         => Z42Type.Unknown  // Unrecognized type defaults to Unknown
    };

    private static Z42Type ElemTypeOf(Z42Type t) => t switch
    {
        Z42ArrayType  at => at.Element,
        Z42OptionType ot => ot.Inner,
        // L3-G4h step2: duck-typed `foreach` — class with `get_Item(int)` indexer
        // yields elements of the indexer's return type (with type-param substitution
        // for instantiated generics).
        Z42InstantiatedType inst when FindIndexerRet(inst.Definition, BuildSubMap(inst)) is { } rt => rt,
        Z42ClassType ct when FindIndexerRet(ct, null) is { } rt => rt,
        _                => Z42Type.Unknown
    };

    private static Z42Type? FindIndexerRet(Z42ClassType ct,
                                           IReadOnlyDictionary<string, Z42Type>? sub)
    {
        if (!ct.Methods.TryGetValue("get_Item", out var mt)) return null;
        return SubstituteTypeParams(mt.Ret, sub);
    }

    private static IReadOnlyDictionary<string, Z42Type> BuildSubMap(Z42InstantiatedType inst)
    {
        var map = new Dictionary<string, Z42Type>();
        var tps = inst.Definition.TypeParams;
        if (tps is not null)
            for (int i = 0; i < tps.Count && i < inst.TypeArgs.Count; i++)
                map[tps[i]] = inst.TypeArgs[i];
        return map;
    }
}
