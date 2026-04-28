using System.Text;

namespace Z42.IR.BinaryFormat;

/// Instruction decoding and helpers — part of ZbcReader.
public static partial class ZbcReader
{
    // ── Block decoding ────────────────────────────────────────────────────────

    private static (List<IrInstr>, IrTerminator) DecodeBlock(
        byte[] data, int start, int end, string[] pool)
    {
        var instrs = new List<IrInstr>();
        using var ms = new MemoryStream(data, start, end - start, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        while (ms.Position < ms.Length)
        {
            byte op  = r.ReadByte();
            byte typ = r.ReadByte();
            int  dst = r.ReadUInt16();

            if (op is Opcodes.Ret or Opcodes.RetVal or Opcodes.Br or Opcodes.BrCond or Opcodes.Throw)
                return (instrs, DecodeTerm(op, typ, dst, r));

            instrs.Add(DecodeInstr(op, typ, dst, r, pool));
        }
        return (instrs, new RetTerm(null));
    }

    private static IrTerminator DecodeTerm(byte op, byte typ, int dstOrCond, BinaryReader r) => op switch
    {
        Opcodes.Ret    => new RetTerm(null),
        Opcodes.RetVal => new RetTerm(R(dstOrCond, typ)),
        Opcodes.Br     => new BrTerm(BL(r.ReadUInt16())),
        Opcodes.BrCond => new BrCondTerm(R(dstOrCond, typ), BL(r.ReadUInt16()), BL(r.ReadUInt16())),
        Opcodes.Throw  => new ThrowTerm(R(dstOrCond, typ)),
        _              => throw new InvalidDataException($"Not a terminator: 0x{op:X2}"),
    };

    private static IrInstr DecodeInstr(byte op, byte typ, int dst, BinaryReader r, string[] pool)
    {
        var d = R(dst, typ);   // typed destination register

        switch (op)
        {
            case Opcodes.ConstStr:  return new ConstStrInstr(d, (int)r.ReadUInt32());
            case Opcodes.ConstI when typ == TypeTags.I64:
                                    return new ConstI64Instr(d, r.ReadInt64());
            case Opcodes.ConstI:    return new ConstI32Instr(d, r.ReadInt32());
            case Opcodes.ConstF:    return new ConstF64Instr(d, r.ReadDouble());
            case Opcodes.ConstBool: return new ConstBoolInstr(d, r.ReadByte() != 0);
            case Opcodes.ConstChar: return new ConstCharInstr(d, (char)r.ReadInt32());
            case Opcodes.ConstNull: return new ConstNullInstr(d);
            case Opcodes.Copy:      return new CopyInstr(d, RU(r.ReadUInt16()));

            case Opcodes.Add:    return new AddInstr   (d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.Sub:    return new SubInstr   (d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.Mul:    return new MulInstr   (d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.Div:    return new DivInstr   (d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.Rem:    return new RemInstr   (d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.Neg:    return new NegInstr   (d, RU(r.ReadUInt16()));
            case Opcodes.And:    return new AndInstr   (d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.Or:     return new OrInstr    (d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.Not:    return new NotInstr   (d, RU(r.ReadUInt16()));
            case Opcodes.BitAnd: return new BitAndInstr(d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.BitOr:  return new BitOrInstr (d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.BitXor: return new BitXorInstr(d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.BitNot: return new BitNotInstr(d, RU(r.ReadUInt16()));
            case Opcodes.Shl:    return new ShlInstr   (d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.Shr:    return new ShrInstr   (d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.ToStr:  return new ToStrInstr (d, RU(r.ReadUInt16()));
            case Opcodes.Eq:     return new EqInstr(d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.Ne:     return new NeInstr(d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.Lt:     return new LtInstr(d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.Le:     return new LeInstr(d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.Gt:     return new GtInstr(d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.Ge:     return new GeInstr(d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.Call:
            {
                var fn   = P(pool, r.ReadUInt32());
                var args = ReadArgs(r);
                return new CallInstr(d, fn, args);
            }
            case Opcodes.Builtin:
            {
                var name = P(pool, r.ReadUInt32());
                var args = ReadArgs(r);
                return new BuiltinInstr(d, name, args);
            }
            case Opcodes.VCall:
            {
                var method = P(pool, r.ReadUInt32());
                var obj    = RU(r.ReadUInt16());
                var args   = ReadArgs(r);
                return new VCallInstr(d, obj, method, args);
            }
            case Opcodes.FieldGet:
            {
                var obj   = RU(r.ReadUInt16());
                var field = P(pool, r.ReadUInt32());
                return new FieldGetInstr(d, obj, field);
            }
            case Opcodes.FieldSet:
            {
                var obj   = RU(r.ReadUInt16());
                var field = P(pool, r.ReadUInt32());
                var val   = RU(r.ReadUInt16());
                return new FieldSetInstr(obj, field, val);
            }
            case Opcodes.StaticGet: return new StaticGetInstr(d, P(pool, r.ReadUInt32()));
            case Opcodes.StaticSet:
            {
                var field = P(pool, r.ReadUInt32());
                var val   = RU(r.ReadUInt16());
                return new StaticSetInstr(field, val);
            }
            case Opcodes.ObjNew:
            {
                var cls  = P(pool, r.ReadUInt32());
                var ctor = P(pool, r.ReadUInt32());
                var args = ReadArgs(r);
                return new ObjNewInstr(d, cls, ctor, args);
            }
            case Opcodes.IsInstance:
            {
                var obj = RU(r.ReadUInt16());
                var cls = P(pool, r.ReadUInt32());
                return new IsInstanceInstr(d, obj, cls);
            }
            case Opcodes.AsCast:
            {
                var obj = RU(r.ReadUInt16());
                var cls = P(pool, r.ReadUInt32());
                return new AsCastInstr(d, obj, cls);
            }
            case Opcodes.ArrayNew:    return new ArrayNewInstr(d, RU(r.ReadUInt16()));
            case Opcodes.ArrayNewLit: return new ArrayNewLitInstr(d, ReadArgs(r));
            case Opcodes.ArrayGet:    return new ArrayGetInstr(d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));
            case Opcodes.ArraySet:
            {
                var arr = RU(r.ReadUInt16()); var idx = RU(r.ReadUInt16()); var val = RU(r.ReadUInt16());
                return new ArraySetInstr(arr, idx, val);
            }
            case Opcodes.ArrayLen:  return new ArrayLenInstr(d, RU(r.ReadUInt16()));
            case Opcodes.StrConcat: return new StrConcatInstr(d, RU(r.ReadUInt16()), RU(r.ReadUInt16()));

            case Opcodes.CallNative:
            {
                var module = P(pool, r.ReadUInt32());
                var type   = P(pool, r.ReadUInt32());
                var symbol = P(pool, r.ReadUInt32());
                var args   = ReadArgs(r);
                return new CallNativeInstr(d, module, type, symbol, args);
            }
            case Opcodes.CallNativeVtable:
            {
                var recv = RU(r.ReadUInt16());
                var slot = r.ReadUInt16();
                var args = ReadArgs(r);
                return new CallNativeVtableInstr(d, recv, slot, args);
            }
            case Opcodes.PinPtr:
                return new PinPtrInstr(d, RU(r.ReadUInt16()));
            case Opcodes.UnpinPtr:
                return new UnpinPtrInstr(RU(r.ReadUInt16()));

            default:
                throw new InvalidDataException($"ZbcReader: unknown opcode 0x{op:X2}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string P(string[] pool, uint idx) =>
        idx < pool.Length ? pool[idx] : $"<str#{idx}>";

    private static string BL(ushort idx) => idx == 0 ? "entry" : $"block_{idx}";

    private static TypedReg R(int id, byte typeTag) => new(id, IrTypeFromTag(typeTag));
    private static TypedReg RU(int id) => new(id, IrType.Unknown);

    private static List<TypedReg> ReadArgs(BinaryReader r)
    {
        int count = r.ReadByte();
        var args  = new List<TypedReg>(count);
        for (int i = 0; i < count; i++) args.Add(RU(r.ReadUInt16()));
        return args;
    }

    private static IrType IrTypeFromTag(byte tag) => tag switch
    {
        TypeTags.Unknown => IrType.Unknown,
        TypeTags.Bool    => IrType.Bool,
        TypeTags.I8      => IrType.I8,
        TypeTags.I16     => IrType.I16,
        TypeTags.I32     => IrType.I32,
        TypeTags.I64     => IrType.I64,
        TypeTags.U8      => IrType.U8,
        TypeTags.U16     => IrType.U16,
        TypeTags.U32     => IrType.U32,
        TypeTags.U64     => IrType.U64,
        TypeTags.F32     => IrType.F32,
        TypeTags.F64     => IrType.F64,
        TypeTags.Char    => IrType.Char,
        TypeTags.Str     => IrType.Str,
        TypeTags.Object  => IrType.Ref,
        TypeTags.Array   => IrType.Ref,
        _                => IrType.Unknown,
    };
}
