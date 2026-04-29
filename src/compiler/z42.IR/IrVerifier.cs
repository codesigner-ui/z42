using System.Diagnostics;

namespace Z42.IR;

/// <summary>
/// Validates IR well-formedness after generation.
/// Runs only in Debug builds; catches bugs before they reach the VM.
/// </summary>
public static class IrVerifier
{
    /// <summary>
    /// Verify all functions in the module. Returns a list of error messages (empty = valid).
    /// </summary>
    public static IReadOnlyList<string> Verify(IrModule module)
    {
        var errors = new List<string>();
        foreach (var fn in module.Functions)
        {
            VerifyBlockLabelsUnique(fn, errors);
            VerifyBranchTargets(fn, errors);
            VerifyDefUse(fn, errors);
            VerifyExceptionTable(fn, errors);
        }
        return errors;
    }

    /// <summary>
    /// Verify and assert — throws in Debug builds if any errors are found.
    /// </summary>
    [Conditional("DEBUG")]
    public static void VerifyOrThrow(IrModule module)
    {
        var errors = Verify(module);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"IR verification failed with {errors.Count} error(s):\n" +
                string.Join("\n", errors));
    }

    private static void VerifyBlockLabelsUnique(IrFunction fn, List<string> errors)
    {
        var seen = new HashSet<string>();
        foreach (var block in fn.Blocks)
        {
            if (!seen.Add(block.Label))
                errors.Add($"[{fn.Name}] duplicate block label `{block.Label}`");
        }
    }

    private static void VerifyBranchTargets(IrFunction fn, List<string> errors)
    {
        var labels = new HashSet<string>(fn.Blocks.Select(b => b.Label));

        foreach (var block in fn.Blocks)
        {
            switch (block.Terminator)
            {
                case BrTerm br:
                    CheckLabel(fn.Name, br.Label, labels, errors);
                    break;
                case BrCondTerm brCond:
                    CheckLabel(fn.Name, brCond.TrueLabel, labels, errors);
                    CheckLabel(fn.Name, brCond.FalseLabel, labels, errors);
                    break;
            }
        }
    }

    private static void CheckLabel(string fnName, string target,
        HashSet<string> labels, List<string> errors)
    {
        if (!labels.Contains(target))
            errors.Add($"[{fnName}] branch target `{target}` does not exist");
    }

    private static void VerifyDefUse(IrFunction fn, List<string> errors)
    {
        // Collect all defined register IDs (Dst fields across all instructions).
        var defined = new HashSet<int>();

        // Parameters are implicitly defined as registers 0..ParamCount-1.
        for (int i = 0; i < fn.ParamCount; i++)
            defined.Add(i);

        // Exception table catch registers are also defined at handler entry.
        if (fn.ExceptionTable is { } table)
        {
            foreach (var entry in table)
                defined.Add(entry.CatchReg.Id);
        }

        foreach (var block in fn.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                // Check uses first, then add defs.
                foreach (var use in GetUses(instr))
                {
                    if (!defined.Contains(use.Id))
                        errors.Add(
                            $"[{fn.Name}] block `{block.Label}`: " +
                            $"register r{use.Id} used before definition in {instr.GetType().Name}");
                }

                if (GetDst(instr) is { } dst)
                    defined.Add(dst.Id);
            }

            // Terminator uses
            foreach (var use in GetTerminatorUses(block.Terminator))
            {
                if (!defined.Contains(use.Id))
                    errors.Add(
                        $"[{fn.Name}] block `{block.Label}`: " +
                        $"register r{use.Id} used before definition in terminator {block.Terminator.GetType().Name}");
            }
        }
    }

    private static void VerifyExceptionTable(IrFunction fn, List<string> errors)
    {
        if (fn.ExceptionTable is not { } table) return;
        var labels = new HashSet<string>(fn.Blocks.Select(b => b.Label));

        foreach (var entry in table)
        {
            if (!labels.Contains(entry.TryStart))
                errors.Add($"[{fn.Name}] exception table: TryStart `{entry.TryStart}` not found");
            if (!labels.Contains(entry.TryEnd))
                errors.Add($"[{fn.Name}] exception table: TryEnd `{entry.TryEnd}` not found");
            if (!labels.Contains(entry.CatchLabel))
                errors.Add($"[{fn.Name}] exception table: CatchLabel `{entry.CatchLabel}` not found");
        }
    }

    // ── Register extraction helpers ──────────────────────────────────────────

    private static TypedReg? GetDst(IrInstr instr) => instr switch
    {
        ConstStrInstr   i => i.Dst,
        ConstI32Instr   i => i.Dst,
        ConstI64Instr   i => i.Dst,
        ConstF64Instr   i => i.Dst,
        ConstBoolInstr  i => i.Dst,
        ConstCharInstr  i => i.Dst,
        ConstNullInstr  i => i.Dst,
        CopyInstr       i => i.Dst,
        StrConcatInstr  i => i.Dst,
        ToStrInstr      i => i.Dst,
        CallInstr       i => i.Dst,
        BuiltinInstr    i => i.Dst,
        AddInstr        i => i.Dst,
        SubInstr        i => i.Dst,
        MulInstr        i => i.Dst,
        DivInstr        i => i.Dst,
        RemInstr        i => i.Dst,
        EqInstr         i => i.Dst,
        NeInstr         i => i.Dst,
        LtInstr         i => i.Dst,
        LeInstr         i => i.Dst,
        GtInstr         i => i.Dst,
        GeInstr         i => i.Dst,
        AndInstr        i => i.Dst,
        OrInstr         i => i.Dst,
        NotInstr        i => i.Dst,
        NegInstr        i => i.Dst,
        BitAndInstr     i => i.Dst,
        BitOrInstr      i => i.Dst,
        BitXorInstr     i => i.Dst,
        BitNotInstr     i => i.Dst,
        ShlInstr        i => i.Dst,
        ShrInstr        i => i.Dst,
        ArrayNewInstr   i => i.Dst,
        ArrayNewLitInstr i => i.Dst,
        ArrayGetInstr   i => i.Dst,
        ArrayLenInstr   i => i.Dst,
        ObjNewInstr     i => i.Dst,
        FieldGetInstr   i => i.Dst,
        VCallInstr      i => i.Dst,
        IsInstanceInstr i => i.Dst,
        AsCastInstr     i => i.Dst,
        StaticGetInstr  i => i.Dst,
        // Native interop (specs C1–C5)
        CallNativeInstr        i => i.Dst,
        CallNativeVtableInstr  i => i.Dst,
        PinPtrInstr            i => i.Dst,
        // No Dst: ArraySetInstr, FieldSetInstr, StaticSetInstr, UnpinPtrInstr
        _ => null,
    };

    private static IEnumerable<TypedReg> GetUses(IrInstr instr) => instr switch
    {
        CopyInstr       i => [i.Src],
        StrConcatInstr  i => [i.A, i.B],
        ToStrInstr      i => [i.Src],
        CallInstr       i => i.Args,
        BuiltinInstr    i => i.Args,
        AddInstr        i => [i.A, i.B],
        SubInstr        i => [i.A, i.B],
        MulInstr        i => [i.A, i.B],
        DivInstr        i => [i.A, i.B],
        RemInstr        i => [i.A, i.B],
        EqInstr         i => [i.A, i.B],
        NeInstr         i => [i.A, i.B],
        LtInstr         i => [i.A, i.B],
        LeInstr         i => [i.A, i.B],
        GtInstr         i => [i.A, i.B],
        GeInstr         i => [i.A, i.B],
        AndInstr        i => [i.A, i.B],
        OrInstr         i => [i.A, i.B],
        NotInstr        i => [i.Src],
        NegInstr        i => [i.Src],
        BitAndInstr     i => [i.A, i.B],
        BitOrInstr      i => [i.A, i.B],
        BitXorInstr     i => [i.A, i.B],
        BitNotInstr     i => [i.Src],
        ShlInstr        i => [i.A, i.B],
        ShrInstr        i => [i.A, i.B],
        ArrayNewInstr   i => [i.Size],
        ArrayNewLitInstr i => i.Elems,
        ArrayGetInstr   i => [i.Arr, i.Idx],
        ArraySetInstr   i => [i.Arr, i.Idx, i.Val],
        ArrayLenInstr   i => [i.Arr],
        ObjNewInstr     i => i.Args,
        FieldGetInstr   i => [i.Obj],
        FieldSetInstr   i => [i.Obj, i.Val],
        VCallInstr      i => [i.Obj, ..i.Args],
        IsInstanceInstr i => [i.Obj],
        AsCastInstr     i => [i.Obj],
        StaticSetInstr  i => [i.Val],
        // Native interop (specs C1–C5)
        CallNativeInstr        i => i.Args,
        CallNativeVtableInstr  i => [i.Recv, ..i.Args],
        PinPtrInstr            i => [i.Src],
        UnpinPtrInstr          i => [i.Pinned],
        // No uses: ConstXxx, StaticGetInstr
        _ => [],
    };

    private static IEnumerable<TypedReg> GetTerminatorUses(IrTerminator term) => term switch
    {
        RetTerm   { Reg: { } r } => [r],
        BrCondTerm bc            => [bc.Cond],
        ThrowTerm  t             => [t.Reg],
        _                        => [],
    };
}
