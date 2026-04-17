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
    public static ExportedModule Extract(SemanticModel sem, string ns)
    {
        var classes    = ExtractClasses(sem);
        var interfaces = ExtractInterfaces(sem);
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

            result.Add(new ExportedClassDef(
                name, ct.BaseClassName,
                false, false, false,
                fields, methods, []));
        }
        return result;
    }

    private static List<ExportedInterfaceDef> ExtractInterfaces(SemanticModel sem)
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
            result.Add(new ExportedInterfaceDef(name, methods));
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
            result.Add(new ExportedFuncDef(name, parms, TypeToString(ft.Ret), ft.MinArgCount));
        }
        return result;
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
        Z42PrimType pt      => pt.Name,
        Z42VoidType         => "void",
        Z42ArrayType at     => $"{TypeToString(at.Element)}[]",
        Z42OptionType ot    => $"{TypeToString(ot.Inner)}?",
        Z42ClassType ct     => ct.Name,
        Z42InterfaceType it => it.Name,
        Z42NullType         => "null",
        Z42ErrorType        => "error",
        Z42UnknownType      => "unknown",
        Z42FuncType         => "func",
        _                   => "unknown",
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
