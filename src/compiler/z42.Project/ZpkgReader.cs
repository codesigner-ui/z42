using System.Text;
using Z42.IR;
using Z42.IR.BinaryFormat;

namespace Z42.Project;

/// <summary>
/// Deserializes binary zpkg v0.1 data.
///
/// Provides two focused entry points:
///   <see cref="ReadModules"/>    — returns IrModule + namespace pairs (for StdlibCallIndex, VM loading)
///   <see cref="ReadNamespaces"/> — returns namespace list only (for fast dependency scanning)
///   <see cref="ReadMeta"/>       — returns package metadata (name, version, namespaces, deps, entry)
/// </summary>
public static class ZpkgReader
{
    /// Magic bytes expected at the start of a binary zpkg file.
    private static readonly byte[] ZpkgMagic = [(byte)'Z', (byte)'P', (byte)'K', (byte)'\0'];

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Returns (IrModule, namespace) pairs for all modules in the package.</summary>
    public static IReadOnlyList<(IrModule Module, string Namespace)> ReadModules(byte[] data)
    {
        var (flags, dir) = ParseHeaderAndDirectory(data);
        bool packed = (flags & 0x01) != 0;

        string[] pool = ReadStrs(data, dir);

        return packed
            ? ReadPackedModules(data, dir, pool)
            : ReadIndexedModules(data, dir, pool);
    }

    /// <summary>Returns the namespace list from the NSPC section (fast path, no body decode).</summary>
    public static IReadOnlyList<string> ReadNamespaces(byte[] data)
    {
        var (_, dir) = ParseHeaderAndDirectory(data);
        string[] pool = ReadStrs(data, dir);
        return ReadNspcSection(data, dir, pool);
    }

    /// <summary>Returns basic package metadata without decoding module bodies.</summary>
    public static ZpkgMeta ReadMeta(byte[] data)
    {
        var (flags, dir) = ParseHeaderAndDirectory(data);
        string[] pool    = ReadStrs(data, dir);
        var (name, version, entry) = ReadMetaSection(data, dir);
        var namespaces   = ReadNspcSection(data, dir, pool);
        var deps         = ReadDepsSection(data, dir, pool);
        bool packed      = (flags & 0x01) != 0;
        bool isExe       = (flags & 0x02) != 0;

        return new ZpkgMeta(name, version, entry, namespaces, deps,
            packed ? ZpkgMode.Packed : ZpkgMode.Indexed,
            isExe  ? ZpkgKind.Exe   : ZpkgKind.Lib);
    }

    // ── Binary detection ──────────────────────────────────────────────────────

    /// Returns true if the byte array starts with the zpkg binary magic.
    public static bool IsBinary(byte[] data) =>
        data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(ZpkgMagic);

    // ── Header + Directory ────────────────────────────────────────────────────

    private static (ushort Flags, Dictionary<string, (int Offset, int Size)> Dir)
        ParseHeaderAndDirectory(byte[] data)
    {
        if (data.Length < 16)
            throw new InvalidDataException("zpkg file too short");
        if (!data.AsSpan(0, 4).SequenceEqual(ZpkgMagic))
            throw new InvalidDataException("Not a binary zpkg (bad magic)");

        ushort flags    = BitConverter.ToUInt16(data, 8);
        ushort secCount = BitConverter.ToUInt16(data, 10);

        var dir = new Dictionary<string, (int Offset, int Size)>(StringComparer.Ordinal);
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

        return (flags, dir);
    }

    // ── STRS section ─────────────────────────────────────────────────────────

    private static string[] ReadStrs(byte[] data, Dictionary<string, (int Offset, int Size)> dir)
    {
        if (!dir.TryGetValue("STRS", out var e)) return [];
        return ParseStrsSection(data, e.Offset, e.Size);
    }

