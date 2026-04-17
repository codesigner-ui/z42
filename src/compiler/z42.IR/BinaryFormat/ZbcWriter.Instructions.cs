// ReSharper disable SwitchStatementHandlesSomeKnownEnumValuesWithDefault

namespace Z42.IR.BinaryFormat;

/// Instruction encoding and register visitor helpers — part of ZbcWriter.
public static partial class ZbcWriter
{
    // ── Instruction encoding ──────────────────────────────────────────────────

    private const ushort NoReg = 0xFFFF;

    private static void WriteInstr(
        BinaryWriter w, IrInstr instr,
        StringPool pool, int[] strRemap,
        Dictionary<string, ushort> blockIdx)
    {
        switch (instr)
        {
            case ConstStrInstr i:
                w.Write(Opcodes.ConstStr); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                w.Write((uint)strRemap[i.Idx]);
                break;
            case ConstI32Instr i:
                w.Write(Opcodes.ConstI); w.Write(TypeTags.I32); WriteReg(w, i.Dst);
                w.Write(i.Val);
                break;
            case ConstI64Instr i:
                w.Write(Opcodes.ConstI); w.Write(TypeTags.I64); WriteReg(w, i.Dst);
                w.Write(i.Val);
                break;
            case ConstF64Instr i:
                w.Write(Opcodes.ConstF); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                w.Write(i.Val);
                break;
            case ConstBoolInstr i:
                w.Write(Opcodes.ConstBool); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                w.Write((byte)(i.Val ? 1 : 0));
                break;
            case ConstCharInstr i:
                w.Write(Opcodes.ConstChar); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                w.Write((int)i.Val);
                break;
            case ConstNullInstr i:
                w.Write(Opcodes.ConstNull); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                break;
            case CopyInstr i:
                w.Write(Opcodes.Copy); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                WriteReg(w, i.Src);
                break;
            case StoreInstr i:
                w.Write(Opcodes.Store); w.Write(TypeTagFromIrType(i.Src.Type)); w.Write(NoReg);
                w.Write((uint)pool.Idx(i.Var)); WriteReg(w, i.Src);
                break;
            case LoadInstr i:
                w.Write(Opcodes.Load); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                w.Write((uint)pool.Idx(i.Var));
                break;

            case AddInstr i:    WriteBin(w, Opcodes.Add,    i.Dst, i.A, i.B); break;
            case SubInstr i:    WriteBin(w, Opcodes.Sub,    i.Dst, i.A, i.B); break;
            case MulInstr i:    WriteBin(w, Opcodes.Mul,    i.Dst, i.A, i.B); break;
            case DivInstr i:    WriteBin(w, Opcodes.Div,    i.Dst, i.A, i.B); break;
            case RemInstr i:    WriteBin(w, Opcodes.Rem,    i.Dst, i.A, i.B); break;
            case AndInstr i:    WriteBin(w, Opcodes.And,    i.Dst, i.A, i.B); break;
            case OrInstr  i:    WriteBin(w, Opcodes.Or,     i.Dst, i.A, i.B); break;
            case BitAndInstr i: WriteBin(w, Opcodes.BitAnd, i.Dst, i.A, i.B); break;
            case BitOrInstr  i: WriteBin(w, Opcodes.BitOr,  i.Dst, i.A, i.B); break;
            case BitXorInstr i: WriteBin(w, Opcodes.BitXor, i.Dst, i.A, i.B); break;
            case ShlInstr i:    WriteBin(w, Opcodes.Shl,    i.Dst, i.A, i.B); break;
            case ShrInstr i:    WriteBin(w, Opcodes.Shr,    i.Dst, i.A, i.B); break;
            case StrConcatInstr i: WriteBin(w, Opcodes.StrConcat, i.Dst, i.A, i.B); break;

            case EqInstr i: WriteBin(w, Opcodes.Eq, i.Dst, i.A, i.B); break;
            case NeInstr i: WriteBin(w, Opcodes.Ne, i.Dst, i.A, i.B); break;
            case LtInstr i: WriteBin(w, Opcodes.Lt, i.Dst, i.A, i.B); break;
            case LeInstr i: WriteBin(w, Opcodes.Le, i.Dst, i.A, i.B); break;
            case GtInstr i: WriteBin(w, Opcodes.Gt, i.Dst, i.A, i.B); break;
            case GeInstr i: WriteBin(w, Opcodes.Ge, i.Dst, i.A, i.B); break;

            case NegInstr    i: WriteUn(w, Opcodes.Neg,    i.Dst, i.Src); break;
            case NotInstr    i: WriteUn(w, Opcodes.Not,    i.Dst, i.Src); break;
            case BitNotInstr i: WriteUn(w, Opcodes.BitNot, i.Dst, i.Src); break;
            case ToStrInstr  i: WriteUn(w, Opcodes.ToStr,  i.Dst, i.Src); break;
            case ArrayLenInstr i: WriteUn(w, Opcodes.ArrayLen, i.Dst, i.Arr); break;

            case CallInstr i:
                w.Write(Opcodes.Call); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                w.Write((uint)pool.Idx(i.Func));
                WriteArgs(w, i.Args);
                break;
            case BuiltinInstr i:
                w.Write(Opcodes.Builtin); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                w.Write((uint)pool.Idx(i.Name));
                WriteArgs(w, i.Args);
                break;
            case VCallInstr i:
                w.Write(Opcodes.VCall); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                w.Write((uint)pool.Idx(i.Method)); WriteReg(w, i.Obj);
                WriteArgs(w, i.Args);
                break;

            case FieldGetInstr i:
                w.Write(Opcodes.FieldGet); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                WriteReg(w, i.Obj); w.Write((uint)pool.Idx(i.FieldName));
                break;
            case FieldSetInstr i:
                w.Write(Opcodes.FieldSet); w.Write(TypeTagFromIrType(i.Val.Type)); w.Write(NoReg);
                WriteReg(w, i.Obj); w.Write((uint)pool.Idx(i.FieldName)); WriteReg(w, i.Val);
                break;
            case StaticGetInstr i:
                w.Write(Opcodes.StaticGet); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                w.Write((uint)pool.Idx(i.Field));
                break;
            case StaticSetInstr i:
                w.Write(Opcodes.StaticSet); w.Write(TypeTagFromIrType(i.Val.Type)); w.Write(NoReg);
                w.Write((uint)pool.Idx(i.Field)); WriteReg(w, i.Val);
                break;

            case ObjNewInstr i:
                w.Write(Opcodes.ObjNew); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                w.Write((uint)pool.Idx(i.ClassName));
                WriteArgs(w, i.Args);
                break;
            case IsInstanceInstr i:
                w.Write(Opcodes.IsInstance); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                WriteReg(w, i.Obj); w.Write((uint)pool.Idx(i.ClassName));
                break;
            case AsCastInstr i:
                w.Write(Opcodes.AsCast); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                WriteReg(w, i.Obj); w.Write((uint)pool.Idx(i.ClassName));
                break;

            case ArrayNewInstr i:
                w.Write(Opcodes.ArrayNew); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                WriteReg(w, i.Size);
                break;
            case ArrayNewLitInstr i:
                w.Write(Opcodes.ArrayNewLit); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                WriteArgs(w, i.Elems);
                break;
            case ArrayGetInstr i:
                w.Write(Opcodes.ArrayGet); w.Write(TypeTagFromIrType(i.Dst.Type)); WriteReg(w, i.Dst);
                WriteReg(w, i.Arr); WriteReg(w, i.Idx);
                break;
            case ArraySetInstr i:
                w.Write(Opcodes.ArraySet); w.Write(TypeTagFromIrType(i.Val.Type)); w.Write(NoReg);
                WriteReg(w, i.Arr); WriteReg(w, i.Idx); WriteReg(w, i.Val);
                break;

            default:
                throw new InvalidOperationException($"ZbcWriter: unhandled instruction {instr.GetType().Name}");
        }
    }

