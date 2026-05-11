using Z42.Core;
using Z42.IR;
using Z42.Semantics.Symbols;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

public static partial class ImportedSymbolLoader
{

    // ── Phase 2 helpers: 填充成员（in-place 修改 Phase 1 骨架的 mutable dict）──

    private static void FillClassMembersInPlace(
        ExportedClassDef                              cls,
        Z42ClassType                                  skeleton,
        IReadOnlyDictionary<string, Z42ClassType>     classes,
        IReadOnlyDictionary<string, Z42InterfaceType> interfaces,
        IReadOnlyDictionary<string, DelegateInfo>?    delegates = null)
    {
        // Cast back to Dictionary<> — BuildClassSkeleton 创建的是 mutable Dictionary.
        var fields        = (Dictionary<string, IFieldSymbol>)skeleton.Fields;
        var staticFields  = (Dictionary<string, IFieldSymbol>)skeleton.StaticFields;
        var methods       = (Dictionary<string, IMethodSymbol>)skeleton.Methods;
        var staticMethods = (Dictionary<string, IMethodSymbol>)skeleton.StaticMethods;
        var memberVis     = (Dictionary<string, Visibility>)skeleton.MemberVisibility;
        // L3 generic: propagate class's TypeParams so field/method signatures
        // containing `T` restore as Z42GenericParamType (not Z42PrimType("T")).
        var tpSet = cls.TypeParams is { Count: > 0 } tps
            ? new HashSet<string>(tps) : null;

        foreach (var f in cls.Fields)
        {
            var ft = ResolveTypeName(f.TypeName, tpSet, classes, interfaces, delegates);
            var vis = ParseVisibility(f.Visibility);
            var sym = new FieldSymbol(f.Name, skeleton, ft, f.IsStatic,
                                       default(Z42.Core.Text.Span), vis,
                                       isEvent: false, decl: null);
            if (f.IsStatic) staticFields[f.Name] = sym;
            else            fields[f.Name]       = sym;
            memberVis[f.Name] = vis;
        }

        foreach (var m in cls.Methods)
        {
            var sig = RebuildFuncType(m.Params, m.ReturnType, m.MinArgCount, tpSet,
                classes, interfaces, delegates);
            var vis = ParseVisibility(m.Visibility);
            var mods = ImportedModifiers(m);
            var syntheticDecl = SynthesizeImportedDecl(m.Name, m.Params, m.ReturnType);
            var sym = new MethodSymbol(m.Name, skeleton, sig, mods,
                                        default(Z42.Core.Text.Span), vis,
                                        decl: syntheticDecl, testAttributes: null);
            if (m.IsStatic) staticMethods[m.Name] = sym;
            else            methods[m.Name]       = sym;
            string visKey = m.Name.Contains('$') ? m.Name[..m.Name.IndexOf('$')] : m.Name;
            memberVis.TryAdd(visKey, vis);
        }
    }

    private static FunctionModifiers ImportedModifiers(ExportedMethodDef m)
    {
        var mods = FunctionModifiers.None;
        if (m.IsStatic)   mods |= FunctionModifiers.Static;
        if (m.IsVirtual)  mods |= FunctionModifiers.Virtual;
        if (m.IsAbstract) mods |= FunctionModifiers.Abstract;
        return mods;
    }

