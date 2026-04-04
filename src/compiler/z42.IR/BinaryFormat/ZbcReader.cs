using System.Text;

namespace Z42.IR.BinaryFormat;

/// <summary>
/// Deserializes binary zbc v0.2 data back into an <see cref="IrModule"/>.
/// Mirrors the layout written by <see cref="ZbcWriter"/>.
///
/// Section order matches the writer: NSPC first, then mode-specific sections.
/// Unknown sections are silently skipped (forward compatibility).
/// </summary>
public static class ZbcReader
{
    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a full or stripped zbc file and reconstructs the <see cref="IrModule"/>.
    /// Stripped zbc produces functions named "func#i" with unknown signatures —
    /// suitable for VM execution (dispatch via zpkg SYIX), not for TypeChecker.
    /// </summary>
    public static IrModule Read(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        // ── Header (16 bytes) ─────────────────────────────────────────────────
        var magic = r.ReadBytes(4);
        if (magic[0] != 'Z' || magic[1] != 'B' || magic[2] != 'C' || magic[3] != 0)
            throw new InvalidDataException("Not a zbc file (bad magic)");

        _ = r.ReadUInt16(); // version_major
        _ = r.ReadUInt16(); // version_minor
        var flags     = (ZbcFlags)r.ReadUInt16();
        r.ReadBytes(6);     // reserved

        bool stripped = flags.HasFlag(ZbcFlags.Stripped);

        // ── Read all sections sequentially ───────────────────────────────────
        string   nspc      = string.Empty;
        string[] pool      = [];
        var      classes   = new List<IrClassDesc>();
        var      sigs      = new List<(string name, ushort paramCount, string retType, string execMode)>();
        var      funcBodies = new List<(int regCount, List<IrBlock> blocks, List<IrExceptionEntry>? excTable)>();

        while (ms.Position < ms.Length)
        {
            var tag    = r.ReadBytes(4);
            uint len   = r.ReadUInt32();
            var  sec   = r.ReadBytes((int)len);

            if (SectionTags.Equals(tag, SectionTags.Nspc))
                nspc = ReadNspcSection(sec);
            else if (SectionTags.Equals(tag, SectionTags.Strs) ||
                     SectionTags.Equals(tag, SectionTags.Bstr))
                pool = ReadStrsSection(sec);
            else if (SectionTags.Equals(tag, SectionTags.Type))
                classes = ReadTypeSection(sec, pool);
            else if (SectionTags.Equals(tag, SectionTags.Sigs))
                sigs = ReadSigsSection(sec, pool);
            else if (SectionTags.Equals(tag, SectionTags.Func))
                funcBodies = ReadFuncSection(sec, pool);
            // IMPT, EXPT, DBUG — skip (not needed for module reconstruction)
        }

        // ── Reconstruct functions ─────────────────────────────────────────────
        var functions = new List<IrFunction>(funcBodies.Count);
        for (int i = 0; i < funcBodies.Count; i++)
        {
            var (regCount, blocks, excTable) = funcBodies[i];
            string name, retType, execMode;
            ushort paramCount;

            if (i < sigs.Count)
            {
                (name, paramCount, retType, execMode) = sigs[i];
            }
            else
            {
                // Stripped mode: no SIGS section
                name       = $"func#{i}";
                paramCount = 0;
                retType    = "void";
                execMode   = "Interp";
            }

            functions.Add(new IrFunction(name, paramCount, retType, execMode, blocks,
                excTable?.Count > 0 ? excTable : null));
        }

        string moduleName = nspc.Length > 0 ? nspc : "unknown";
        return new IrModule(moduleName, BuildStringPool(pool, functions), classes, functions);
    }

