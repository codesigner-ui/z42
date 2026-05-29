using Z42.Core.Text;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.IR;

namespace Z42.Semantics.Codegen;

internal sealed partial class FunctionEmitter
{

    // ── Member access ─────────────────────────────────────────────────────────

    private TypedReg EmitBoundMember(BoundMember m)
    {
        // Enum constant: BoundIdent with unknown type + name in enum constants
        if (m.Target is BoundIdent enumId
            && _ctx.EnumConstants.TryGetValue($"{enumId.Name}.{m.MemberName}", out long enumVal))
        {
            var dst = Alloc(IrType.I64);
            Emit(new ConstI64Instr(dst, enumVal));
            return dst;
        }

        // Static field: BoundIdent with unknown type + class name in static fields
        if (m.Target is BoundIdent sfId
            && _ctx.TryGetStaticFieldKey(sfId.Name, m.MemberName) is { } sfKey)
        {
            var dst = Alloc(ToIrType(m.Type));
            Emit(new StaticGetInstr(dst, sfKey));
            return dst;
        }

        // 2026-05-04 D-1b: instance method group conversion `obj.Method` —
        // 当 BoundMember.Type 是 Z42FuncType（说明 `MemberName` 是方法而非字段）
        // 且 receiver 是 class，emit 一个 thunk closure：MkClos(thunk, [recv])。
        // Thunk 内部 vcall env[0].MethodName(args)；与 lambda 闭包同享 env 协议。
        // 之前这条路径直接 emit FieldGet → 把方法名当字段读 → 运行时拿到 Null →
        // CallIndirect 报错（D-1b 实施期间发现的根因）。
        if (m.Type is Z42FuncType funcTy
            && IsInstanceMethodOf(m.Target.Type, m.MemberName, funcTy.Params.Count, out var qualClass))
        {
            var recvReg   = EmitExpr(m.Target);
            var thunkName = _ctx.GetOrCreateInstanceMethodThunk(qualClass, m.MemberName, funcTy);
            var dstClos   = Alloc(IrType.Ref);
            // stackAlloc=false：closure escape 难以静态证明（用户可能存到字段 /
            // 传 cross-frame）；保守 heap-alloc。等未来 escape 分析升级再开 stack。
            Emit(new MkClosInstr(dstClos, thunkName, new List<TypedReg> { recvReg }, false));
            return dstClos;
        }

        // Instance field access
        var objReg = EmitExpr(m.Target);
        var dst2 = Alloc(ToIrType(m.Type));
        Emit(new FieldGetInstr(dst2, objReg, m.MemberName));
        return dst2;
    }

    /// 2026-05-04 D-1b: 判定 `MemberName` 是否是 receiver 类型上的实例方法
    /// （而非字段）。返回 true + 输出 qualifiedClassName 用于 thunk 命名。
    /// 同名 field + method 优先 field（保持已有行为）；arity 用 Z42FuncType.Params 计数。
    private bool IsInstanceMethodOf(Z42Type recvType, string name, int arity, out string qualClass)
    {
        qualClass = "";
        switch (recvType)
        {
            case Z42ClassType ct:
                if (ct.Fields.ContainsKey(name)) return false;  // field 优先
                if (ct.Methods.ContainsKey(name)
                    || ct.Methods.ContainsKey($"{name}${arity}"))
                {
                    qualClass = _ctx.QualifyClassName(ct.Name);
                    return true;
                }
                return false;
            case Z42InstantiatedType inst:
                var def = inst.Definition;
                if (def.Fields.ContainsKey(name)) return false;
                if (def.Methods.ContainsKey(name)
                    || def.Methods.ContainsKey($"{name}${arity}"))
                {
                    qualClass = _ctx.QualifyClassName(def.Name);
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    // ── Assignment ────────────────────────────────────────────────────────────

    private TypedReg EmitBoundAssign(BoundAssign assign)
    {
        var valReg = EmitExpr(assign.Value);

        if (assign.Target is BoundIdent id)
        {
            WriteBackName(id.Name, valReg);
        }
        else if (assign.Target is BoundIndex ix)
        {
            var arrReg = EmitExpr(ix.Target);
            var idxReg = EmitExpr(ix.Index);
            Emit(new ArraySetInstr(arrReg, idxReg, valReg));
        }
        else if (assign.Target is BoundMember fm)
        {
            // Static field assignment via BoundIdent target
            if (fm.Target is BoundIdent { Name: var aClsName }
                && _ctx.TryGetStaticFieldKey(aClsName, fm.MemberName) is { } sfKey)
            {
                Emit(new StaticSetInstr(sfKey, valReg));
            }
            else
            {
                var objReg = EmitExpr(fm.Target);
                Emit(new FieldSetInstr(objReg, fm.MemberName, valReg));
            }
        }

        return valReg;
    }
    // ── New object ────────────────────────────────────────────────────────────

    private TypedReg EmitBoundNew(BoundNew n)
    {
        // L3-G4h step3: `new List<T>()` / `new Dictionary<K,V>()` 走普通 ObjNew 路径
        // 到 stdlib `Std.Collections.List` / `Std.Collections.Dictionary`.
        // L3-Impl2-followup (2026-04-26 script-first-stringbuilder): `new StringBuilder()`
        // 也走普通路径 — 不再拦截到 __sb_new builtin（StringBuilder 现在是纯脚本类）。
        // L3-G4d: QualifyClassName honours imports so `new Stack<int>()` can
        // resolve to `Std.Collections.Stack` when only the stdlib version is in
        // scope. Local classes win over same-named imports (handled in QualifyClassName).
        var argRegs = n.Args.Select(EmitExpr).ToList();
        string qualCls = _ctx.QualifyClassName(n.QualName);
        // fix-vcall-dep-tracking (2026-05-29): record the constructed class's
        // dependency namespace so the runtime declares + lazy-loads its zpkg.
        // `new LinkedList<int>()` on Std.Collections must register z42.collections
        // as a dependency; otherwise the runtime never loads it and the first
        // method VCall (`list.IsEmpty()`) fails "function not found". Without
        // this, a CU that only touches an imported class via `new` + virtual
        // methods (never a DepIndex-static call) records no dependency at all —
        // the root cause of the cross-platform-flaky "VCall not found".
        if (_ctx.ImportedClassNamespaces.TryGetValue(n.QualName, out var ctorDepNs))
            _ctx.TrackDepNamespace(ctorDepNs);
        // FQ ctor name = "{qualifiedClass}.{methodKey}" — TypeChecker
        // 已在 BoundNew.CtorName 提供 method key（含 $N suffix 如有）。
        string fqCtor = $"{qualCls}.{n.CtorName}";
        argRegs = FillDefaults(fqCtor, argRegs);
        var dst = Alloc(IrType.Ref);
        // 2026-05-07 add-default-generic-typeparam (D-8b-3 Phase 2): when this
        // is `new Foo<T1, T2>()`, the bound result type is Z42InstantiatedType
        // carrying TypeArgs. Forward each to the IR as a runtime type-tag string
        // so VM populates the new instance's type_args; this is the source of
        // truth for `DefaultOf` and any future runtime type-args queries.
        IReadOnlyList<string>? typeArgs = n.Type is Z42InstantiatedType inst
            ? inst.TypeArgs.Select(ExportedTypeExtractor.TypeToString).ToList()
            : null;
        Emit(new ObjNewInstr(dst, qualCls, fqCtor, argRegs, typeArgs));
        return dst;
    }
}