    private static void WriteTerminator(
        BinaryWriter w, IrTerminator term, Dictionary<string, ushort> blockIdx)
    {
        switch (term)
        {
            case RetTerm { Reg: null }:
                w.Write(Opcodes.Ret); w.Write(TypeTags.Unknown); w.Write(NoReg);
                break;
            case RetTerm { Reg: TypedReg r }:
                w.Write(Opcodes.RetVal); w.Write(TypeTagFromIrType(r.Type)); w.Write((ushort)r.Id);
                break;
            case BrTerm bt:
                w.Write(Opcodes.Br); w.Write(TypeTags.Unknown); w.Write(NoReg);
                w.Write(blockIdx[bt.Label]);
                break;
            case BrCondTerm bc:
                w.Write(Opcodes.BrCond); w.Write(TypeTagFromIrType(bc.Cond.Type)); WriteReg(w, bc.Cond);
                w.Write(blockIdx[bc.TrueLabel]); w.Write(blockIdx[bc.FalseLabel]);
                break;
            case ThrowTerm tt:
                w.Write(Opcodes.Throw); w.Write(TypeTagFromIrType(tt.Reg.Type)); WriteReg(w, tt.Reg);
                break;
            default:
                throw new InvalidOperationException($"ZbcWriter: unhandled terminator {term.GetType().Name}");
        }
    }

