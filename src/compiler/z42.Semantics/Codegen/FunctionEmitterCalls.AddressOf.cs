using Z42.Semantics.Bound;
using Z42.IR;

namespace Z42.Semantics.Codegen;

/// Callsite argument lowering for `ref` / `out` / `in` (spec
/// impl-ref-out-in-runtime). Distinct entry — `EmitCallArg` is invoked
/// once per BoundCall arg by `EmitBoundCall`. The three `EmitLoad*AddrFor`
/// helpers materialise a `Value::Ref` register for the appropriate lvalue
/// shape; `LookupLocalRegOrNull` is the slot lookup used by LoadLocalAddr.
///
/// spec split-emitter-stmts-calls (2026-05-12): extracted from
/// FunctionEmitterCalls.cs to keep the main file under the 300 LOC soft
/// limit. Zero behavior change.
internal sealed partial class FunctionEmitter
{
    /// Spec impl-ref-out-in-runtime: emit one call argument. If the bound
    /// expression is a `BoundModifiedArg` (callsite has `ref`/`out`/`in`
    /// prefix), produce a `Value::Ref` via the appropriate address-load
    /// opcode based on the inner lvalue shape:
    ///   - `BoundIdent` → `LoadLocalAddr` pointing at the local's slot
    ///   - `BoundIndex` → `LoadElemAddr` (array element)
    ///   - `BoundMember` → `LoadFieldAddr` (object field)
    /// For non-modified args, falls through to normal `EmitExpr`.
    private TypedReg EmitCallArg(BoundExpr arg)
    {
        if (arg is not BoundModifiedArg ma)
            return EmitExpr(arg);

        // `out var x` 内联声明：在 Codegen 端为 x 分配一个新的 local register
        // （TypeChecker 已注册到 caller scope），让后续 EmitLoadLocalAddrFor
        // 能查找到 slot。
        if (ma.OutDecl is { } decl && !_locals.ContainsKey(decl.Name))
        {
            var declReg = Alloc(IrType.Unknown);
            _locals[decl.Name] = declReg;
        }

        return ma.Inner switch
        {
            BoundIdent id      => EmitLoadLocalAddrFor(id),
            BoundIndex idx     => EmitLoadElemAddrFor(idx),
            BoundMember mem    => EmitLoadFieldAddrFor(mem),
            // TypeChecker should have rejected non-lvalue at compile time,
            // but guard defensively: emit normal expr (acts as by-value).
            _                  => EmitExpr(ma.Inner),
        };
    }

    private TypedReg EmitLoadLocalAddrFor(BoundIdent id)
    {
        // Look up the register the ident is bound to. `EmitExpr(id)` would
        // load the *value*; we instead need the slot index. Reuse the
        // existing local lookup machinery.
        var slotReg = LookupLocalRegOrNull(id.Name)
            ?? EmitExpr(id);  // fallback: treat as expression value
        var dst = Alloc(IrType.Unknown);  // Value::Ref has no specific IrType
        Emit(new LoadLocalAddrInstr(dst, slotReg));
        return dst;
    }

    private TypedReg EmitLoadElemAddrFor(BoundIndex idx)
    {
        var arrReg = EmitExpr(idx.Target);
        var idxReg = EmitExpr(idx.Index);
        var dst    = Alloc(IrType.Unknown);
        Emit(new LoadElemAddrInstr(dst, arrReg, idxReg));
        return dst;
    }

    private TypedReg EmitLoadFieldAddrFor(BoundMember mem)
    {
        var objReg = EmitExpr(mem.Target);
        var dst    = Alloc(IrType.Unknown);
        Emit(new LoadFieldAddrInstr(dst, objReg, mem.MemberName));
        return dst;
    }

    /// Look up the IR register currently bound to the named local. Returns
    /// `null` when the name is not a local (e.g. captured variable, field,
    /// type, function ref). Used by `LoadLocalAddr` emission to find the
    /// slot to point at.
    private TypedReg? LookupLocalRegOrNull(string name)
    {
        // FunctionEmitter maintains `_locals: Dictionary<string, TypedReg>`
        // (or equivalent). Return the binding if present.
        return _locals.TryGetValue(name, out var reg) ? reg : null;
    }
}
