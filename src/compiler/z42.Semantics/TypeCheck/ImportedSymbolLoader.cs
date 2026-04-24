using Z42.IR;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// <summary>
/// Reconstructs Z42Type objects from <see cref="ExportedModule"/> metadata
/// loaded from zpkg TSIG sections. These imported symbols are merged into
/// the <see cref="SymbolCollector"/> before type checking begins.
/// </summary>
public static class ImportedSymbolLoader
{
    /// <summary>
    /// Load exported modules and produce a merged SymbolTable containing
    /// all imported types filtered by the given <paramref name="usings"/>.
    /// </summary>
    public static ImportedSymbols Load(
        IReadOnlyList<ExportedModule> modules,
        IReadOnlyList<string> usings)
    {
        var allowedNs  = new HashSet<string>(usings, StringComparer.Ordinal);
        var classes    = new Dictionary<string, Z42ClassType>(StringComparer.Ordinal);
        var funcs      = new Dictionary<string, Z42FuncType>(StringComparer.Ordinal);
        var interfaces = new Dictionary<string, Z42InterfaceType>(StringComparer.Ordinal);
        var enumConsts = new Dictionary<string, long>(StringComparer.Ordinal);
        var enumTypes  = new HashSet<string>(StringComparer.Ordinal);
        var classNs    = new Dictionary<string, string>(StringComparer.Ordinal);
        // L3-G3d: raw serialized constraints — resolved to bundles later by TypeChecker
        // (bundles need Z42InterfaceType / Z42ClassType references that only exist
        // after all modules are loaded).
        var classConstraints = new Dictionary<string, List<ExportedTypeParamConstraint>>(StringComparer.Ordinal);
        var funcConstraints  = new Dictionary<string, List<ExportedTypeParamConstraint>>(StringComparer.Ordinal);
        var classInterfaces  = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var mod in modules)
        {
            if (!allowedNs.Contains(mod.Namespace)) continue;

            foreach (var cls in mod.Classes)
                if (!classes.ContainsKey(cls.Name))
                {
                    classes[cls.Name] = RebuildClassType(cls);
                    classNs[cls.Name] = mod.Namespace;
                    if (cls.TypeParamConstraints is { Count: > 0 } cc)
                        classConstraints[cls.Name] = cc;
                    // L3-G4b primitive-as-struct: preserve the class's declared interface
                    // list so the TypeChecker can answer "does stdlib struct int implement
                    // IComparable?" via data-driven lookup.
                    if (cls.Interfaces.Count > 0)
                        classInterfaces[cls.Name] = new List<string>(cls.Interfaces);
                }

            foreach (var iface in mod.Interfaces)
                if (!interfaces.ContainsKey(iface.Name))
                    interfaces[iface.Name] = RebuildInterfaceType(iface);

            foreach (var en in mod.Enums)
            {
                enumTypes.Add(en.Name);
                foreach (var m in en.Members)
                    enumConsts.TryAdd($"{en.Name}.{m.Name}", m.Value);
            }

            foreach (var fn in mod.Functions)
                if (!funcs.ContainsKey(fn.Name))
                {
                    funcs[fn.Name] = RebuildFuncType(fn.Params, fn.ReturnType, fn.MinArgCount);
                    if (fn.TypeParamConstraints is { Count: > 0 } fc)
                        funcConstraints[fn.Name] = fc;
                }
        }

