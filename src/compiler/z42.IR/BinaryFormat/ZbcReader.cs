using System.Text;

namespace Z42.IR.BinaryFormat;

/// <summary>
/// Deserializes binary zbc v0.3 data back into an <see cref="IrModule"/>.
/// Mirrors the layout written by <see cref="ZbcWriter"/>.
///
/// v0.3: header[16] + section directory[sec_count × 12] + sections at absolute offsets.
/// Sections are accessed via the directory (random access, O(1) per section).
/// </summary>
public static class ZbcReader
{
    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a full or stripped zbc file and reconstructs the <see cref="IrModule"/>.
    /// </summary>
    public static IrModule Read(byte[] data)
    {
        ParseHeader(data, out _, out ushort minor, out var flags, out ushort secCount);
        bool stripped = flags.HasFlag(ZbcFlags.Stripped);

        var dir = ReadDirectory(data, minor, secCount);

        string   nspc      = ReadSection(data, dir, SectionTags.Nspc,
                                         ReadNspcSection, string.Empty);
        string[] pool      = ReadSection(data, dir, SectionTags.Strs,
                                         ReadStrsSection,
                             ReadSection(data, dir, SectionTags.Bstr,
                                         ReadStrsSection, []));
        var classes        = ReadSection(data, dir, SectionTags.Type,
                                         sec => ReadTypeSection(sec, pool),
                                         new List<IrClassDesc>());
        var sigs           = ReadSection(data, dir, SectionTags.Sigs,
                                         sec => ReadSigsSection(sec, pool),
                                         new List<(string, ushort, string, string, bool)>());
        var funcBodies     = ReadSection(data, dir, SectionTags.Func,
                                         sec => ReadFuncSection(sec, pool),
                                         new List<(int, List<IrBlock>, List<IrExceptionEntry>?)>());

        // ── Reconstruct functions ─────────────────────────────────────────────
        var functions = new List<IrFunction>(funcBodies.Count);
        for (int i = 0; i < funcBodies.Count; i++)
        {
            var (regCount, blocks, excTable) = funcBodies[i];
            string name, retType, execMode;
            ushort paramCount;
            bool   isStatic;

            if (i < sigs.Count)
                (name, paramCount, retType, execMode, isStatic) = sigs[i];
            else
            {
                name = $"func#{i}"; paramCount = 0; retType = "void"; execMode = "Interp"; isStatic = false;
            }

            functions.Add(new IrFunction(name, paramCount, retType, execMode, blocks,
                excTable?.Count > 0 ? excTable : null, IsStatic: isStatic));
        }

        string moduleName = nspc.Length > 0 ? nspc : "unknown";
        return new IrModule(moduleName, BuildStringPool(pool, functions), classes, functions);
    }

    /// <summary>
    /// Reads only the NSPC section from a zbc file (fast path for namespace scanning).
    /// </summary>
    public static string ReadNamespace(byte[] data)
    {
        if (data.Length < 16 + 8) return string.Empty;
        ParseHeader(data, out _, out ushort minor, out _, out ushort secCount);
        var dir = ReadDirectory(data, minor, secCount);
        return ReadSection(data, dir, SectionTags.Nspc, ReadNspcSection, string.Empty);
    }

    /// <summary>Reads the ZbcFlags from a zbc file header.</summary>
    public static ZbcFlags ReadFlags(byte[] data)
    {
        if (data.Length < 10) return ZbcFlags.None;
        return (ZbcFlags)BitConverter.ToUInt16(data, 8);
    }

    // ── Header + Directory ────────────────────────────────────────────────────

    private static void ParseHeader(
        byte[] data, out ushort major, out ushort minor,
        out ZbcFlags flags, out ushort secCount)
    {
        if (data.Length < 16)
            throw new InvalidDataException("zbc file too short");
        if (data[0] != 'Z' || data[1] != 'B' || data[2] != 'C' || data[3] != 0)
            throw new InvalidDataException("Not a zbc file (bad magic)");

        major    = BitConverter.ToUInt16(data, 4);
        minor    = BitConverter.ToUInt16(data, 6);
        flags    = (ZbcFlags)BitConverter.ToUInt16(data, 8);
        secCount = BitConverter.ToUInt16(data, 10); // sec_count (was reserved[2] in v0.2)
    }

