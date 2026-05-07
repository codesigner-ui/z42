using Z42.Core.Text;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Bound expression emission — part of FunctionEmitter.
internal sealed partial class FunctionEmitter
{
    // ── Bound expression dispatcher ───────────────────────────────────────────

    private TypedReg EmitExpr(BoundExpr expr)
    {
        TrackLine(expr.Span);
        switch (expr)
        {
            case BoundLitStr s:
            {
                var dst = Alloc(IrType.Str);
                Emit(new ConstStrInstr(dst, _ctx.Intern(s.Value)));
                return dst;
            }
            case BoundLitInt n:
            {
                var dst = Alloc(ToIrType(n.Type));
                // Always emit I64: VM creates Value::I64 for all ints.
                // TypedReg.Type carries the narrower type for future typed dispatch.
                Emit(new ConstI64Instr(dst, n.Value));
                return dst;
            }
            case BoundLitFloat f:
            {
                var dst = Alloc(ToIrType(f.Type));
                Emit(new ConstF64Instr(dst, f.Value));
                return dst;
            }
            case BoundLitBool b:
            {
                var dst = Alloc(IrType.Bool);
                Emit(new ConstBoolInstr(dst, b.Value));
                return dst;
            }
            case BoundLitNull:
            {
                var dst = Alloc(IrType.Ref);
                Emit(new ConstNullInstr(dst));
                return dst;
            }
            case BoundLitChar c:
            {
                var dst = Alloc(IrType.Char);
                Emit(new ConstCharInstr(dst, c.Value));
                return dst;
            }
            case BoundDefault def:
            {
                // 2026-05-07 add-default-generic-typeparam (D-8b-3 Phase 2):
                // generic type-parameter T → emit `DefaultOfInstr(idx)`. Runtime
                // resolves `this.type_desc.type_args[idx]` and looks up the zero
                // value via `default_value_for(tag)`. Method-level / free generic
                // path carries idx=0 and gracefully returns Null at runtime.
                if (def.GenericParamIndex is int paramIdx)
                {
                    var dstG = Alloc(IrType.Ref);
                    Emit(new DefaultOfInstr(dstG, (byte)paramIdx));
                    return dstG;
                }
                // add-default-expression (2026-05-06): zero-value emit by type tag.
                // No new IR opcode — every default(T) is one of the existing 6
                // Const* instructions. Mirrors the VM's `default_value_for(type_tag)`
                // table in src/runtime/src/metadata/types.rs.
                if (def.Target is Z42PrimType pt)
                {
                    switch (pt.Name)
                    {
                        case "double" or "float" or "f32" or "f64":
                        {
                            var dstF = Alloc(IrType.F64);
                            Emit(new ConstF64Instr(dstF, 0.0));
                            return dstF;
                        }
                        case "bool":
                        {
                            var dstB = Alloc(IrType.Bool);
                            Emit(new ConstBoolInstr(dstB, false));
                            return dstB;
                        }
                        case "char":
                        {
                            var dstC = Alloc(IrType.Char);
                            Emit(new ConstCharInstr(dstC, '\0'));
                            return dstC;
                        }
                        case "string":
                        {
                            var dstS = Alloc(IrType.Ref);
                            Emit(new ConstNullInstr(dstS));
                            return dstS;
                        }
                        default:
                            // numeric primitives (int / long / short / byte and i8..u64 aliases)
                            if (Z42Type.IsNumeric(pt))
                            {
                                var dstN = Alloc(ToIrType(pt));
                                Emit(new ConstI64Instr(dstN, 0));
                                return dstN;
                            }
                            break;
                    }
                }
                // class / interface / array / nullable / struct (unsupported types
                // were already converted to Z42Type.Error in TypeChecker — they still
                // hit this branch and emit a harmless null so codegen doesn't crash
                // on the same input that produced E0421).
                var dstRef = Alloc(IrType.Ref);
                Emit(new ConstNullInstr(dstRef));
                return dstRef;
            }
            case BoundInterpolatedStr interp:
                return EmitInterpolation(interp);

            case BoundIdent id:
            {
                if (_locals.TryGetValue(id.Name, out var reg))
                    return reg;
                if (_instanceFields.Contains(id.Name))
                {
                    var dst = Alloc(ToIrType(id.Type));
                    Emit(new FieldGetInstr(dst, new TypedReg(0, IrType.Ref), id.Name));
                    return dst;
                }
                // 2026-05-02 impl-closure-l3-monomorphize prerequisite: ident
                // 解析为顶层函数 / 静态方法 → emit LoadFn 把 FuncRef 装入寄存器。
                // 之前这条路径不存在，导致 `var f = Helper;` 在 Codegen 崩溃；
                // mono spec 的 alias 跟踪让 `f()` 直接走 Call 而不需要 LoadFn，
                // 但 `var g = f;` / `Apply(Helper, ...)` 等场景仍需要把 ident 装载
                // 为 FuncRef，因此补齐这条根因路径。
                if (id.Type is Z42FuncType
                    && _ctx.TopLevelFunctionNames.Contains(id.Name))
                {
                    // 2026-05-02 D1b: 顶层函数方法组转换 → LoadFnCached
                    //（共享 module-level cache slot；同 fn name 跨多 site 复用）
                    var dst = Alloc(IrType.Ref);
                    var fq  = _ctx.QualifyName(id.Name);
                    var slot = _ctx.GetOrAllocFuncRefSlot(fq);
                    Emit(new LoadFnCachedInstr(dst, fq, (uint)slot));
                    return dst;
                }
                if (id.Type is Z42FuncType
                    && _currentClassName is not null
                    && _ctx.ClassRegistry.TryGetStaticMethods(
                        _ctx.QualifyName(_currentClassName), out var staticSet)
                    && staticSet.Contains(id.Name))
                {
                    // 2026-05-02 D1b: 静态方法方法组转换 → LoadFnCached
                    var dst = Alloc(IrType.Ref);
                    var fq  = $"{_ctx.QualifyName(_currentClassName)}.{id.Name}";
                    var slot = _ctx.GetOrAllocFuncRefSlot(fq);
                    Emit(new LoadFnCachedInstr(dst, fq, (uint)slot));
                    return dst;
                }
                throw new InvalidOperationException($"undefined variable `{id.Name}`");
            }

            case BoundLambda lambda:
                return EmitLambdaLiteral(lambda);

            case BoundCapturedIdent ci:
            {
                // Captured ident lowering: env[CaptureIndex] read.
                // See docs/design/closure.md §6 + impl-closure-l3-core Decision 7.
                if (_envReg < 0)
                    throw new InvalidOperationException(
                        $"BoundCapturedIdent `{ci.Name}` reached emitter without an active env register (ICE)");
                var idxReg = Alloc(IrType.I32);
                Emit(new ConstI32Instr(idxReg, ci.CaptureIndex));
                var dst = Alloc(ToIrType(ci.Type));
                Emit(new ArrayGetInstr(dst, new TypedReg(_envReg, IrType.Ref), idxReg));
                return dst;
            }

            case BoundAssign assign:
                return EmitBoundAssign(assign);

            case BoundCall call:
                return EmitBoundCall(call);

            case BoundBinary bin:
                return EmitBoundBinary(bin);

            case BoundUnary u:
                return EmitBoundUnary(u);

            case BoundPostfix post:
                return EmitBoundPostfix(post);

            case BoundConditional ternary:
                return EmitBoundTernary(ternary);

            case BoundNullConditional nc:
                return EmitBoundNullConditional(nc);

            case BoundNullCoalesce nc:
                return EmitBoundNullCoalesce(nc);

            case BoundIsPattern ipe:
            {
                var objReg  = EmitExpr(ipe.Target);
                var boolReg = Alloc(IrType.Bool);
                // 2026-05-05: must use QualifyClassName (resolves imports)
                // rather than QualifyName (always prepends current CU's
                // namespace). Without this, `e is TestFailure` from a CU
                // whose namespace is `Z42TestDogfood` emits class_name =
                // "Z42TestDogfood.TestFailure" while the runtime class is
                // "Std.TestFailure" → IsInstance returns false.
                var qualName = _ctx.QualifyClassName(ipe.TypeName);
                Emit(new IsInstanceInstr(boolReg, objReg, qualName));
                var castReg = Alloc(IrType.Ref);
                Emit(new AsCastInstr(castReg, objReg, qualName));
                _locals[ipe.Binding] = castReg;
                return boolReg;
            }

            case BoundCast cast:
                return EmitExpr(cast.Operand); // cast is a no-op in IR

            case BoundMember m:
                return EmitBoundMember(m);

            case BoundIndex ix:
            {
                var targetReg = EmitExpr(ix.Target);
                var idxReg    = EmitExpr(ix.Index);
                var dst       = Alloc(ToIrType(ix.Type));
                Emit(new ArrayGetInstr(dst, targetReg, idxReg));
                return dst;
            }

            case BoundArrayCreate ac:
            {
                var sizeReg = EmitExpr(ac.Size);
                var dst = Alloc(IrType.Ref);
                Emit(new ArrayNewInstr(dst, sizeReg));
                return dst;
            }

            case BoundArrayLit al:
            {
                var elemRegs = al.Elements.Select(EmitExpr).ToList();
                var dst = Alloc(IrType.Ref);
                Emit(new ArrayNewLitInstr(dst, elemRegs));
                return dst;
            }

            case BoundNew n:
                return EmitBoundNew(n);

            case BoundSwitchExpr sw:
                return EmitBoundSwitchExpr(sw);

            case BoundError err:
                // BoundError should never reach Codegen — PipelineCore checks diags.HasErrors
                // after TypeCheck and bails out. If we get here, it's a compiler bug (ICE).
                throw new InvalidOperationException(
                    $"BoundError reached codegen (ICE): {err.Message}. " +
                    "TypeChecker should have reported an error and pipeline should have stopped.");

            default:
                throw new NotSupportedException(
                    $"BoundExpr type {expr.GetType().Name} not yet supported in FunctionEmitter");
        }
    }
}