    /// <summary>
    /// Reads only the NSPC section from a zbc file.
    /// Performs minimal IO — reads header (16 bytes) then first section tag/length/data.
    /// Returns empty string if the file is malformed or has no NSPC section.
    /// </summary>
    public static string ReadNamespace(byte[] data)
    {
        if (data.Length < 16 + 8) return string.Empty;

        using var ms = new MemoryStream(data, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        var magic = r.ReadBytes(4);
        if (magic[0] != 'Z' || magic[1] != 'B' || magic[2] != 'C' || magic[3] != 0)
            return string.Empty;

        r.ReadBytes(12); // skip rest of header (version + flags + reserved)

        // First section must be NSPC
        var tag  = r.ReadBytes(4);
        uint len = r.ReadUInt32();
        if (!SectionTags.Equals(tag, SectionTags.Nspc)) return string.Empty;

        return ReadNspcSection(r.ReadBytes((int)len));
    }

    /// <summary>
    /// Reads the ZbcFlags from a zbc file header without parsing sections.
    /// </summary>
    public static ZbcFlags ReadFlags(byte[] data)
    {
        if (data.Length < 10) return ZbcFlags.None;
        return (ZbcFlags)BitConverter.ToUInt16(data, 8);
    }

    // ── NSPC section ──────────────────────────────────────────────────────────

    private static string ReadNspcSection(byte[] data)
    {
        if (data.Length < 2) return string.Empty;
        ushort len = BitConverter.ToUInt16(data, 0);
        if (len == 0 || data.Length < 2 + len) return string.Empty;
        return Encoding.UTF8.GetString(data, 2, len);
    }

    // ── STRS / BSTR section ───────────────────────────────────────────────────

    private static string[] ReadStrsSection(byte[] data)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        uint count  = r.ReadUInt32();
        var offsets = new (uint off, uint len)[count];
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

    // ── SIGS section ──────────────────────────────────────────────────────────

    private static List<(string, ushort, string, string)> ReadSigsSection(byte[] data, string[] pool)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        uint count = r.ReadUInt32();
        var result = new List<(string, ushort, string, string)>((int)count);

        for (int i = 0; i < count; i++)
        {
            string name       = P(pool, r.ReadUInt32());
            ushort paramCount = r.ReadUInt16();
            string retType    = TypeTags.ToIrString(r.ReadByte());
            string execMode   = ExecModes.ToIrString(r.ReadByte());
            result.Add((name, paramCount, retType, execMode));
        }
        return result;
    }

    // ── FUNC section (bodies only) ────────────────────────────────────────────

    private static List<(int, List<IrBlock>, List<IrExceptionEntry>?)> ReadFuncSection(
        byte[] data, string[] pool)
    {
        using var ms   = new MemoryStream(data, writable: false);
        using var r    = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        uint funcCount = r.ReadUInt32();
        var result     = new List<(int, List<IrBlock>, List<IrExceptionEntry>?)>((int)funcCount);

        for (int fi = 0; fi < funcCount; fi++)
        {
            int    regCount   = r.ReadUInt16();
            ushort blockCount = r.ReadUInt16();
            uint   instrLen   = r.ReadUInt32();
            ushort excCount   = r.ReadUInt16();

            var blockOffsets = new uint[blockCount];
            for (int i = 0; i < blockCount; i++) blockOffsets[i] = r.ReadUInt32();

            var excTable = new List<IrExceptionEntry>(excCount);
            for (int i = 0; i < excCount; i++)
            {
                ushort tryS   = r.ReadUInt16();
                ushort tryE   = r.ReadUInt16();
                ushort catchB = r.ReadUInt16();
                uint catchT   = r.ReadUInt32();
                ushort catchR = r.ReadUInt16();
                excTable.Add(new IrExceptionEntry(
                    BL(tryS),
                    tryE < blockCount ? BL(tryE) : $"block_{blockCount}",
                    BL(catchB),
                    catchT == uint.MaxValue ? null : P(pool, catchT),
                    catchR));
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

            // Remap placeholder exception labels to resolved block labels
            List<IrExceptionEntry>? resolvedExc = null;
            if (excTable.Count > 0)
            {
                resolvedExc = excTable
                    .Select(e => new IrExceptionEntry(
                        Resolve(e.TryStart, blocks), Resolve(e.TryEnd, blocks),
                        Resolve(e.CatchLabel, blocks), e.CatchType, e.CatchReg))
                    .ToList();
            }

            result.Add((regCount, blocks, resolvedExc));
        }
        return result;
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

    private static IrInstr DecodeInstr(byte op, byte typ, int dst, BinaryReader r, string[] pool)
    {
        switch (op)
        {
            case Opcodes.ConstStr:  return new ConstStrInstr(dst, (int)r.ReadUInt32());
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
            case Opcodes.Load: return new LoadInstr(dst, P(pool, r.ReadUInt32()));

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

    // ── String pool reconstruction ────────────────────────────────────────────

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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string P(string[] pool, uint idx) =>
        idx < pool.Length ? pool[idx] : $"<str#{idx}>";

    private static string BL(ushort idx) => idx == 0 ? "entry" : $"block_{idx}";

    private static List<int> ReadArgs(BinaryReader r)
    {
        int count = r.ReadByte();
        var args  = new List<int>(count);
        for (int i = 0; i < count; i++) args.Add(r.ReadUInt16());
        return args;
    }
}
