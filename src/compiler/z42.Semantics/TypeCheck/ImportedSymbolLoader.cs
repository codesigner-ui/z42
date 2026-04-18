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

        foreach (var mod in modules)
        {
            if (!allowedNs.Contains(mod.Namespace)) continue;

            foreach (var cls in mod.Classes)
                if (!classes.ContainsKey(cls.Name))
                {
                    classes[cls.Name] = RebuildClassType(cls);
                    classNs[cls.Name] = mod.Namespace;
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
                    funcs[fn.Name] = RebuildFuncType(fn.Params, fn.ReturnType, fn.MinArgCount);
        }

        return new ImportedSymbols(classes, funcs, interfaces, enumConsts, enumTypes, classNs);
    }

    private static Z42ClassType RebuildClassType(ExportedClassDef cls)
    {
        var fields        = new Dictionary<string, Z42Type>();
        var staticFields  = new Dictionary<string, Z42Type>();
        var methods       = new Dictionary<string, Z42FuncType>();
        var staticMethods = new Dictionary<string, Z42FuncType>();
        var memberVis     = new Dictionary<string, Visibility>();

        foreach (var f in cls.Fields)
        {
            var ft = ResolveTypeName(f.TypeName);
            if (f.IsStatic) staticFields[f.Name] = ft;
            else            fields[f.Name]        = ft;
            memberVis[f.Name] = ParseVisibility(f.Visibility);
        }

        foreach (var m in cls.Methods)
        {
            var sig = RebuildFuncType(m.Params, m.ReturnType, m.MinArgCount);
            if (m.IsStatic) staticMethods[m.Name] = sig;
            else            methods[m.Name]        = sig;
            string visKey = m.Name.Contains('$') ? m.Name[..m.Name.IndexOf('$')] : m.Name;
            memberVis.TryAdd(visKey, ParseVisibility(m.Visibility));
        }

        return new Z42ClassType(cls.Name, fields, methods, staticFields, staticMethods,
            memberVis, cls.BaseClass);
    }

    private static Z42InterfaceType RebuildInterfaceType(ExportedInterfaceDef iface)
    {
        var methods = new Dictionary<string, Z42FuncType>();
        foreach (var m in iface.Methods)
            methods[m.Name] = RebuildFuncType(m.Params, m.ReturnType, m.MinArgCount);
        return new Z42InterfaceType(iface.Name, methods);
    }

    private static Z42FuncType RebuildFuncType(
        List<ExportedParamDef> parms, string retType, int minArgCount)
    {
        var paramTypes = parms.Select(p => ResolveTypeName(p.TypeName)).ToList();
        return new Z42FuncType(paramTypes, ResolveTypeName(retType),
            minArgCount == paramTypes.Count ? -1 : minArgCount);
    }

    /// Resolve a type name string (as serialized in TSIG) back to a Z42Type.
    internal static Z42Type ResolveTypeName(string name)
    {
        if (name.EndsWith("[]"))
            return new Z42ArrayType(ResolveTypeName(name[..^2]));
        if (name.EndsWith("?"))
            return new Z42OptionType(ResolveTypeName(name[..^1]));

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
    Dictionary<string, string>           ClassNamespaces);