        return new ImportedSymbols(classes, funcs, interfaces, enumConsts, enumTypes, classNs,
            classConstraints, funcConstraints, classInterfaces);
    }

    private static Z42ClassType RebuildClassType(ExportedClassDef cls)
    {
        var fields        = new Dictionary<string, Z42Type>();
        var staticFields  = new Dictionary<string, Z42Type>();
        var methods       = new Dictionary<string, Z42FuncType>();
        var staticMethods = new Dictionary<string, Z42FuncType>();
        var memberVis     = new Dictionary<string, Visibility>();
        // L3 generic: propagate class's TypeParams so field/method signatures
        // containing `T` restore as Z42GenericParamType (not Z42PrimType("T")).
        // Previously TypeToString emitted "unknown" for generic params, so this
        // wasn't needed; fixing that serialization makes this round-trip matter.
        var tpSet = cls.TypeParams is { Count: > 0 } tps
            ? new HashSet<string>(tps) : null;

        foreach (var f in cls.Fields)
        {
            var ft = ResolveTypeName(f.TypeName, tpSet);
            if (f.IsStatic) staticFields[f.Name] = ft;
            else            fields[f.Name]        = ft;
            memberVis[f.Name] = ParseVisibility(f.Visibility);
        }

        foreach (var m in cls.Methods)
        {
            var sig = RebuildFuncType(m.Params, m.ReturnType, m.MinArgCount, tpSet);
            if (m.IsStatic) staticMethods[m.Name] = sig;
            else            methods[m.Name]        = sig;
            string visKey = m.Name.Contains('$') ? m.Name[..m.Name.IndexOf('$')] : m.Name;
            memberVis.TryAdd(visKey, ParseVisibility(m.Visibility));
        }

        // L3-G4d: preserve TypeParams so imported generic classes remain generic
        // (e.g. new Std.Collections.Stack<int>() works on consumer side).
        IReadOnlyList<string>? typeParams = cls.TypeParams is { Count: > 0 }
            ? cls.TypeParams.AsReadOnly() : null;
        return new Z42ClassType(cls.Name, fields, methods, staticFields, staticMethods,
            memberVis, cls.BaseClass, typeParams);
    }

    private static Z42InterfaceType RebuildInterfaceType(ExportedInterfaceDef iface)
    {
        // L3 primitive-as-struct: restore interface's type params so `T` in method
        // signatures (e.g. `T op_Add(T other)` in `INumber<T>`) resolves to
        // Z42GenericParamType on the consumer side rather than `Z42PrimType("T")`.
        var tpSet = iface.TypeParams is { Count: > 0 } tps
            ? new HashSet<string>(tps) : null;
        var methods       = new Dictionary<string, Z42FuncType>();
        var staticMembers = new Dictionary<string, Z42StaticMember>();
        foreach (var m in iface.Methods)
        {
            var sig = RebuildFuncType(m.Params, m.ReturnType, m.MinArgCount, tpSet);
            if (m.IsStatic)
            {
                // L3 static abstract tier (C# 11 alignment): reconstruct Kind from
                // (IsAbstract, IsVirtual) pair exactly as exported.
                var kind = m.IsAbstract ? StaticMemberKind.Abstract
                         : m.IsVirtual  ? StaticMemberKind.Virtual
                         : StaticMemberKind.Concrete;
                staticMembers[m.Name] = new Z42StaticMember(m.Name, sig, kind);
            }
            else
            {
                methods[m.Name] = sig;
            }
        }
        return new Z42InterfaceType(iface.Name, methods,
            StaticMembers: staticMembers.Count > 0 ? staticMembers : null);
    }

    private static Z42FuncType RebuildFuncType(
        List<ExportedParamDef> parms, string retType, int minArgCount,
        HashSet<string>? genericParams = null)
    {
        var paramTypes = parms.Select(p => ResolveTypeName(p.TypeName, genericParams)).ToList();
        return new Z42FuncType(paramTypes, ResolveTypeName(retType, genericParams),
            minArgCount == paramTypes.Count ? -1 : minArgCount);
    }

    /// Resolve a type name string (as serialized in TSIG) back to a Z42Type.
    internal static Z42Type ResolveTypeName(string name, HashSet<string>? genericParams = null)
    {
        if (name.EndsWith("[]"))
            return new Z42ArrayType(ResolveTypeName(name[..^2], genericParams));
        if (name.EndsWith("?"))
            return new Z42OptionType(ResolveTypeName(name[..^1], genericParams));
        if (genericParams != null && genericParams.Contains(name))
            return new Z42GenericParamType(name);

        return name switch
        {
            "int"    or "i32" => Z42Type.Int,
            "long"   or "i64" => Z42Type.Long,
            "float"  or "f32" => Z42Type.Float,
            "double" or "f64" => Z42Type.Double,
            "bool"            => Z42Type.Bool,
            "string"          => Z42Type.String,
            "char"            => Z42Type.Char,
            "object"          => Z42Type.Object,
            "void"            => Z42Type.Void,
            "null"            => Z42Type.Null,
            "i8"              => Z42Type.I8,
            "i16"             => Z42Type.I16,
            "u8"              => Z42Type.U8,
            "u16"             => Z42Type.U16,
            "u32"             => Z42Type.U32,
            "u64"             => Z42Type.U64,
            "unknown"         => Z42Type.Unknown,
            "error"           => Z42Type.Error,
            // Unknown type name — treat as a class reference (will be resolved
            // when the class is also imported, or remain as a named prim type).
            _ => new Z42PrimType(name),
        };
    }

    private static Visibility ParseVisibility(string vis) => vis switch
    {
        "public"    => Visibility.Public,
        "private"   => Visibility.Private,
        "protected" => Visibility.Protected,
        "internal"  => Visibility.Internal,
        _           => Visibility.Public,
    };
}

/// Imported symbols from dependency zpkg TSIG sections, ready to be merged
/// into the SymbolCollector.
public sealed record ImportedSymbols(
    Dictionary<string, Z42ClassType>     Classes,
    Dictionary<string, Z42FuncType>      Functions,
    Dictionary<string, Z42InterfaceType> Interfaces,
    Dictionary<string, long>             EnumConstants,
    HashSet<string>                      EnumTypes,
    /// Maps short class name (e.g. "Console") to its original namespace (e.g. "Std.IO").
    /// Used by IrGen to qualify imported class names with the correct dependency namespace
    /// instead of the local file's namespace.
    Dictionary<string, string>           ClassNamespaces,
    /// L3-G3d: raw serialized `where` constraints keyed by decl short name.
    /// Consumer TypeChecker resolves these to `GenericConstraintBundle` after all
    /// imported interfaces / classes are available, then merges into its constraint maps.
    Dictionary<string, List<ExportedTypeParamConstraint>>? ClassConstraints = null,
    Dictionary<string, List<ExportedTypeParamConstraint>>? FuncConstraints  = null,
    /// L3-G4b primitive-as-struct: imported class → declared interface list
    /// (by short name). Enables data-driven `PrimitiveImplementsInterface`
    /// to work when stdlib `struct int : IComparable<int>` is loaded from a zpkg.
    Dictionary<string, List<string>>?    ClassInterfaces = null);
