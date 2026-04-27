using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Syntax.Parser;
using Z42.IR;

namespace Z42.Semantics.TypeCheck;

/// `where`-clause resolution (Pass 0.5, decl-site) + imported constraint rehydration
/// — part of the TypeChecker partial class.
///
/// Pairs with `TypeChecker.Generics.cs` (call-site validation + substitution helpers).
public sealed partial class TypeChecker
{
    /// L3-G3d: convert imported `ExportedTypeParamConstraint` lists into
    /// `GenericConstraintBundle` maps and merge into `_classConstraints` /
    /// `_funcConstraints`. Local constraints (already resolved in Pass 0.5) win.
    private void MergeImportedConstraints()
    {
        if (_imported is null) return;
        if (_imported.ClassConstraints is { } cc)
            foreach (var (declName, raw) in cc)
                if (!_classConstraints.ContainsKey(declName))
                    _classConstraints[declName] = RehydrateConstraints(raw);
        if (_imported.FuncConstraints is { } fc)
            foreach (var (declName, raw) in fc)
                if (!_funcConstraints.ContainsKey(declName))
                    _funcConstraints[declName] = RehydrateConstraints(raw);
    }

    private IReadOnlyDictionary<string, GenericConstraintBundle> RehydrateConstraints(
        List<ExportedTypeParamConstraint> raw)
    {
        var result = new Dictionary<string, GenericConstraintBundle>(raw.Count);
        foreach (var c in raw)
        {
            var ifaces = new List<Z42InterfaceType>(c.Interfaces.Count);
            foreach (var iname in c.Interfaces)
                if (_symbols.Interfaces.TryGetValue(iname, out var it))
                    ifaces.Add(it);
            Z42ClassType? baseCls = c.BaseClass != null
                && _symbols.Classes.TryGetValue(c.BaseClass, out var bc) ? bc : null;
            result[c.TypeParam] = new GenericConstraintBundle(
                baseCls, ifaces,
                c.RequiresClass, c.RequiresStruct, c.TypeParamRef,
                c.RequiresConstructor,
                c.RequiresEnum);
        }
        return result;
    }

    /// Pass 0.5: resolve every `where` clause in the CU into cached constraint maps
    /// consulted by body binding (`PushTypeParams`) and call-site validation.
    private void ResolveAllWhereConstraints(CompilationUnit cu)
    {
        foreach (var fn in cu.Functions)
        {
            if (fn.TypeParams == null) continue;
            var map = ResolveWhereConstraints(fn.Where, fn.TypeParams, fn.Span);
            if (map != null) _funcConstraints[fn.Name] = map;
        }
        foreach (var cls in cu.Classes)
        {
            if (cls.TypeParams != null)
            {
                var map = ResolveWhereConstraints(cls.Where, cls.TypeParams, cls.Span);
                if (map != null) _classConstraints[cls.Name] = map;
            }
            foreach (var m in cls.Methods)
            {
                if (m.TypeParams == null) continue;
                var map = ResolveWhereConstraints(m.Where, m.TypeParams, m.Span);
                if (map != null) _funcConstraints[$"{cls.Name}.{m.Name}"] = map;
            }
        }
    }

