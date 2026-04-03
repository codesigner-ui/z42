using System.Text;

namespace Z42.IR.BinaryFormat;

/// <summary>
/// Deserializes binary z42bc data back into an <see cref="IrModule"/>.
/// Mirrors the layout written by <see cref="ZbcWriter"/>.
/// </summary>
public static class ZbcReader
{
    // ── Public API ─────────────────────────────────────────────────────────────

    public static IrModule Read(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        // Header (16 bytes)
        var magic = r.ReadBytes(4);
        if (magic[0] != 'Z' || magic[1] != 'B' || magic[2] != 'C' || magic[3] != 0)
            throw new InvalidDataException("Not a z42bc file (bad magic)");

        _ = r.ReadByte();    // version_major
        _ = r.ReadByte();    // version_minor
        _ = r.ReadUInt16();  // flags
        uint nameIdx  = r.ReadUInt32();
        uint secCount = r.ReadUInt32();

        // Section directory
        var sections = new (byte[] tag, uint offset, uint size)[secCount];
        for (int i = 0; i < secCount; i++)
            sections[i] = (r.ReadBytes(4), r.ReadUInt32(), r.ReadUInt32() + 0 * r.ReadUInt32());
        // Re-read properly (each entry is 16 bytes: tag(4)+offset(4)+size(4)+flags(4))
        // The loop above consumed flags in size via the hack — do it correctly:
        ms.Position = 16; // reset to after header
        for (int i = 0; i < secCount; i++)
        {
            var tag    = r.ReadBytes(4);
            var offset = r.ReadUInt32();
            var size   = r.ReadUInt32();
            _          = r.ReadUInt32(); // section flags
            sections[i] = (tag, offset, size);
        }

        // Load sections
        string[] pool  = [];
        var classes    = new List<IrClassDesc>();
        var functions  = new List<IrFunction>();

        foreach (var (tag, offset, size) in sections)
        {
            var sec = new byte[size];
            Array.Copy(data, (int)offset, sec, 0, (int)size);

            if (SectionTags.Equals(tag, SectionTags.Strp))
                pool = ReadStrpSection(sec);
            else if (SectionTags.Equals(tag, SectionTags.Type))
                classes = ReadTypeSection(sec, pool);
            else if (SectionTags.Equals(tag, SectionTags.Func))
                functions = ReadFuncSection(sec, pool);
        }

        string moduleName = nameIdx < pool.Length ? pool[nameIdx] : "unknown";
        return new IrModule(moduleName, BuildStringPool(pool, functions), classes, functions);
    }

    // ── Rebuild IrModule.StringPool ────────────────────────────────────────────

    private static List<string> BuildStringPool(string[] unifiedPool, List<IrFunction> fns)
    {
        var result = new List<string>();
        var seen   = new HashSet<int>();

        foreach (var fn in fns)
            foreach (var block in fn.Blocks)
                foreach (var instr in block.Instructions)
                    if (instr is ConstStrInstr cs && seen.Add(cs.Idx))
                        result.Add(cs.Idx < unifiedPool.Length ? unifiedPool[cs.Idx] : "");

        return result;
    }

    // ── STRP section ──────────────────────────────────────────────────────────

    private static string[] ReadStrpSection(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        uint count    = r.ReadUInt32();
        var offsets   = new (uint off, uint len)[count];
        for (int i = 0; i < count; i++)
            offsets[i] = (r.ReadUInt32(), r.ReadUInt32());

        long dataStart = ms.Position;
        var result     = new string[count];
        for (int i = 0; i < count; i++)
        {
            ms.Position = dataStart + offsets[i].off;
            result[i]   = Encoding.UTF8.GetString(r.ReadBytes((int)offsets[i].len));
        }
        return result;
    }

    // ── TYPE section ──────────────────────────────────────────────────────────

    private static List<IrClassDesc> ReadTypeSection(byte[] data, string[] pool)
    {
        using var ms  = new MemoryStream(data, writable: false);
        using var r   = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        uint count    = r.ReadUInt32();
        var classes   = new List<IrClassDesc>((int)count);

        for (int i = 0; i < count; i++)
        {
            string name     = P(pool, r.ReadUInt32());
            uint baseRaw    = r.ReadUInt32();
            string? baseCls = baseRaw == uint.MaxValue ? null : P(pool, baseRaw);
            ushort fldCount = r.ReadUInt16();
            var fields      = new List<IrFieldDesc>(fldCount);
            for (int f = 0; f < fldCount; f++)
                fields.Add(new IrFieldDesc(P(pool, r.ReadUInt32()), TypeTags.ToIrString(r.ReadByte())));
            classes.Add(new IrClassDesc(name, baseCls, fields));
        }
        return classes;
    }

    // ── FUNC section ──────────────────────────────────────────────────────────

