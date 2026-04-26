using Z42.IR;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// Extracts <see cref="ExportedModule"/> from a <see cref="SemanticModel"/>
/// for serialization into the zpkg TSIG section.
/// </summary>
public static class ExportedTypeExtractor
{
    /// Build an ExportedModule from the SemanticModel of a compiled source file.
    public static ExportedModule Extract(SemanticModel sem, string ns, CompilationUnit? cu = null)
    {
        var classes    = ExtractClasses(sem);
        var interfaces = ExtractInterfaces(sem, cu);
        var enums      = ExtractEnums(sem);
        var functions  = ExtractFunctions(sem);
        return new ExportedModule(ns, classes, interfaces, enums, functions);
    }

    private static List<ExportedClassDef> ExtractClasses(SemanticModel sem)
    {
        var result = new List<ExportedClassDef>();
        foreach (var (name, ct) in sem.Classes)
        {
            if (name == "Object") continue; // skip synthetic Object stub
            // L3-G4g: do NOT re-export imported classes. Previously sem.Classes contained
            // both local + imported (SymbolCollector merges imports into `_classes`), so
            // TSIG picked up e.g. Std.Collections.HashMap when compiling Std.Text. The
            // owning consumer of that TSIG then mis-routed `new HashMap<...>()` to Std.Text.
            if (sem.ImportedClassNames.Contains(name)) continue;

            var fields = new List<ExportedFieldDef>();
            foreach (var (fn, ft) in ct.Fields)
            {
                var vis = ct.MemberVisibility.TryGetValue(fn, out var v)
                    ? VisToString(v) : "public";
                fields.Add(new ExportedFieldDef(fn, TypeToString(ft), vis, false));
            }
            foreach (var (fn, ft) in ct.StaticFields)
            {
                var vis = ct.MemberVisibility.TryGetValue(fn, out var v)
                    ? VisToString(v) : "public";
                fields.Add(new ExportedFieldDef(fn, TypeToString(ft), vis, true));
            }

            var methods = new List<ExportedMethodDef>();
            foreach (var (mn, mt) in ct.Methods)
                methods.Add(FuncToMethod(mn, mt, false, ct.MemberVisibility));
            foreach (var (mn, mt) in ct.StaticMethods)
                methods.Add(FuncToMethod(mn, mt, true, ct.MemberVisibility));

            // L3-G4d: propagate generic type parameters so consumers can
            // instantiate imported generic classes with type arguments.
            var typeParams = ct.TypeParams?.ToList();
            // L3-G3d: propagate `where` bundles so consumer TypeChecker can
            // validate type args at call sites without relying on VM loader fallback.
            var constraints = ExtractTypeParamConstraints(
                sem.ClassConstraints.GetValueOrDefault(name));
            // L3-G4b primitive-as-struct: export the class's declared interface list
            // so consumer TypeChecker can answer "does int satisfy IComparable?" via
            // data-driven lookup (replaces hardcoded PrimitiveImplementsInterface).
            var ifaceNames = sem.ClassInterfaces.TryGetValue(name, out var ifaceList)
                ? ifaceList.Select(i => i.Name).ToList()
                : new List<string>();
            result.Add(new ExportedClassDef(
                name, ct.BaseClassName,
                false, false, false,
                fields, methods, ifaceNames, typeParams, constraints));
        }
        return result;
    }

    private static List<ExportedInterfaceDef> ExtractInterfaces(SemanticModel sem, CompilationUnit? cu)
    {
        var result = new List<ExportedInterfaceDef>();
        foreach (var (name, it) in sem.Interfaces)
        {
            var methods = new List<ExportedMethodDef>();
            foreach (var (mn, mt) in it.Methods)
            {
                var parms = mt.Params.Select((p, i) =>
                    new ExportedParamDef($"p{i}", TypeToString(p))).ToList();
                methods.Add(new ExportedMethodDef(
                    mn, parms, TypeToString(mt.Ret),
                    "public", false, true, true, mt.MinArgCount));
            }
            // L3 static abstract interface members (C# 11 alignment): export
            // three-tier static members so the consumer can answer
            // "does class C static-override INumber.op_Add?" via TSIG.
            if (it.StaticMembers is { } staticMap)
            {
                foreach (var (mn, member) in staticMap)
                {
                    var mt = member.Signature;
                    var parms = mt.Params.Select((p, i) =>
                        new ExportedParamDef($"p{i}", TypeToString(p))).ToList();
                    bool isAbstract = member.Kind == StaticMemberKind.Abstract;
                    bool isVirtual  = member.Kind == StaticMemberKind.Virtual;
                    methods.Add(new ExportedMethodDef(
                        mn, parms, TypeToString(mt.Ret),
                        "public", true, isVirtual, isAbstract, mt.MinArgCount));
                }
            }
            // Preserve interface's TypeParams so consumer can restore `T` in method
            // signatures as Z42GenericParamType (required for interfaces like
            // `INumber<T> { T op_Add(T other); }` where T appears in return position),
            // AND so generic interface dispatch (`IEquatable<int>.Equals(T)` →
            // `Equals(int)`) can substitute via TypeParams ↔ TypeArgs map.
            //
            // Source priority: `it.TypeParams` (Z42InterfaceType field, set by
            // SymbolCollector.CollectInterfaces from local InterfaceDecl, or by
            // ImportedSymbolLoader.BuildInterfaceSkeleton from imported TSIG).
            // Fallback to cu lookup for legacy paths where it.TypeParams isn't set.
            var tps = it.TypeParams?.ToList()
                   ?? cu?.Interfaces.FirstOrDefault(i => i.Name == name)?.TypeParams?.ToList();
            result.Add(new ExportedInterfaceDef(name, methods, tps));
        }
        return result;
    }

