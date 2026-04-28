using System.Text;
using Z42.IR;
using Z42.IR.BinaryFormat;

namespace Z42.Project;

/// 基础 zpkg 段读取器（META / NSPC / DEPS / SIGS + Packed/Indexed module reading）。
/// 与 ZpkgReader.cs（入口/Header/STRS）和 ZpkgReader.Tsig.cs（类型签名段）配套。
public static partial class ZpkgReader
{
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

                var (regCount, blocks, excTable, lineTable) = funcBodies[fi];
                functions.Add(new IrFunction(name, paramCount, retType, execMode, blocks,
                    excTable?.Count > 0 ? excTable : null, IsStatic: isStatic,
                    LineTable: lineTable));
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
            // Skip generic type parameters + per-tp constraints (L3-G1 + L3-G3a + L3-G2.5 bare-tp)
            byte tpCount = r.ReadByte();
            for (int t = 0; t < tpCount; t++)
            {
                r.ReadUInt32();                       // type-param name idx
                byte flags = r.ReadByte();            // constraint flags
                if ((flags & 0x04) != 0) r.ReadUInt32();  // base class idx
                if ((flags & 0x08) != 0) r.ReadUInt32();  // type_param_constraint idx
                byte ifaceCount = r.ReadByte();
                for (int k = 0; k < ifaceCount; k++) r.ReadUInt32();
            }
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
}