    /// spec extend-named-args-shim (2026-05-12): synthesize a minimal
    /// `FunctionDecl` from a TSIG `ExportedMethodDef` / `ExportedFunctionDef`
    /// so call sites can read `Decl.Params[i].Name` for named-arg reorder.
    /// Only `Params` is meaningful — every other field is a placeholder
    /// (Body=empty BlockStmt, Visibility=Public, no modifiers). The
    /// synthetic decl is never executed, only inspected.
    ///
    /// Default exprs are absent in TSIG (D-9): `Param.Default` is left null,
    /// and `BindArgsReordered`'s `sig` fallback emits `BoundDefault(type)`
    /// for missing optional slots (i >= sig.MinArgCount).
    internal static FunctionDecl SynthesizeImportedDecl(
        string name,
        IReadOnlyList<ExportedParamDef> exportedParams,
        string returnTypeName)
    {
        var span = default(Core.Text.Span);
        var parms = new List<Param>(exportedParams.Count);
        foreach (var ep in exportedParams)
        {
            parms.Add(new Param(
                Name: ep.Name,
                Type: new NamedType(ep.TypeName, span),
                Default: null,
                Span: span,
                Modifier: ParamModifier.None));
        }
        TypeExpr retType = returnTypeName == "void"
            ? new VoidType(span)
            : new NamedType(returnTypeName, span);
        return new FunctionDecl(
            Name:           name,
            Params:         parms,
            ReturnType:     retType,
            Body:           new BlockStmt(new List<Stmt>(), span),
            Visibility:     Visibility.Public,
            Modifiers:      FunctionModifiers.None,
            NativeIntrinsic: null,
            Span:           span);
    }

    /// 接口的 StaticMembers 字段在 Z42InterfaceType 中是 nullable，骨架时为 null。
    /// 如果方法集合包含 static members，需要"升级"骨架为带 StaticMembers 的新 record；
    /// 否则保持 in-place 修改 methods 字典。返回最终 InterfaceType（可能与 skeleton 同一引用，
    /// 也可能是替换实例，取决于是否有 static members）。
    private static Z42InterfaceType FillInterfaceMembersInPlace(
        ExportedInterfaceDef                          iface,
        Z42InterfaceType                              skeleton,
        IReadOnlyDictionary<string, Z42ClassType>     classes,
        IReadOnlyDictionary<string, Z42InterfaceType> interfaces,
        IReadOnlyDictionary<string, DelegateInfo>?    delegates = null)
    {
        // L3 primitive-as-struct: restore interface's type params so `T` in method
        // signatures (e.g. `T op_Add(T other)` in `INumber<T>`) resolves to
        // Z42GenericParamType on the consumer side rather than `Z42PrimType("T")`.
        var tpSet = iface.TypeParams is { Count: > 0 } tps
            ? new HashSet<string>(tps) : null;
        var methods       = (Dictionary<string, IMethodSymbol>)skeleton.Methods;
        Dictionary<string, Z42StaticMember>? staticMembers = null;
        foreach (var m in iface.Methods)
        {
            var sig = RebuildFuncType(m.Params, m.ReturnType, m.MinArgCount, tpSet,
                classes, interfaces, delegates);
            if (m.IsStatic)
            {
                staticMembers ??= new Dictionary<string, Z42StaticMember>();
                // L3 static abstract tier (C# 11 alignment): reconstruct Kind from
                // (IsAbstract, IsVirtual) pair exactly as exported.
                var kind = m.IsAbstract ? StaticMemberKind.Abstract
                         : m.IsVirtual  ? StaticMemberKind.Virtual
                         : StaticMemberKind.Concrete;
                staticMembers[m.Name] = new Z42StaticMember(m.Name, sig, kind);
            }
            else
            {
                var syntheticDecl = SynthesizeImportedDecl(m.Name, m.Params, m.ReturnType);
                var sym = new MethodSymbol(m.Name, skeleton, sig,
                                            ImportedModifiers(m),
                                            default(Z42.Core.Text.Span),
                                            Visibility.Public,
                                            decl: syntheticDecl, testAttributes: null);
                methods[m.Name] = sym;
            }
        }
        // 如果没 static members，骨架本身已被填充，直接返回；否则替换实例（带 StaticMembers）。
        // 替换实例时 Methods 字典引用保持不变（仍是骨架那个 Dictionary），其他类对该接口
        // 的方法引用仍然有效。
        if (staticMembers is null) return skeleton;
        return new Z42InterfaceType(iface.Name, skeleton.Methods,
            TypeArgs: skeleton.TypeArgs, StaticMembers: staticMembers,
            TypeParams: skeleton.TypeParams);
    }

}