    private static List<ExportedEnumDef> ExtractEnums(SemanticModel sem)
    {
        // Group enum constants by enum type name (prefix before '.')
        var enumGroups = new Dictionary<string, List<ExportedEnumMember>>();
        foreach (var (key, val) in sem.EnumConstants)
        {
            int dot = key.IndexOf('.');
            if (dot < 0) continue;
            string enumName   = key[..dot];
            string memberName = key[(dot + 1)..];
            if (!enumGroups.TryGetValue(enumName, out var members))
            {
                members = [];
                enumGroups[enumName] = members;
            }
            members.Add(new ExportedEnumMember(memberName, val));
        }
        return enumGroups.Select(kv => new ExportedEnumDef(kv.Key, kv.Value)).ToList();
    }

    private static List<ExportedFuncDef> ExtractFunctions(SemanticModel sem)
    {
        var result = new List<ExportedFuncDef>();
        foreach (var (name, ft) in sem.Funcs)
        {
            var parms = ft.Params.Select((p, i) =>
                new ExportedParamDef($"p{i}", TypeToString(p))).ToList();
            // L3-G3d: propagate generic type params and where-clause constraints
            // for imported free functions (stdlib-defined generic helpers).
            var constraints = ExtractTypeParamConstraints(
                sem.FuncConstraints.GetValueOrDefault(name));
            List<string>? typeParams = null;
            if (sem.FuncConstraints.TryGetValue(name, out var cMap))
                typeParams = cMap.Keys.ToList();
            result.Add(new ExportedFuncDef(name, parms, TypeToString(ft.Ret), ft.MinArgCount,
                typeParams, constraints));
        }
        return result;
    }

    /// Convert a (possibly-null) constraint map to the serializable list form.
    /// L3-G3d: interfaces / base class stored by name only; inner type-args of
    /// generic interfaces (e.g. `IEquatable<T>`) are recoverable via type-param context.
    private static List<ExportedTypeParamConstraint>? ExtractTypeParamConstraints(
        IReadOnlyDictionary<string, GenericConstraintBundle>? map)
    {
        if (map is null || map.Count == 0) return null;
        var list = new List<ExportedTypeParamConstraint>(map.Count);
        foreach (var (tp, bundle) in map)
        {
            if (bundle.IsEmpty) continue;
            list.Add(new ExportedTypeParamConstraint(
                tp,
                bundle.Interfaces.Select(i => i.Name).ToList(),
                bundle.BaseClass?.Name,
                bundle.TypeParamConstraint,
                bundle.RequiresClass,
                bundle.RequiresStruct,
                bundle.RequiresConstructor,
                bundle.RequiresEnum));
        }
        return list.Count == 0 ? null : list;
    }

    private static ExportedMethodDef FuncToMethod(
        string name, Z42FuncType ft, bool isStatic,
        IReadOnlyDictionary<string, Visibility> memberVis)
    {
        // Strip arity suffix for visibility lookup: "Method$2" → "Method"
        string visKey = name.Contains('$') ? name[..name.IndexOf('$')] : name;
        var vis = memberVis.TryGetValue(visKey, out var v) ? VisToString(v) : "public";
        var parms = ft.Params.Select((p, i) =>
            new ExportedParamDef($"p{i}", TypeToString(p))).ToList();
        return new ExportedMethodDef(
            name, parms, TypeToString(ft.Ret),
            vis, isStatic, false, false, ft.MinArgCount);
    }

    /// Convert a Z42Type to its string representation for TSIG serialization.
    internal static string TypeToString(Z42Type type) => type switch
    {
        Z42PrimType pt         => pt.Name,
        // L3 generic: serialize the param name (e.g. "T"); consumer restores as
        // Z42GenericParamType when tpSet matches — critical for `INumber<T>`'s
        // `static abstract T op_Add(T a, T b)` round-trip.
        Z42GenericParamType gp => gp.Name,
        Z42VoidType            => "void",
        Z42ArrayType at        => $"{TypeToString(at.Element)}[]",
        Z42OptionType ot       => $"{TypeToString(ot.Inner)}?",
        Z42ClassType ct        => ct.Name,
        Z42InterfaceType it    => it.Name,
        Z42NullType            => "null",
        Z42ErrorType           => "error",
        Z42UnknownType         => "unknown",
        Z42FuncType            => "func",
        _                      => "unknown",
    };

    private static string VisToString(Visibility vis) => vis switch
    {
        Visibility.Public    => "public",
        Visibility.Private   => "private",
        Visibility.Protected => "protected",
        Visibility.Internal  => "internal",
        _                    => "public",
    };
}
