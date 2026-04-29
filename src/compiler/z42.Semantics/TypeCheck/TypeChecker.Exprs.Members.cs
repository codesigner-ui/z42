using Z42.Core.Text;
using Z42.Core.Diagnostics;
using Z42.Semantics.Bound;
using Z42.Syntax.Parser;

namespace Z42.Semantics.TypeCheck;

/// Member expression binding (non-call) + constructor name resolution
/// — part of the TypeChecker partial class.
///
/// Pairs with `TypeChecker.Exprs.cs` (dispatcher + simple bindings) and
/// `TypeChecker.Exprs.Operators.cs` (assignment + binary).
public sealed partial class TypeChecker
{
    // ── Member expression (non-call) ──────────────────────────────────────────

    private BoundExpr BindMemberExpr(MemberExpr m, TypeEnv env)
    {
        var target = BindExpr(m.Target, env);
        // L3-G4a: substitute Z42InstantiatedType fields/methods with concrete TypeArgs.
        if (target.Type is Z42InstantiatedType inst)
        {
            var subMap = BuildSubstitutionMap(inst);
            var def    = inst.Definition;
            bool insideClass = env.CurrentClass == def.Name;
            if (def.Fields.TryGetValue(m.Member, out var ft))
            {
                if (!insideClass
                    && def.MemberVisibility.TryGetValue(m.Member, out var fv)
                    && fv == Visibility.Private)
                    _diags.Error(DiagnosticCodes.AccessViolation,
                        $"field `{m.Member}` is private to `{def.Name}`", m.Span);
                return new BoundMember(target, m.Member, SubstituteTypeParams(ft, subMap), m.Span);
            }
            // Auto-property getter dispatch on instantiated generic class
            if (def.Methods.TryGetValue($"get_{m.Member}", out var instGetter)
                && instGetter.Params.Count == 0)
            {
                if (!insideClass
                    && def.MemberVisibility.TryGetValue($"get_{m.Member}", out var pv)
                    && pv == Visibility.Private)
                    _diags.Error(DiagnosticCodes.AccessViolation,
                        $"property `{m.Member}` getter is private to `{def.Name}`", m.Span);
                var subRet = SubstituteTypeParams(instGetter.Ret, subMap);
                return new BoundCall(BoundCallKind.Virtual, target, def.Name,
                    $"get_{m.Member}", null, new List<BoundExpr>(), subRet, m.Span);
            }
            if (def.Methods.TryGetValue(m.Member, out var mt))
            {
                if (!insideClass
                    && def.MemberVisibility.TryGetValue(m.Member, out var mv)
                    && mv == Visibility.Private)
                    _diags.Error(DiagnosticCodes.AccessViolation,
                        $"method `{m.Member}` is private to `{def.Name}`", m.Span);
                return new BoundMember(target, m.Member, (Z42FuncType)SubstituteTypeParams(mt, subMap), m.Span);
            }
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"type `{inst}` has no member `{m.Member}`", m.Span);
            return new BoundError($"no member `{m.Member}`", Z42Type.Error, m.Span);
        }
        if (target.Type is Z42ClassType ct)
        {
            bool insideClass = env.CurrentClass == ct.Name;
            if (ct.Fields.TryGetValue(m.Member, out var ft))
            {
                if (!insideClass
                    && ct.MemberVisibility.TryGetValue(m.Member, out var fv)
                    && fv == Visibility.Private)
                    _diags.Error(DiagnosticCodes.AccessViolation,
                        $"field `{m.Member}` is private to `{ct.Name}`", m.Span);
                return new BoundMember(target, m.Member, ft, m.Span);
            }
            // Auto-property getter dispatch: 字段缺失但 method `get_<Member>` 存在
            // → 转 BoundCall(Virtual, target, get_<Member>, []) 调用 getter。
            if (ct.Methods.TryGetValue($"get_{m.Member}", out var getter)
                && getter.Params.Count == 0)
            {
                if (!insideClass
                    && ct.MemberVisibility.TryGetValue($"get_{m.Member}", out var pv)
                    && pv == Visibility.Private)
                    _diags.Error(DiagnosticCodes.AccessViolation,
                        $"property `{m.Member}` getter is private to `{ct.Name}`", m.Span);
                return new BoundCall(BoundCallKind.Virtual, target, ct.Name,
                    $"get_{m.Member}", null, new List<BoundExpr>(), getter.Ret, m.Span);
            }
            if (ct.Methods.TryGetValue(m.Member, out var mt))
            {
                if (!insideClass
                    && ct.MemberVisibility.TryGetValue(m.Member, out var mv)
                    && mv == Visibility.Private)
                    _diags.Error(DiagnosticCodes.AccessViolation,
                        $"method `{m.Member}` is private to `{ct.Name}`", m.Span);
                return new BoundMember(target, m.Member, mt, m.Span);
            }
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"type `{ct.Name}` has no member `{m.Member}`", m.Span);
            return new BoundError($"no member `{m.Member}`", Z42Type.Error, m.Span);
        }
        if (target.Type is Z42InterfaceType ifaceType)
        {
            // Auto-property getter dispatch on interface receiver — substitute
            // generic param return type per ifaceType TypeArgs (e.g. IEnumerator<int>.Current → int).
            if (ifaceType.Methods.TryGetValue($"get_{m.Member}", out var ifaceGetter)
                && ifaceGetter.Params.Count == 0)
            {
                var subMap = BuildInterfaceSubstitutionMap(ifaceType);
                var subRet = subMap is null ? ifaceGetter.Ret
                                            : SubstituteTypeParams(ifaceGetter.Ret, subMap);
                return new BoundCall(BoundCallKind.Virtual, target, ifaceType.Name,
                    $"get_{m.Member}", null, new List<BoundExpr>(), subRet, m.Span);
            }
            if (ifaceType.Methods.TryGetValue(m.Member, out var ifmt))
                return new BoundMember(target, m.Member, ifmt, m.Span);
        }
        // L3-G2 / G2.5: type parameter member access — resolve via base class first, then constraint interfaces.
        if (target.Type is Z42GenericParamType gp)
        {
            // Field-stored T may have null constraints; consult active where-clause scope.
            // L3-G2.5 bare-typeparam: LookupEffectiveConstraints merges one hop through U: T.
            var bundle = (gp.BaseClassConstraint, gp.InterfaceConstraints) switch
            {
                (null, null) => _symbols.LookupEffectiveConstraints(gp.Name),
                _            => _symbols.LookupEffectiveConstraints(gp.Name) is { IsEmpty: false } scoped
                                ? scoped  // active scope wins when it exists (may have TypeParamConstraint hop)
                                : new GenericConstraintBundle(gp.BaseClassConstraint,
                                      gp.InterfaceConstraints ?? []),
            };
            if (bundle.BaseClass is { } bc)
            {
                if (bc.Fields.TryGetValue(m.Member, out var ft))
                    return new BoundMember(target, m.Member, ft, m.Span);
                if (bc.Methods.TryGetValue(m.Member, out var mt))
                    return new BoundMember(target, m.Member, mt, m.Span);
            }
            foreach (var iface in bundle.Interfaces)
                if (iface.Methods.TryGetValue(m.Member, out var cfmt))
                    return new BoundMember(target, m.Member, cfmt, m.Span);

            _diags.Error(DiagnosticCodes.TypeMismatch,
                bundle.IsEmpty
                    ? $"unconstrained type parameter `{gp.Name}` has no member `{m.Member}`; add a `where {gp.Name}: ...` clause"
                    : $"type parameter `{gp.Name}` has no member `{m.Member}` in its constraints",
                m.Span);
            return new BoundError($"no member `{m.Member}` on `{gp.Name}`", Z42Type.Error, m.Span);
        }
        if (m.Member is "Length" && (target.Type is Z42ArrayType || target.Type == Z42Type.String))
            return new BoundMember(target, m.Member, Z42Type.Int, m.Span);
        if (m.Member == "Count")
            return new BoundMember(target, m.Member, Z42Type.Int, m.Span);
        // Spec C5 — PinnedView exposes only `ptr` and `len`, both `long`.
        if (target.Type == Z42Type.PinnedView)
        {
            if (m.Member is "ptr" or "len")
                return new BoundMember(target, m.Member, Z42Type.Long, m.Span);
            _diags.Error(DiagnosticCodes.UndefinedSymbol,
                $"`PinnedView` has no field `{m.Member}` (only `ptr` / `len`)", m.Span);
            return new BoundError($"no member `{m.Member}` on PinnedView", Z42Type.Error, m.Span);
        }
        return new BoundMember(target, m.Member, Z42Type.Unknown, m.Span);
    }

    /// Overload-resolve a constructor for `new ClassName(...args)` and return
    /// the method-table key (without class prefix). Codegen prepends the
    /// qualified class namespace to form the FQ ctor function name.
    ///
    /// Naming convention (matches stdlib emit):
    ///   - Single ctor: `"ClassName"` (no `$N` suffix)
    ///   - Overloaded:  `"ClassName$N"` (1-based by declaration order)
    ///
    /// Returns the method key. Special cases:
    ///   - Class has **no explicit ctor**: return `className` (VM skips ctor
    ///     call — preserves legacy "default no-arg ctor" semantics for classes
    ///     declared without a body ctor).
    ///   - Class has explicit ctor(s) but none match `argCount`: emit
    ///     diagnostic and return `className` as graceful fallback.
    private string ResolveCtorName(string className, int argCount, Span span)
    {
        Z42ClassType? cls = null;
        if (_symbols.Classes.TryGetValue(className, out var local))
            cls = local;
        else if (_imported?.Classes.TryGetValue(className, out var imp) == true)
            cls = imp;

        if (cls is null) return className; // 类未找到（下游报错）

        bool hasSingle      = cls.Methods.ContainsKey(className);
        var  overloadKeys   = cls.Methods.Keys
            .Where(k => k.StartsWith(className + "$"))
            .ToList();
        bool hasExplicitCtor = hasSingle || overloadKeys.Count > 0;

        // 无显式 ctor → 默认无参 ctor 语义；VM 跳过 ctor 调用。
        if (!hasExplicitCtor) return className;

        // arity 匹配判定（含 default params）：argCount 在 [MinArgCount, Params.Count] 闭区间。
        bool ArityMatches(Z42FuncType sig) =>
            argCount >= sig.MinArgCount && argCount <= sig.Params.Count;

        // 单 ctor: arity 检查（Z42FuncType.Params 不含 this）
        if (hasSingle && overloadKeys.Count == 0)
        {
            var sig = cls.Methods[className];
            if (ArityMatches(sig))
                return className;
            _diags.Error(DiagnosticCodes.TypeMismatch,
                $"constructor of `{className}` expects {sig.MinArgCount}..{sig.Params.Count} argument(s), got {argCount}", span);
            return className;
        }

        // 重载: 按 arity 选
        foreach (var key in overloadKeys)
        {
            if (ArityMatches(cls.Methods[key]))
                return key;
        }
        // 单 ctor 与重载并存的情况（罕见）：单 ctor 也比一下
        if (hasSingle && ArityMatches(cls.Methods[className]))
            return className;

        _diags.Error(DiagnosticCodes.TypeMismatch,
            $"no constructor of `{className}` matches {argCount} argument(s)", span);
        return className;
    }
}
