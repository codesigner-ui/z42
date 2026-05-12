using Z42.Semantics.TypeCheck;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Default-parameter fill-in for callsites. Two paths:
///   1. Same-CU: read `BoundDefaults[Param]` for the user-written default expr.
///   2. Cross-CU: TSIG doesn't carry default-value expressions, so emit a
///      type-zero const via `EmitTypeDefault` (bool=false / int=0 / ref=null
///      etc.). spec fix-default-param-cross-cu (D-9, 2026-05-04).
///
/// spec split-emitter-stmts-calls (2026-05-12): extracted from
/// FunctionEmitterCalls.cs to keep the main file under the 300 LOC soft
/// limit. Zero behavior change.
internal sealed partial class FunctionEmitter
{
    /// Fill omitted trailing args with their default value expressions.
    ///
    /// 2026-05-04 fix-default-param-cross-cu (D-9)：双层 fallback —
    ///   1. 优先 `_ctx.FuncParams` + `BoundDefaults`（local CU，用户写的真实
    ///      默认表达式）；
    ///   2. 跨 CU（imported 方法 FuncParams 不覆盖）→ 查 `_funcSignatures`
    ///      用 Z42FuncType.RequiredCount + Params 类型 emit type-default const
    ///      （bool=false / int=0 / ref=null 等）。完整用户 default 跨 CU 退化
    ///      为 type-default —— TSIG 当前不导出 default value 表达式，留 follow-up。
    private List<TypedReg> FillDefaults(string qualifiedName, List<TypedReg> argRegs)
    {
        if (_ctx.FuncParams.TryGetValue(qualifiedName, out var parms))
        {
            if (argRegs.Count >= parms.Count) return argRegs;
            var filled   = new List<TypedReg>(argRegs);
            var defaults = _ctx.SemanticModel.BoundDefaults;
            for (int i = argRegs.Count; i < parms.Count; i++)
            {
                if (!defaults.TryGetValue(parms[i], out var boundDefault))
                    throw new InvalidOperationException(
                        $"missing argument {i + 1} for `{qualifiedName}` and no bound default");
                filled.Add(EmitExpr(boundDefault));
            }
            return filled;
        }

        // Cross-CU fallback：用 Z42FuncType + type-default const
        if (_ctx.TryGetMethodSignature(qualifiedName, out var sig))
        {
            int total = sig.Params.Count;
            if (argRegs.Count >= total) return argRegs;
            var filled = new List<TypedReg>(argRegs);
            for (int i = argRegs.Count; i < total; i++)
                filled.Add(EmitTypeDefault(sig.Params[i]));
            return filled;
        }
        return argRegs;
    }

    /// 2026-05-04 fix-default-param-cross-cu (D-9)：emit `Z42Type` 的 type-default
    /// const 到新分配的 register。用于跨 CU 调用 fallback 填充缺位 default param。
    private TypedReg EmitTypeDefault(Z42Type t)
    {
        switch (t)
        {
            case Z42PrimType pt:
                switch (pt.Name)
                {
                    case "bool":
                    {
                        var dst = Alloc(IrType.Bool);
                        Emit(new ConstBoolInstr(dst, false));
                        return dst;
                    }
                    case "int" or "i32" or "short" or "i16" or "byte" or "u8" or "sbyte" or "i8" or "ushort" or "u16" or "uint" or "u32":
                    {
                        var dst = Alloc(IrType.I32);
                        Emit(new ConstI32Instr(dst, 0));
                        return dst;
                    }
                    case "long" or "i64" or "ulong" or "u64":
                    {
                        var dst = Alloc(IrType.I64);
                        Emit(new ConstI64Instr(dst, 0));
                        return dst;
                    }
                    case "float" or "f32" or "double" or "f64":
                    {
                        var dst = Alloc(IrType.F64);
                        Emit(new ConstF64Instr(dst, 0.0));
                        return dst;
                    }
                    case "char":
                    {
                        var dst = Alloc(IrType.Char);
                        Emit(new ConstCharInstr(dst, '\0'));
                        return dst;
                    }
                    case "string":
                    {
                        var dst = Alloc(IrType.Ref);
                        Emit(new ConstNullInstr(dst));
                        return dst;
                    }
                    default:
                    {
                        var dst = Alloc(IrType.Ref);
                        Emit(new ConstNullInstr(dst));
                        return dst;
                    }
                }
            default:
            {
                // 引用类型 / Option / Class / Interface / Func / Array / GenericParam
                var dst = Alloc(IrType.Ref);
                Emit(new ConstNullInstr(dst));
                return dst;
            }
        }
    }
}
