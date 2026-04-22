using System.Text;

namespace Z42.IR.BinaryFormat;

/// <summary>
/// Deserializes binary zbc v0.3 data back into an <see cref="IrModule"/>.
/// Mirrors the layout written by <see cref="ZbcWriter"/>.
///
/// v0.3: header[16] + section directory[sec_count × 12] + sections at absolute offsets.
/// Sections are accessed via the directory (random access, O(1) per section).
/// </summary>
public static partial class ZbcReader
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
                                         new List<SigEntry>());
        var funcBodies     = ReadSection(data, dir, SectionTags.Func,
                                         sec => ReadFuncSection(sec, pool),
                                         new List<(int, List<IrBlock>, List<IrExceptionEntry>?, List<IrLineEntry>?)>());
        var dbugVarTables  = ReadSection(data, dir, SectionTags.Dbug,
                                         sec => ReadDbugSection(sec, pool),
                                         new List<List<IrLocalVarEntry>?>());

        // ── Reconstruct functions ─────────────────────────────────────────────
        var functions = new List<IrFunction>(funcBodies.Count);
        for (int i = 0; i < funcBodies.Count; i++)
        {
            var (regCount, blocks, excTable, lineTable) = funcBodies[i];
            var sig = i < sigs.Count ? sigs[i] : null;
            string name     = sig?.Name     ?? $"func#{i}";
            ushort paramCount = sig?.ParamCount ?? 0;
            string retType  = sig?.RetType  ?? "void";
            string execMode = sig?.ExecMode ?? "Interp";
            bool   isStatic = sig?.IsStatic ?? false;
            var typeParams  = sig?.TypeParams;
            var typeParamConstraints = sig?.TypeParamConstraints;

            var localVars = i < dbugVarTables.Count ? dbugVarTables[i] : null;
            functions.Add(new IrFunction(name, paramCount, retType, execMode, blocks,
                excTable?.Count > 0 ? excTable : null, IsStatic: isStatic,
                LineTable: lineTable, LocalVarTable: localVars,
                TypeParams: typeParams, TypeParamConstraints: typeParamConstraints));
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
            // Generic type parameters (L3-G1) + per-tp constraints (L3-G3a)
            byte tpCount = r.ReadByte();
            List<string>? typeParams = tpCount > 0 ? new(tpCount) : null;
            List<IrConstraintBundle>? typeParamConstraints = tpCount > 0
                ? new List<IrConstraintBundle>(tpCount) : null;
            for (int t = 0; t < tpCount; t++)
            {
                typeParams!.Add(P(pool, r.ReadUInt32()));
                typeParamConstraints!.Add(ReadConstraintBundle(r, pool));
            }
            classes.Add(new IrClassDesc(name, baseCls, fields, typeParams, typeParamConstraints));
        }
        return classes;
    }

    // ── Constraint bundle codec (L3-G3a) ─────────────────────────────────────

    /// Decodes one constraint bundle (matches ZbcWriter.WriteConstraintBundle layout).
    private static IrConstraintBundle ReadConstraintBundle(BinaryReader r, string[] pool)
    {
        byte flags = r.ReadByte();
        bool reqClass  = (flags & 0x01) != 0;
        bool reqStruct = (flags & 0x02) != 0;
        bool hasBase   = (flags & 0x04) != 0;
        string? baseClass = hasBase ? P(pool, r.ReadUInt32()) : null;
        byte ifaceCount = r.ReadByte();
        var ifaces = new List<string>(ifaceCount);
        for (int i = 0; i < ifaceCount; i++)
            ifaces.Add(P(pool, r.ReadUInt32()));
        return new IrConstraintBundle(reqClass, reqStruct, baseClass, ifaces);
    }

    // ── SIGS section ──────────────────────────────────────────────────────────

    private record SigEntry(
        string Name, ushort ParamCount, string RetType, string ExecMode, bool IsStatic,
        List<string>? TypeParams, List<IrConstraintBundle>? TypeParamConstraints);

    private static List<SigEntry> ReadSigsSection(byte[] data, string[] pool)
    {
        using var ms = new MemoryStream(data, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        uint count = r.ReadUInt32();
        var result = new List<SigEntry>((int)count);

        for (int i = 0; i < count; i++)
        {
            string name       = P(pool, r.ReadUInt32());
            ushort paramCount = r.ReadUInt16();
            string retType    = TypeTags.ToIrString(r.ReadByte());
            string execMode   = ExecModes.ToIrString(r.ReadByte());
            bool   isStatic   = r.ReadByte() != 0;
            // Generic type parameters (v0.3+) + per-tp constraints (v0.5+)
            byte tpCount = (ms.Position < ms.Length) ? r.ReadByte() : (byte)0;
            List<string>? typeParams = null;
            List<IrConstraintBundle>? typeParamConstraints = null;
            if (tpCount > 0)
            {
                typeParams = new List<string>(tpCount);
                typeParamConstraints = new List<IrConstraintBundle>(tpCount);
                for (int t = 0; t < tpCount; t++)
                {
                    typeParams.Add(P(pool, r.ReadUInt32()));
                    typeParamConstraints.Add(ReadConstraintBundle(r, pool));
                }
            }
            result.Add(new SigEntry(name, paramCount, retType, execMode, isStatic,
                typeParams, typeParamConstraints));
        }
        return result;
    }

    // ── FUNC section ─────────────────────────────────────────────────────────

    private static List<(int, List<IrBlock>, List<IrExceptionEntry>?, List<IrLineEntry>?)> ReadFuncSection(
        byte[] data, string[] pool)
    {
        using var ms   = new MemoryStream(data, writable: false);
        using var r    = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        uint funcCount = r.ReadUInt32();
        var result     = new List<(int, List<IrBlock>, List<IrExceptionEntry>?, List<IrLineEntry>?)>((int)funcCount);

        // Peek flag: detect old format (no line_count field) vs new.
        // For forward compat, we only read line_count when enough bytes remain.

        for (int fi = 0; fi < funcCount; fi++)
        {
            int    regCount   = r.ReadUInt16();
            ushort blockCount = r.ReadUInt16();
            uint   instrLen   = r.ReadUInt32();
            ushort excCount   = r.ReadUInt16();
            ushort lineCount  = r.ReadUInt16();

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

            var lineTable = lineCount > 0 ? new List<IrLineEntry>(lineCount) : null;
            for (int i = 0; i < lineCount; i++)
            {
                ushort blk  = r.ReadUInt16();
                ushort ins  = r.ReadUInt16();
                uint lineNo = r.ReadUInt32();
                uint fileId = r.ReadUInt32();
                string? file = fileId == uint.MaxValue ? null : P(pool, fileId);
                lineTable!.Add(new IrLineEntry(blk, ins, (int)lineNo, file));
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

            result.Add((regCount, blocks, resolvedExc, lineTable));
        }
        return result;
    }

    private static string Resolve(string raw, List<IrBlock> blocks)
    {
        if (raw.StartsWith("block_") && int.TryParse(raw[6..], out int idx))
            return idx < blocks.Count ? blocks[idx].Label : raw;
        return raw;
    }

    // ── Internal helpers for ZpkgReader ──────────────────────────────────────

    /// Exposes FUNC section decoding for ZpkgReader (packed mode module bodies).
    public static List<(int, List<IrBlock>, List<IrExceptionEntry>?, List<IrLineEntry>?)> DecodeFuncSectionPublic(
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

    // ── DBUG section (local variable names) ──────────────────────────────────

    private static List<List<IrLocalVarEntry>?> ReadDbugSection(
        ReadOnlySpan<byte> sec, string[] pool)
    {
        int pos = 0;
        uint funcCount = BitConverter.ToUInt32(sec.Slice(pos, 4)); pos += 4;

        var result = new List<List<IrLocalVarEntry>?>((int)funcCount);
        for (int f = 0; f < (int)funcCount; f++)
        {
            ushort varCount = BitConverter.ToUInt16(sec.Slice(pos, 2)); pos += 2;
            if (varCount == 0) { result.Add(null); continue; }

            var vars = new List<IrLocalVarEntry>(varCount);
            for (int v = 0; v < varCount; v++)
            {
                uint nameIdx = BitConverter.ToUInt32(sec.Slice(pos, 4)); pos += 4;
                ushort regId = BitConverter.ToUInt16(sec.Slice(pos, 2)); pos += 2;
                string name  = nameIdx < pool.Length ? pool[nameIdx] : $"?{nameIdx}";
                vars.Add(new IrLocalVarEntry(name, regId));
            }
            result.Add(vars);
        }
        return result;
    }
}
