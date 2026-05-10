using Z42.Core.Text;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Bound expression emission — part of FunctionEmitter.
internal sealed partial class FunctionEmitter
{
    // ── Bound expression dispatcher ───────────────────────────────────────────

    /// Lazy-instantiated expr visitor (nested class below). Per-emitter
    /// instance so it can hold a reference to `this` for outer access.
    private IrEmitExprVisitor? _exprVisitor;

    private TypedReg EmitExpr(BoundExpr expr)
    {
        TrackLine(expr.Span);
        return (_exprVisitor ??= new IrEmitExprVisitor(this)).Visit(expr);
    }

    /// introduce-bound-visitor S7 — replaces the legacy 27-case switch in
    /// `EmitExpr`. Nested private class so it accesses FunctionEmitter's
    /// private state through `_e` without leaking visibility. Behavior
    /// identical: each Visit method either calls a partial helper
    /// (EmitBound{Call,Binary,Member,...}) or emits inline.
    private sealed class IrEmitExprVisitor : BoundExprVisitor<TypedReg>
    {
        private readonly FunctionEmitter _e;
        public IrEmitExprVisitor(FunctionEmitter e) { _e = e; }

        // ── Literals ──────────────────────────────────────────────────────────

        protected override TypedReg VisitLitStr(BoundLitStr s)
        {
            var dst = _e.Alloc(IrType.Str);
            _e.Emit(new ConstStrInstr(dst, _e._ctx.Intern(s.Value)));
            return dst;
        }

        protected override TypedReg VisitLitInt(BoundLitInt n)
        {
            var dst = _e.Alloc(ToIrType(n.Type));
            // Always emit I64: VM creates Value::I64 for all ints.
            // TypedReg.Type carries the narrower type for future typed dispatch.
            _e.Emit(new ConstI64Instr(dst, n.Value));
            return dst;
        }

        protected override TypedReg VisitLitFloat(BoundLitFloat f)
        {
            var dst = _e.Alloc(ToIrType(f.Type));
            _e.Emit(new ConstF64Instr(dst, f.Value));
            return dst;
        }

        protected override TypedReg VisitLitBool(BoundLitBool b)
        {
            var dst = _e.Alloc(IrType.Bool);
            _e.Emit(new ConstBoolInstr(dst, b.Value));
            return dst;
        }

        protected override TypedReg VisitLitNull(BoundLitNull n)
        {
            var dst = _e.Alloc(IrType.Ref);
            _e.Emit(new ConstNullInstr(dst));
            return dst;
        }

        protected override TypedReg VisitLitChar(BoundLitChar c)
        {
            var dst = _e.Alloc(IrType.Char);
            _e.Emit(new ConstCharInstr(dst, c.Value));
            return dst;
        }