    // ── Encoding helpers ─────────────────────────────────────────────────────

    private static void WriteBin(BinaryWriter w, byte op, TypedReg dst, TypedReg a, TypedReg b)
    {
        w.Write(op); w.Write(TypeTagFromIrType(dst.Type)); w.Write((ushort)dst.Id);
        w.Write((ushort)a.Id); w.Write((ushort)b.Id);
    }

    private static void WriteUn(BinaryWriter w, byte op, TypedReg dst, TypedReg src)
    {
        w.Write(op); w.Write(TypeTagFromIrType(dst.Type)); w.Write((ushort)dst.Id);
        w.Write((ushort)src.Id);
    }

    private static void WriteReg(BinaryWriter w, TypedReg reg) => w.Write((ushort)reg.Id);

    private static void WriteArgs(BinaryWriter w, List<TypedReg> args)
    {
        w.Write((byte)args.Count);
        foreach (var a in args) w.Write((ushort)a.Id);
    }

    internal static byte TypeTagFromIrType(IrType type) => type switch
    {
        IrType.Unknown => TypeTags.Unknown,
        IrType.Bool    => TypeTags.Bool,
        IrType.I8      => TypeTags.I8,
        IrType.I16     => TypeTags.I16,
        IrType.I32     => TypeTags.I32,
        IrType.I64     => TypeTags.I64,
        IrType.U8      => TypeTags.U8,
        IrType.U16     => TypeTags.U16,
        IrType.U32     => TypeTags.U32,
        IrType.U64     => TypeTags.U64,
        IrType.F32     => TypeTags.F32,
        IrType.F64     => TypeTags.F64,
        IrType.Char    => TypeTags.Char,
        IrType.Str     => TypeTags.Str,
        IrType.Ref     => TypeTags.Object,
        IrType.Void    => TypeTags.Unknown,
        _              => TypeTags.Unknown,
    };

    internal static void InternInstrStrings(StringPool pool, IrInstr instr)
    {
        switch (instr)
        {
            case CallInstr i:       pool.Intern(i.Func); break;
            case BuiltinInstr i:    pool.Intern(i.Name); break;
            case VCallInstr i:      pool.Intern(i.Method); break;
            case FieldGetInstr i:   pool.Intern(i.FieldName); break;
            case FieldSetInstr i:   pool.Intern(i.FieldName); break;
            case ObjNewInstr i:     pool.Intern(i.ClassName); break;
            case IsInstanceInstr i: pool.Intern(i.ClassName); break;
            case AsCastInstr i:     pool.Intern(i.ClassName); break;
            case StaticGetInstr i:  pool.Intern(i.Field); break;
            case StaticSetInstr i:  pool.Intern(i.Field); break;
            case StoreInstr i:      pool.Intern(i.Var); break;
            case LoadInstr i:       pool.Intern(i.Var); break;
        }
    }