    private static List<IrFunction> ReadFuncSection(byte[] data, string[] pool)
    {
        using var ms       = new MemoryStream(data, writable: false);
        using var r        = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        uint funcCount     = r.ReadUInt32();
        var functions      = new List<IrFunction>((int)funcCount);

        for (int fi = 0; fi < funcCount; fi++)
        {
            string name       = P(pool, r.ReadUInt32());
            _                 = r.ReadByte();    // fn flags
            byte execByte     = r.ReadByte();
            ushort paramCount = r.ReadUInt16();
            _                 = r.ReadUInt16();  // reg_count (not stored in IrFunction)
            ushort blockCount = r.ReadUInt16();
            uint instrLen     = r.ReadUInt32();
            ushort excCount   = r.ReadUInt16();

            var blockOffsets  = new uint[blockCount];
            for (int i = 0; i < blockCount; i++) blockOffsets[i] = r.ReadUInt32();

            var excTable = new List<IrExceptionEntry>(excCount);
            for (int i = 0; i < excCount; i++)
            {
                ushort tryS  = r.ReadUInt16();
                ushort tryE  = r.ReadUInt16();
                ushort catchB = r.ReadUInt16();
                uint catchT  = r.ReadUInt32();
                ushort catchR = r.ReadUInt16();
                excTable.Add(new IrExceptionEntry(
                    BL(tryS), tryE < blockCount ? BL(tryE) : $"block_{blockCount}",
                    BL(catchB), catchT == uint.MaxValue ? null : P(pool, catchT), catchR));
            }

            byte[] instrBytes = r.ReadBytes((int)instrLen);

            var blocks = new List<IrBlock>(blockCount);
            for (int bi = 0; bi < blockCount; bi++)
            {
                int start = (int)blockOffsets[bi];
                int end   = bi + 1 < blockCount ? (int)blockOffsets[bi + 1] : (int)instrLen;
                var (instrs, term) = DecodeBlock(instrBytes, start, end, pool);
                blocks.Add(new IrBlock(bi == 0 ? "entry" : $"block_{bi}", instrs, term));
            }

            // Remap exception table labels to resolved block labels
            List<IrExceptionEntry>? resolvedExc = null;
            if (excTable.Count > 0)
            {
                resolvedExc = excTable
                    .Select(e => new IrExceptionEntry(
                        Resolve(e.TryStart, blocks), Resolve(e.TryEnd, blocks),
                        Resolve(e.CatchLabel, blocks), e.CatchType, e.CatchReg))
                    .ToList();
            }

            functions.Add(new IrFunction(name, paramCount, "void",
                ExecModes.ToIrString(execByte), blocks,
                resolvedExc?.Count > 0 ? resolvedExc : null));
        }
        return functions;
    }

    private static string Resolve(string raw, List<IrBlock> blocks)
    {
        if (raw.StartsWith("block_") && int.TryParse(raw[6..], out int idx))
            return idx < blocks.Count ? blocks[idx].Label : raw;
        return raw;
    }

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
                return (instrs, DecodeTerm(op, dst, r));