        protected override TypedReg VisitDefault(BoundDefault def)
        {
            // 2026-05-07 add-default-generic-typeparam (D-8b-3 Phase 2):
            // generic type-parameter T → emit `DefaultOfInstr(idx)`. Runtime
            // resolves `this.type_desc.type_args[idx]` and looks up the zero
            // value via `default_value_for(tag)`. Method-level / free generic
            // path carries idx=0 and gracefully returns Null at runtime.
            if (def.GenericParamIndex is int paramIdx)
            {
                var dstG = _e.Alloc(IrType.Ref);
                _e.Emit(new DefaultOfInstr(dstG, (byte)paramIdx));
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
                        var dstF = _e.Alloc(IrType.F64);
                        _e.Emit(new ConstF64Instr(dstF, 0.0));
                        return dstF;
                    }
                    case "bool":
                    {
                        var dstB = _e.Alloc(IrType.Bool);
                        _e.Emit(new ConstBoolInstr(dstB, false));
                        return dstB;
                    }
                    case "char":
                    {
                        var dstC = _e.Alloc(IrType.Char);
                        _e.Emit(new ConstCharInstr(dstC, '\0'));
                        return dstC;
                    }
                    case "string":
                    {
                        var dstS = _e.Alloc(IrType.Ref);
                        _e.Emit(new ConstNullInstr(dstS));
                        return dstS;
                    }
                    default:
                        // numeric primitives (int / long / short / byte and i8..u64 aliases)
                        if (Z42Type.IsNumeric(pt))
                        {
                            var dstN = _e.Alloc(ToIrType(pt));
                            _e.Emit(new ConstI64Instr(dstN, 0));
                            return dstN;
                        }
                        break;
                }
            }
            // class / interface / array / nullable / struct (unsupported types
            // were already converted to Z42Type.Error in TypeChecker — they still
            // hit this branch and emit a harmless null so codegen doesn't crash
            // on the same input that produced E0421).
            var dstRef = _e.Alloc(IrType.Ref);
            _e.Emit(new ConstNullInstr(dstRef));
            return dstRef;
        }

        protected override TypedReg VisitInterpolatedStr(BoundInterpolatedStr interp)
            => _e.EmitInterpolation(interp);

        // ── Identifiers ───────────────────────────────────────────────────────

        protected override TypedReg VisitIdent(BoundIdent id)
        {
            if (_e._locals.TryGetValue(id.Name, out var reg))
                return reg;
            if (_e._instanceFields.Contains(id.Name))
            {
                var dst = _e.Alloc(ToIrType(id.Type));
                _e.Emit(new FieldGetInstr(dst, new TypedReg(0, IrType.Ref), id.Name));
                return dst;
            }
            // 2026-05-02 impl-closure-l3-monomorphize prerequisite: ident
            // 解析为顶层函数 / 静态方法 → emit LoadFn 把 FuncRef 装入寄存器。
            // 之前这条路径不存在，导致 `var f = Helper;` 在 Codegen 崩溃；
            // mono spec 的 alias 跟踪让 `f()` 直接走 Call 而不需要 LoadFn，
            // 但 `var g = f;` / `Apply(Helper, ...)` 等场景仍需要把 ident 装载
            // 为 FuncRef，因此补齐这条根因路径。
            if (id.Type is Z42FuncType
                && _e._ctx.TopLevelFunctionNames.Contains(id.Name))
            {
                // 2026-05-02 D1b: 顶层函数方法组转换 → LoadFnCached
                //（共享 module-level cache slot；同 fn name 跨多 site 复用）
                var dst = _e.Alloc(IrType.Ref);
                var fq  = _e._ctx.QualifyName(id.Name);
                var slot = _e._ctx.GetOrAllocFuncRefSlot(fq);
                _e.Emit(new LoadFnCachedInstr(dst, fq, (uint)slot));
                return dst;
            }
            if (id.Type is Z42FuncType
                && _e._currentClassName is not null
                && _e._ctx.ClassRegistry.TryGetStaticMethods(
                    _e._ctx.QualifyName(_e._currentClassName), out var staticSet)
                && staticSet.Contains(id.Name))
            {
                // 2026-05-02 D1b: 静态方法方法组转换 → LoadFnCached
                var dst = _e.Alloc(IrType.Ref);
                var fq  = $"{_e._ctx.QualifyName(_e._currentClassName)}.{id.Name}";
                var slot = _e._ctx.GetOrAllocFuncRefSlot(fq);
                _e.Emit(new LoadFnCachedInstr(dst, fq, (uint)slot));
                return dst;
            }
            throw new InvalidOperationException($"undefined variable `{id.Name}`");
        }

        protected override TypedReg VisitCapturedIdent(BoundCapturedIdent ci)
        {
            // Captured ident lowering: env[CaptureIndex] read.
            // See docs/design/language/closure.md §6 + impl-closure-l3-core Decision 7.
            if (_e._envReg < 0)
                throw new InvalidOperationException(
                    $"BoundCapturedIdent `{ci.Name}` reached emitter without an active env register (ICE)");
            var idxReg = _e.Alloc(IrType.I32);
            _e.Emit(new ConstI32Instr(idxReg, ci.CaptureIndex));
            var dst = _e.Alloc(ToIrType(ci.Type));
            _e.Emit(new ArrayGetInstr(dst, new TypedReg(_e._envReg, IrType.Ref), idxReg));
            return dst;
        }

        // ── Operators / calls (delegate to existing partial helpers) ──────────

        protected override TypedReg VisitLambda(BoundLambda lambda)
            => _e.EmitLambdaLiteral(lambda);

        protected override TypedReg VisitAssign(BoundAssign a)             => _e.EmitBoundAssign(a);
        protected override TypedReg VisitCall(BoundCall c)                 => _e.EmitBoundCall(c);
        protected override TypedReg VisitIndirectCall(BoundIndirectCall ic) => _e.EmitBoundIndirectCall(ic);
        protected override TypedReg VisitBinary(BoundBinary b)             => _e.EmitBoundBinary(b);
        protected override TypedReg VisitUnary(BoundUnary u)               => _e.EmitBoundUnary(u);
        protected override TypedReg VisitPostfix(BoundPostfix p)           => _e.EmitBoundPostfix(p);
        protected override TypedReg VisitConditional(BoundConditional t)   => _e.EmitBoundTernary(t);
        protected override TypedReg VisitNullConditional(BoundNullConditional nc)
            => _e.EmitBoundNullConditional(nc);
        protected override TypedReg VisitNullCoalesce(BoundNullCoalesce nc)
            => _e.EmitBoundNullCoalesce(nc);

        protected override TypedReg VisitIsPattern(BoundIsPattern ipe)
        {
            var objReg  = _e.EmitExpr(ipe.Target);
            var boolReg = _e.Alloc(IrType.Bool);
            // 2026-05-05: must use QualifyClassName (resolves imports)
            // rather than QualifyName (always prepends current CU's
            // namespace). Without this, `e is TestFailure` from a CU
            // whose namespace is `Z42TestDogfood` emits class_name =
            // "Z42TestDogfood.TestFailure" while the runtime class is
            // "Std.TestFailure" → IsInstance returns false.
            var qualName = _e._ctx.QualifyClassName(ipe.TypeName);
            _e.Emit(new IsInstanceInstr(boolReg, objReg, qualName));
            var castReg = _e.Alloc(IrType.Ref);
            _e.Emit(new AsCastInstr(castReg, objReg, qualName));
            _e._locals[ipe.Binding] = castReg;
            return boolReg;
        }

        protected override TypedReg VisitCast(BoundCast cast)
            => _e.EmitExpr(cast.Operand); // cast is a no-op in IR

        protected override TypedReg VisitMember(BoundMember m)
            => _e.EmitBoundMember(m);

        protected override TypedReg VisitIndex(BoundIndex ix)
        {
            var targetReg = _e.EmitExpr(ix.Target);
            var idxReg    = _e.EmitExpr(ix.Index);
            var dst       = _e.Alloc(ToIrType(ix.Type));
            _e.Emit(new ArrayGetInstr(dst, targetReg, idxReg));
            return dst;
        }

        protected override TypedReg VisitArrayCreate(BoundArrayCreate ac)
        {
            var sizeReg = _e.EmitExpr(ac.Size);
            var dst = _e.Alloc(IrType.Ref);
            _e.Emit(new ArrayNewInstr(dst, sizeReg));
            return dst;
        }

        protected override TypedReg VisitArrayLit(BoundArrayLit al)
        {
            var elemRegs = al.Elements.Select(_e.EmitExpr).ToList();
            var dst = _e.Alloc(IrType.Ref);
            _e.Emit(new ArrayNewLitInstr(dst, elemRegs));
            return dst;
        }

        protected override TypedReg VisitNew(BoundNew n)            => _e.EmitBoundNew(n);
        protected override TypedReg VisitSwitchExpr(BoundSwitchExpr sw) => _e.EmitBoundSwitchExpr(sw);

        protected override TypedReg VisitError(BoundError err)
        {
            // BoundError should never reach Codegen — PipelineCore checks diags.HasErrors
            // after TypeCheck and bails out. If we get here, it's a compiler bug (ICE).
            throw new InvalidOperationException(
                $"BoundError reached codegen (ICE): {err.Message}. " +
                "TypeChecker should have reported an error and pipeline should have stopped.");
        }

        protected override TypedReg VisitModifiedArg(BoundModifiedArg m)
        {
            // BoundModifiedArg only appears as a callsite argument; EmitBoundCall
            // unwraps it before emitting. If it reaches the visitor directly,
            // it's a compiler bug — preserve the legacy default-throw behavior.
            throw new NotSupportedException(
                $"BoundModifiedArg should not reach EmitExpr directly (handled inside BoundCall args).");
        }
    }
}