    private static int ComputeRegCount(IrFunction fn)
    {
        int max = fn.ParamCount - 1;
        void Visit(TypedReg r) { if (r.Id >= 0 && r.Id > max) max = r.Id; }
        foreach (var block in fn.Blocks)
        {
            foreach (var instr in block.Instructions) VisitInstrRegs(instr, Visit);
            VisitTermRegs(block.Terminator, Visit);
        }
        return Math.Max(max + 1, fn.ParamCount);
    }

    private static void VisitInstrRegs(IrInstr instr, Action<TypedReg> v)
    {
        switch (instr)
        {
            case ConstStrInstr i:  v(i.Dst); break;
            case ConstI32Instr i:  v(i.Dst); break;
            case ConstI64Instr i:  v(i.Dst); break;
            case ConstF64Instr i:  v(i.Dst); break;
            case ConstBoolInstr i: v(i.Dst); break;
            case ConstCharInstr i: v(i.Dst); break;
            case ConstNullInstr i: v(i.Dst); break;
            case CopyInstr i:  v(i.Dst); v(i.Src); break;
            case StoreInstr i: v(i.Src); break;
            case LoadInstr i:  v(i.Dst); break;
            case AddInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case SubInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case MulInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case DivInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case RemInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case NegInstr i:    v(i.Dst); v(i.Src); break;
            case AndInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case OrInstr  i:    v(i.Dst); v(i.A); v(i.B); break;
            case NotInstr i:    v(i.Dst); v(i.Src); break;
            case BitAndInstr i: v(i.Dst); v(i.A); v(i.B); break;
            case BitOrInstr  i: v(i.Dst); v(i.A); v(i.B); break;
            case BitXorInstr i: v(i.Dst); v(i.A); v(i.B); break;
            case BitNotInstr i: v(i.Dst); v(i.Src); break;
            case ShlInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case ShrInstr i:    v(i.Dst); v(i.A); v(i.B); break;
            case EqInstr i:     v(i.Dst); v(i.A); v(i.B); break;
            case NeInstr i:     v(i.Dst); v(i.A); v(i.B); break;
            case LtInstr i:     v(i.Dst); v(i.A); v(i.B); break;
            case LeInstr i:     v(i.Dst); v(i.A); v(i.B); break;
            case GtInstr i:     v(i.Dst); v(i.A); v(i.B); break;
            case GeInstr i:     v(i.Dst); v(i.A); v(i.B); break;
            case ToStrInstr i:     v(i.Dst); v(i.Src); break;
            case StrConcatInstr i: v(i.Dst); v(i.A); v(i.B); break;
            case CallInstr i:   v(i.Dst); foreach (var a in i.Args) v(a); break;
            case BuiltinInstr i: v(i.Dst); foreach (var a in i.Args) v(a); break;
            case VCallInstr i:  v(i.Dst); v(i.Obj); foreach (var a in i.Args) v(a); break;
            case FieldGetInstr i:  v(i.Dst); v(i.Obj); break;
            case FieldSetInstr i:  v(i.Obj); v(i.Val); break;
            case StaticGetInstr i: v(i.Dst); break;
            case StaticSetInstr i: v(i.Val); break;
            case ObjNewInstr i:    v(i.Dst); foreach (var a in i.Args) v(a); break;
            case IsInstanceInstr i: v(i.Dst); v(i.Obj); break;
            case AsCastInstr i:    v(i.Dst); v(i.Obj); break;
            case ArrayNewInstr i:    v(i.Dst); v(i.Size); break;
            case ArrayNewLitInstr i: v(i.Dst); foreach (var e in i.Elems) v(e); break;
            case ArrayGetInstr i:    v(i.Dst); v(i.Arr); v(i.Idx); break;
            case ArraySetInstr i:    v(i.Arr); v(i.Idx); v(i.Val); break;
            case ArrayLenInstr i:    v(i.Dst); v(i.Arr); break;
        }
    }

    private static void VisitTermRegs(IrTerminator term, Action<TypedReg> v)
    {
        switch (term)
        {
            case RetTerm { Reg: TypedReg r }: v(r); break;
            case BrCondTerm bc: v(bc.Cond); break;
            case ThrowTerm tt:  v(tt.Reg); break;
        }
    }
}