    private static string[] ParseStrsSection(byte[] data, int offset, int size)
    {
        using var ms = new MemoryStream(data, offset, size, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        uint count  = r.ReadUInt32();
        var entries = new (uint Off, uint Len)[count];
        for (int i = 0; i < count; i++)
            entries[i] = (r.ReadUInt32(), r.ReadUInt32());

        long dataBase = ms.Position;
        var result    = new string[count];
        for (int i = 0; i < count; i++)
        {
            ms.Position = dataBase + entries[i].Off;
            result[i]   = Encoding.UTF8.GetString(r.ReadBytes((int)entries[i].Len));
        }
        return result;
    }

    // ── META section ─────────────────────────────────────────────────────────

    private static (string Name, string Version, string? Entry) ReadMetaSection(
        byte[] data, Dictionary<string, (int Offset, int Size)> dir)
    {
        if (!dir.TryGetValue("META", out var e)) return ("", "0.0.0", null);
        using var ms = new MemoryStream(data, e.Offset, e.Size, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        string name    = ReadInlineStr(r);
        string version = ReadInlineStr(r);
        string entryRaw= ReadInlineStr(r);
        string? entry  = entryRaw.Length > 0 ? entryRaw : null;
        return (name, version, entry);
    }

    private static string ReadInlineStr(BinaryReader r)
    {
        ushort len = r.ReadUInt16();
        return len == 0 ? string.Empty : Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    // ── NSPC section ─────────────────────────────────────────────────────────

    private static List<string> ReadNspcSection(
        byte[] data, Dictionary<string, (int Offset, int Size)> dir, string[] pool)
    {
        if (!dir.TryGetValue("NSPC", out var e)) return [];
        using var ms = new MemoryStream(data, e.Offset, e.Size, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        uint count   = r.ReadUInt32();
        var result   = new List<string>((int)count);
        for (int i = 0; i < count; i++) result.Add(P(pool, r.ReadUInt32()));
        return result;
    }

    // ── DEPS section ─────────────────────────────────────────────────────────

    private static List<ZpkgDep> ReadDepsSection(
        byte[] data, Dictionary<string, (int Offset, int Size)> dir, string[] pool)
    {
        if (!dir.TryGetValue("DEPS", out var e)) return [];
        using var ms = new MemoryStream(data, e.Offset, e.Size, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        uint count   = r.ReadUInt32();
        var result   = new List<ZpkgDep>((int)count);
        for (int i = 0; i < count; i++)
        {
            string file    = P(pool, r.ReadUInt32());
            ushort nsCount = r.ReadUInt16();
            var ns         = new List<string>(nsCount);
            for (int j = 0; j < nsCount; j++) ns.Add(P(pool, r.ReadUInt32()));
            result.Add(new ZpkgDep(file, ns));
        }
        return result;
    }

    // ── Packed-mode module reading ────────────────────────────────────────────

    private static List<(IrModule, string)> ReadPackedModules(
        byte[] data,
        Dictionary<string, (int Offset, int Size)> dir,
        string[] pool)
    {
        // SIGS: global function signature table
        var sigs = ReadSigsSection(data, dir, pool);

        if (!dir.TryGetValue("MODS", out var modsEntry)) return [];

        using var ms = new MemoryStream(data, modsEntry.Offset, modsEntry.Size, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        uint modCount = r.ReadUInt32();
        var result    = new List<(IrModule, string)>((int)modCount);

        for (int mi = 0; mi < modCount; mi++)
        {
            string ns          = P(pool, r.ReadUInt32());
            string sourceFile  = P(pool, r.ReadUInt32());
            string sourceHash  = P(pool, r.ReadUInt32());
            ushort funcCount   = r.ReadUInt16();
            uint firstSigIdx   = r.ReadUInt32();

            uint funcBodySize  = r.ReadUInt32();
            byte[] funcData    = r.ReadBytes((int)funcBodySize);

            uint typeBodySize  = r.ReadUInt32();
            byte[] typeData    = typeBodySize > 0 ? r.ReadBytes((int)typeBodySize) : [];

            // Decode function bodies using global pool
            var funcBodies = ZbcReader.DecodeFuncSectionPublic(funcData, pool);
            var classes    = typeData.Length > 0
                ? ZbcReader.DecodeTypeSectionPublic(typeData, pool)
                : new List<IrClassDesc>();

            // Reconstruct functions
            var functions = new List<IrFunction>(funcCount);
            for (int fi = 0; fi < funcCount; fi++)
            {
                int sigIdx = (int)(firstSigIdx + fi);
                var (name, paramCount, retType, execMode, isStatic) = sigIdx < sigs.Count
                    ? sigs[sigIdx]
                    : ($"func#{fi}", (ushort)0, "void", "Interp", false);

                var (regCount, blocks, excTable) = funcBodies[fi];
                functions.Add(new IrFunction(name, paramCount, retType, execMode, blocks,
                    excTable?.Count > 0 ? excTable : null, IsStatic: isStatic));
            }

            var strPool = ZbcReader.RebuildStringPoolPublic(pool, functions);
            result.Add((new IrModule(ns, strPool, classes, functions), ns));
        }

        return result;
    }

    private static List<(string Name, ushort ParamCount, string RetType, string ExecMode, bool IsStatic)> ReadSigsSection(
        byte[] data, Dictionary<string, (int Offset, int Size)> dir, string[] pool)
    {
        if (!dir.TryGetValue("SIGS", out var e)) return [];
        using var ms = new MemoryStream(data, e.Offset, e.Size, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
        uint count   = r.ReadUInt32();
        var result   = new List<(string, ushort, string, string, bool)>((int)count);
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

    // ── Indexed-mode module reading ───────────────────────────────────────────

    private static List<(IrModule, string)> ReadIndexedModules(
        byte[] data,
        Dictionary<string, (int Offset, int Size)> dir,
        string[] pool)
    {
        // Indexed mode: module bodies are in separate .zbc files on disk.
        // ZpkgReader doesn't load .zbc files from disk (caller must do that).
        // Return empty list; runtime uses loader.rs to load .zbc files directly.
        return [];
    }

    // ── TSIG section (type signatures for reference compilation) ────────────

    /// <summary>Read exported type signatures from the TSIG section.</summary>
    public static List<ExportedModule> ReadTsig(byte[] data)
    {
        var (_, dir) = ParseHeaderAndDirectory(data);
        string[] pool = ReadStrs(data, dir);
        return ReadTsigSection(data, dir, pool);
    }

    private static List<ExportedModule> ReadTsigSection(
        byte[] data, Dictionary<string, (int Offset, int Size)> dir, string[] pool)
    {
        if (!dir.TryGetValue("TSIG", out var e)) return [];
        using var ms = new MemoryStream(data, e.Offset, e.Size, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        ushort modCount = r.ReadUInt16();
        var result = new List<ExportedModule>(modCount);

        for (int mi = 0; mi < modCount; mi++)
        {
            string ns = P(pool, r.ReadUInt32());

            // Classes
            ushort clsCount = r.ReadUInt16();
            var classes = new List<ExportedClassDef>(clsCount);
            for (int ci = 0; ci < clsCount; ci++)
            {
                string name    = P(pool, r.ReadUInt32());
                uint baseRaw   = r.ReadUInt32();
                string? baseCls = baseRaw == uint.MaxValue ? null : P(pool, baseRaw);
                byte flags     = r.ReadByte();
                bool isAbstract = (flags & 0x01) != 0;
                bool isSealed   = (flags & 0x02) != 0;
                bool isStatic   = (flags & 0x04) != 0;

                ushort fldCount = r.ReadUInt16();
                var fields = new List<ExportedFieldDef>(fldCount);
                for (int fi = 0; fi < fldCount; fi++)
                {
                    string fn = P(pool, r.ReadUInt32());
                    string ft = P(pool, r.ReadUInt32());
                    string fv = P(pool, r.ReadUInt32());
                    bool   fs = r.ReadByte() != 0;
                    fields.Add(new ExportedFieldDef(fn, ft, fv, fs));
                }

                ushort mthCount = r.ReadUInt16();
                var methods = new List<ExportedMethodDef>(mthCount);
                for (int mi2 = 0; mi2 < mthCount; mi2++)
                    methods.Add(ReadMethodDef(r, pool));

                ushort ifaceCount = r.ReadUInt16();
                var ifaces = new List<string>(ifaceCount);
                for (int ii = 0; ii < ifaceCount; ii++)
                    ifaces.Add(P(pool, r.ReadUInt32()));

                classes.Add(new ExportedClassDef(name, baseCls, isAbstract, isSealed, isStatic,
                    fields, methods, ifaces));
            }

            // Interfaces
            ushort ifcCount = r.ReadUInt16();
            var interfaces = new List<ExportedInterfaceDef>(ifcCount);
            for (int ii = 0; ii < ifcCount; ii++)
            {
                string name     = P(pool, r.ReadUInt32());
                ushort mthCount = r.ReadUInt16();
                var methods = new List<ExportedMethodDef>(mthCount);
                for (int mi2 = 0; mi2 < mthCount; mi2++)
                    methods.Add(ReadMethodDef(r, pool));
                interfaces.Add(new ExportedInterfaceDef(name, methods));
            }

            // Enums
            ushort enumCount = r.ReadUInt16();
            var enums = new List<ExportedEnumDef>(enumCount);
            for (int ei = 0; ei < enumCount; ei++)
            {
                string name     = P(pool, r.ReadUInt32());
                ushort memCount = r.ReadUInt16();
                var members = new List<ExportedEnumMember>(memCount);
                for (int mi2 = 0; mi2 < memCount; mi2++)
                {
                    string mn = P(pool, r.ReadUInt32());
                    long   mv = r.ReadInt64();
                    members.Add(new ExportedEnumMember(mn, mv));
                }
                enums.Add(new ExportedEnumDef(name, members));
            }

            // Functions
            ushort fnCount = r.ReadUInt16();
            var functions = new List<ExportedFuncDef>(fnCount);
            for (int fi = 0; fi < fnCount; fi++)
            {
                string name    = P(pool, r.ReadUInt32());
                string retType = P(pool, r.ReadUInt32());
                ushort minArgs = r.ReadUInt16();
                byte paramCnt  = r.ReadByte();
                var parms = new List<ExportedParamDef>(paramCnt);
                for (int pi = 0; pi < paramCnt; pi++)
                {
                    string pn = P(pool, r.ReadUInt32());
                    string pt = P(pool, r.ReadUInt32());
                    parms.Add(new ExportedParamDef(pn, pt));
                }
                functions.Add(new ExportedFuncDef(name, parms, retType, minArgs));
            }

            result.Add(new ExportedModule(ns, classes, interfaces, enums, functions));
        }
        return result;
    }

    private static ExportedMethodDef ReadMethodDef(BinaryReader r, string[] pool)
    {
        string name    = P(pool, r.ReadUInt32());
        string retType = P(pool, r.ReadUInt32());
        string vis     = P(pool, r.ReadUInt32());
        byte flags     = r.ReadByte();
        bool isStatic   = (flags & 0x01) != 0;
        bool isVirtual  = (flags & 0x02) != 0;
        bool isAbstract = (flags & 0x04) != 0;
        ushort minArgs  = r.ReadUInt16();
        byte paramCnt   = r.ReadByte();
        var parms = new List<ExportedParamDef>(paramCnt);
        for (int pi = 0; pi < paramCnt; pi++)
        {
            string pn = P(pool, r.ReadUInt32());
            string pt = P(pool, r.ReadUInt32());
            parms.Add(new ExportedParamDef(pn, pt));
        }
        return new ExportedMethodDef(name, parms, retType, vis, isStatic, isVirtual, isAbstract, minArgs);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string P(string[] pool, uint idx) =>
        idx < pool.Length ? pool[idx] : $"<str#{idx}>";
}

// ── Package metadata record ────────────────────────────────────────────────────

/// Lightweight metadata read from a binary zpkg without decoding module bodies.
public sealed record ZpkgMeta(
    string             Name,
    string             Version,
    string?            Entry,
    IReadOnlyList<string>   Namespaces,
    IReadOnlyList<ZpkgDep>  Dependencies,
    ZpkgMode           Mode,
    ZpkgKind           Kind
);