            instrs.Add(DecodeInstr(op, typ, dst, r, pool));
        }
        return (instrs, new RetTerm(null));
    }

    private static IrTerminator DecodeTerm(byte op, int dstOrCond, BinaryReader r) => op switch
    {
        Opcodes.Ret    => new RetTerm(null),
        Opcodes.RetVal => new RetTerm(dstOrCond),
        Opcodes.Br     => new BrTerm(BL(r.ReadUInt16())),
        Opcodes.BrCond => new BrCondTerm(dstOrCond, BL(r.ReadUInt16()), BL(r.ReadUInt16())),
        Opcodes.Throw  => new ThrowTerm(dstOrCond),
        _              => throw new InvalidDataException($"Not a terminator: 0x{op:X2}"),
    };

    // Instruction decoder — uses separate helper methods for multi-field instructions
    // to guarantee left-to-right read order (switch expression arms don't guarantee order).
    private static IrInstr DecodeInstr(byte op, byte typ, int dst, BinaryReader r, string[] pool)
    {
        switch (op)
        {
            case Opcodes.ConstStr:  return new ConstStrInstr (dst, (int)r.ReadUInt32());
            case Opcodes.ConstI when typ == TypeTags.I64:
                                    return new ConstI64Instr(dst, r.ReadInt64());
            case Opcodes.ConstI:    return new ConstI32Instr(dst, r.ReadInt32());
            case Opcodes.ConstF:    return new ConstF64Instr(dst, r.ReadDouble());
            case Opcodes.ConstBool: return new ConstBoolInstr(dst, r.ReadByte() != 0);
            case Opcodes.ConstNull: return new ConstNullInstr(dst);
            case Opcodes.Copy:      return new CopyInstr(dst, r.ReadUInt16());

            case Opcodes.Store:
            {
                var varName = P(pool, r.ReadUInt32());
                var src     = r.ReadUInt16();
                return new StoreInstr(varName, src);
            }
            case Opcodes.Load:      return new LoadInstr(dst, P(pool, r.ReadUInt32()));

            case Opcodes.Add:    return new AddInstr   (dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.Sub:    return new SubInstr   (dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.Mul:    return new MulInstr   (dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.Div:    return new DivInstr   (dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.Rem:    return new RemInstr   (dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.Neg:    return new NegInstr   (dst, r.ReadUInt16());
            case Opcodes.And:    return new AndInstr   (dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.Or:     return new OrInstr    (dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.Not:    return new NotInstr   (dst, r.ReadUInt16());
            case Opcodes.BitAnd: return new BitAndInstr(dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.BitOr:  return new BitOrInstr (dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.BitXor: return new BitXorInstr(dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.BitNot: return new BitNotInstr(dst, r.ReadUInt16());
            case Opcodes.Shl:    return new ShlInstr   (dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.Shr:    return new ShrInstr   (dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.ToStr:  return new ToStrInstr (dst, r.ReadUInt16());

            case Opcodes.Eq:     return new EqInstr(dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.Ne:     return new NeInstr(dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.Lt:     return new LtInstr(dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.Le:     return new LeInstr(dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.Gt:     return new GtInstr(dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.Ge:     return new GeInstr(dst, r.ReadUInt16(), r.ReadUInt16());

            case Opcodes.Call:
            {
                var fn   = P(pool, r.ReadUInt32());
                var args = ReadArgs(r);
                return new CallInstr(dst, fn, args);
            }
            case Opcodes.Builtin:
            {
                var name = P(pool, r.ReadUInt32());
                var args = ReadArgs(r);
                return new BuiltinInstr(dst, name, args);
            }
            case Opcodes.VCall:
            {
                var method = P(pool, r.ReadUInt32());
                var obj    = r.ReadUInt16();
                var args   = ReadArgs(r);
                return new VCallInstr(dst, obj, method, args);
            }

            case Opcodes.FieldGet:
            {
                var obj   = r.ReadUInt16();
                var field = P(pool, r.ReadUInt32());
                return new FieldGetInstr(dst, obj, field);
            }
            case Opcodes.FieldSet:
            {
                var obj   = r.ReadUInt16();
                var field = P(pool, r.ReadUInt32());
                var val   = r.ReadUInt16();
                return new FieldSetInstr(obj, field, val);
            }
            case Opcodes.StaticGet: return new StaticGetInstr(dst, P(pool, r.ReadUInt32()));
            case Opcodes.StaticSet:
            {
                var field = P(pool, r.ReadUInt32());
                var val   = r.ReadUInt16();
                return new StaticSetInstr(field, val);
            }

            case Opcodes.ObjNew:
            {
                var cls  = P(pool, r.ReadUInt32());
                var args = ReadArgs(r);
                return new ObjNewInstr(dst, cls, args);
            }
            case Opcodes.IsInstance:
            {
                var obj = r.ReadUInt16();
                var cls = P(pool, r.ReadUInt32());
                return new IsInstanceInstr(dst, obj, cls);
            }
            case Opcodes.AsCast:
            {
                var obj = r.ReadUInt16();
                var cls = P(pool, r.ReadUInt32());
                return new AsCastInstr(dst, obj, cls);
            }

            case Opcodes.ArrayNew:    return new ArrayNewInstr(dst, r.ReadUInt16());
            case Opcodes.ArrayNewLit: return new ArrayNewLitInstr(dst, ReadArgs(r));
            case Opcodes.ArrayGet:    return new ArrayGetInstr(dst, r.ReadUInt16(), r.ReadUInt16());
            case Opcodes.ArraySet:
            {
                var arr = r.ReadUInt16(); var idx = r.ReadUInt16(); var val = r.ReadUInt16();
                return new ArraySetInstr(arr, idx, val);
            }
            case Opcodes.ArrayLen:  return new ArrayLenInstr(dst, r.ReadUInt16());
            case Opcodes.StrConcat: return new StrConcatInstr(dst, r.ReadUInt16(), r.ReadUInt16());

            default:
                throw new InvalidDataException($"ZbcReader: unknown opcode 0x{op:X2}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// Pool string lookup (short alias for brevity).
    private static string P(string[] pool, uint idx) =>
        idx < pool.Length ? pool[idx] : $"<str#{idx}>";

    /// Block label from index.
    private static string BL(ushort idx) => idx == 0 ? "entry" : $"block_{idx}";

    private static List<int> ReadArgs(BinaryReader r)
    {
        int count = r.ReadByte();
        var args  = new List<int>(count);
        for (int i = 0; i < count; i++) args.Add(r.ReadUInt16());
        return args;
    }
}