    /// Resolve a `where T: BaseClass + I + J, K: I2` clause into a map
    /// `TypeParam → GenericConstraintBundle`. Type expressions in constraints see T as a
    /// generic param via a transient type-param scope (so `where T: IComparable<T>` works
    /// self-referentially).
    /// Reports diagnostics for: unknown type params, invalid constraints (not class/interface),
    /// multiple base classes, or a base class appearing after an interface in the `+` list.
    private IReadOnlyDictionary<string, GenericConstraintBundle>? ResolveWhereConstraints(
        WhereClause? where, IReadOnlyList<string> declaredTypeParams, Span declSpan)
    {
        if (where == null || where.Constraints.Count == 0) return null;

        _symbols.PushTypeParams(declaredTypeParams); // transient, no constraints yet
        try
        {
            var result = new Dictionary<string, GenericConstraintBundle>();
            foreach (var entry in where.Constraints)
            {
                if (!declaredTypeParams.Contains(entry.TypeParam))
                {
                    _diags.Error(DiagnosticCodes.UndefinedSymbol,
                        $"`where` refers to unknown type parameter `{entry.TypeParam}`", entry.Span);
                    continue;
                }
                Z42ClassType? baseClass = null;
                var ifaces = new List<Z42InterfaceType>();
                string? typeParamConstraint = null;
                foreach (var tx in entry.Constraints)
                {
                    // L3-G2.5 bare-typeparam: NamedType matching another active type param
                    // is recorded as a subtype constraint (resolved before class/interface fallback).
                    if (tx is NamedType nt
                        && declaredTypeParams.Contains(nt.Name)
                        && nt.Name != entry.TypeParam) // self-reference handled elsewhere (L3-G2 IComparable<T>)
                    {
                        if (typeParamConstraint != null)
                            _diags.Error(DiagnosticCodes.TypeMismatch,
                                $"generic parameter `{entry.TypeParam}` cannot have multiple type-param constraints",
                                tx.Span);
                        else
                            typeParamConstraint = nt.Name;
                        continue;
                    }
                    var resolved = _symbols.ResolveType(tx);
                    switch (resolved)
                    {
                        case Z42ClassType cc when baseClass != null:
                            _diags.Error(DiagnosticCodes.TypeMismatch,
                                $"generic parameter `{entry.TypeParam}` cannot have multiple class constraints",
                                tx.Span);
                            break;
                        case Z42ClassType cc when ifaces.Count > 0:
                            _diags.Error(DiagnosticCodes.TypeMismatch,
                                $"class constraint `{cc.Name}` must appear first in constraint list for `{entry.TypeParam}`",
                                tx.Span);
                            baseClass = cc; // still record to avoid cascading errors
                            break;
                        case Z42ClassType cc:
                            baseClass = cc;
                            break;
                        case Z42InterfaceType iface:
                            ifaces.Add(iface);
                            break;
                        default:
                            _diags.Error(DiagnosticCodes.TypeMismatch,
                                $"constraint on `{entry.TypeParam}` must be a class or interface, got `{resolved}`",
                                tx.Span);
                            break;
                    }
                }
                // L3-G2.5 refvalue: translate class/struct flags and enforce mutual exclusion.
                bool reqClass  = entry.Kinds.HasFlag(GenericConstraintKind.Class);
                bool reqStruct = entry.Kinds.HasFlag(GenericConstraintKind.Struct);
                bool reqCtor   = entry.Kinds.HasFlag(GenericConstraintKind.Constructor);
                bool reqEnum   = entry.Kinds.HasFlag(GenericConstraintKind.Enum);
                if (reqClass && reqStruct)
                {
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"generic parameter `{entry.TypeParam}` cannot be both `class` and `struct`",
                        entry.Span);
                    reqClass = reqStruct = false; // don't cascade
                }
                // L3-G2.5 enum: mutually exclusive with `class` (enum is value type).
                // `enum` + `struct` is allowed but redundant (enum already implies value type).
                if (reqEnum && reqClass)
                {
                    _diags.Error(DiagnosticCodes.TypeMismatch,
                        $"generic parameter `{entry.TypeParam}` cannot be both `enum` and `class`",
                        entry.Span);
                    reqEnum = reqClass = false;
                }
                if (baseClass != null || ifaces.Count > 0 || reqClass || reqStruct
                    || typeParamConstraint != null || reqCtor || reqEnum)
                    result[entry.TypeParam] = new GenericConstraintBundle(
                        baseClass, ifaces, reqClass, reqStruct, typeParamConstraint, reqCtor, reqEnum);
            }
            return result.Count > 0 ? result : null;
        }
        finally { _symbols.PopTypeParams(); }
    }
}