    /// Builds tag → (absoluteOffset, size) map from the section directory.
    /// For files without a directory (secCount == 0) falls back to sequential scan.
    private static Dictionary<string, (int Offset, int Size)> ReadDirectory(
        byte[] data, ushort minor, ushort secCount)
    {
        var dir = new Dictionary<string, (int, int)>(StringComparer.Ordinal);

        if (secCount == 0)
        {
            // Legacy sequential scan (no directory)
            int pos = 16;
            while (pos + 8 <= data.Length)
            {
                string tag = Encoding.ASCII.GetString(data, pos, 4);
                int    len = (int)BitConverter.ToUInt32(data, pos + 4);
                dir[tag]   = (pos + 8, len);
                pos       += 8 + len;
            }
        }
        else
        {
            // v0.3 directory
            int pos = 16;
            for (int i = 0; i < secCount; i++)
            {
                if (pos + 12 > data.Length) break;
                string tag    = Encoding.ASCII.GetString(data, pos, 4);
                int    offset = (int)BitConverter.ToUInt32(data, pos + 4);
                int    size   = (int)BitConverter.ToUInt32(data, pos + 8);
                dir[tag]      = (offset, size);
                pos          += 12;
            }
        }

        return dir;
    }

    private static T ReadSection<T>(
        byte[] data,
        Dictionary<string, (int Offset, int Size)> dir,
        byte[] tagBytes,
        Func<byte[], T> reader,
        T defaultValue)
    {
        string tag = Encoding.ASCII.GetString(tagBytes);
        if (!dir.TryGetValue(tag, out var entry)) return defaultValue;
        var sec = new byte[entry.Size];
        Array.Copy(data, entry.Offset, sec, 0, entry.Size);
        return reader(sec);
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
        using var ms = new MemoryStream(data, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        uint count   = r.ReadUInt32();
        var classes  = new List<IrClassDesc>((int)count);

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

    private static List<(string, ushort, string, string, bool)> ReadSigsSection(byte[] data, string[] pool)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        uint count = r.ReadUInt32();
        var result = new List<(string, ushort, string, string, bool)>((int)count);

        for (int i = 0; i < count; i++)
        {
            string name       = P(pool, r.ReadUInt32());
            ushort paramCount = r.ReadUInt16();
            string retType    = TypeTags.ToIrString(r.ReadByte());
            string execMode   = ExecModes.ToIrString(r.ReadByte());
            bool   isStatic   = r.ReadByte() != 0;
            result.Add((name, paramCount, retType, execMode, isStatic));
        }
        return result;
    }

    // ── FUNC section ─────────────────────────────────────────────────────────

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
                    RU(catchR)));
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
            case Opcodes.Store:
            {
                var varName = P(pool, r.ReadUInt32());
                var src     = RU(r.ReadUInt16());
                return new StoreInstr(varName, src);
            }
            case Opcodes.Load: return new LoadInstr(d, P(pool, r.ReadUInt32()));

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
                var args = ReadArgs(r);
                return new ObjNewInstr(d, cls, args);
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

            default:
                throw new InvalidDataException($"ZbcReader: unknown opcode 0x{op:X2}");
        }
    }

    // ── Internal helpers for ZpkgReader ──────────────────────────────────────

    /// Exposes FUNC section decoding for ZpkgReader (packed mode module bodies).
    public static List<(int, List<IrBlock>, List<IrExceptionEntry>?)> DecodeFuncSectionPublic(
        byte[] data, string[] pool) => ReadFuncSection(data, pool);

    /// Exposes TYPE section decoding for ZpkgReader.
    public static List<IrClassDesc> DecodeTypeSectionPublic(
        byte[] data, string[] pool) => ReadTypeSection(data, pool);

    /// Exposes string pool rebuild + remap for ZpkgReader.
    public static List<string> RebuildStringPoolPublic(string[] globalPool, List<IrFunction> fns)
        => BuildStringPool(globalPool, fns);

    // ── String pool reconstruction ────────────────────────────────────────────

    /// Rebuilds the module string pool from only the strings actually used by ConstStr instructions.
    /// Also remaps ConstStr.Idx from global pool indices to rebuilt local indices.
    private static List<string> BuildStringPool(string[] globalPool, List<IrFunction> fns)
    {
        var result  = new List<string>();
        var seen    = new Dictionary<int, int>();   // globalIdx → localIdx

        foreach (var fn in fns)
            foreach (var block in fn.Blocks)
                foreach (var instr in block.Instructions)
                    if (instr is ConstStrInstr cs && !seen.ContainsKey(cs.Idx))
                    {
                        seen[cs.Idx] = result.Count;
                        result.Add(cs.Idx < globalPool.Length ? globalPool[cs.Idx] : "");
                    }

        // Remap ConstStr indices in-place
        foreach (var fn in fns)
            foreach (var block in fn.Blocks)
                for (int i = 0; i < block.Instructions.Count; i++)
                    if (block.Instructions[i] is ConstStrInstr cs && seen.TryGetValue(cs.Idx, out int local))
                        block.Instructions[i] = new ConstStrInstr(cs.Dst, local);

        return result;
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
