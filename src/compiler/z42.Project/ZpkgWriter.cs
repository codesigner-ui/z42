using System.Text;
using Z42.IR.BinaryFormat;

namespace Z42.Project;

/// <summary>
/// Serializes a <see cref="ZpkgFile"/> (and its <see cref="ZbcFile"/> modules)
/// into binary zpkg format v0.1.
///
/// File layout:
///   Header    (16 bytes): magic[4] + major[2] + minor[2] + flags[2] + sec_count[2] + reserved[4]
///   Directory (sec_count × 12 bytes): tag[4] + offset[4] + size[4]
///   Sections:
///     META   — name/version/entry (inline UTF-8, no STRS dependency)
///     STRS   — unified string heap (zpkg metadata + all module strings in packed mode)
///     NSPC   — namespace list     (→ STRS)
///     EXPT   — export table       (→ STRS)
///     DEPS   — dependency list    (→ STRS)
///     SIGS   — all function signatures across modules (packed mode only, → STRS)
///     MODS   — per-module FUNC+TYPE bodies (packed mode, indices → STRS)
///     FILE   — per-file path entries (indexed mode, → STRS)
///
/// Packed-mode string optimization: a single unified STRS heap is built from
/// ALL modules (metadata + body strings). Duplicate strings across modules are
/// stored only once, and individual module bodies reference global pool indices.
/// </summary>
public static class ZpkgWriter
{
    public const ushort VersionMajor = 0;
    public const ushort VersionMinor = 1;

    /// Magic bytes: "ZPK\0"
    private static readonly byte[] Magic = [(byte)'Z', (byte)'P', (byte)'K', (byte)'\0'];

    // ── Flags ─────────────────────────────────────────────────────────────────

    private const ushort FlagPacked = 0x01;
    private const ushort FlagExe    = 0x02;

    // ── Public API ─────────────────────────────────────────────────────────────

    /// Serializes a <see cref="ZpkgFile"/> to binary bytes.
    /// <paramref name="zbcFiles"/> is only used in indexed mode
    /// (packed mode embeds modules from <see cref="ZpkgFile.Modules"/>).
    public static byte[] Write(ZpkgFile zpkg, IReadOnlyList<ZbcFile>? zbcFiles = null)
    {
        return zpkg.Mode == ZpkgMode.Packed
            ? WritePacked(zpkg)
            : WriteIndexed(zpkg, zbcFiles ?? []);
    }

    // ── Packed mode ───────────────────────────────────────────────────────────

    private static byte[] WritePacked(ZpkgFile zpkg)
    {
        // ── Build unified string pool from all modules ─────────────────────────
        var pool = new StringPool();

        // Intern zpkg-level metadata strings first
        InternZpkgStrings(pool, zpkg);

        // Per-module strRemap: module.StringPool[i] → global pool index
        var remaps = new int[zpkg.Modules.Count][];
        for (int mi = 0; mi < zpkg.Modules.Count; mi++)
        {
            var zbc  = zpkg.Modules[mi];
            // Intern per-file metadata strings (namespace, source path, hash)
            pool.Intern(zbc.Namespace);
            pool.Intern(zbc.SourceFile);
            pool.Intern(zbc.SourceHash);
            var remap = new int[zbc.Module.StringPool.Count];
            ZbcWriter.InternPoolStrings(pool, zbc.Module, remap, fullMode: true);
            remaps[mi] = remap;
        }

        // ── Build sections ─────────────────────────────────────────────────────
        var sections = new List<(byte[] Tag, byte[] Data)>
        {
            (ZpkgTags.Meta, BuildMetaSection(zpkg)),
            (ZpkgTags.Strs, ZbcWriter.BuildStrpSection(pool)),
            (ZpkgTags.Nspc, BuildNspcSection(zpkg.Namespaces, pool)),
            (ZpkgTags.Expt, BuildExptSection(zpkg.Exports, pool)),
            (ZpkgTags.Deps, BuildDepsSection(zpkg.Dependencies, pool)),
            (ZpkgTags.Sigs, BuildSigsSection(zpkg.Modules, pool)),
            (ZpkgTags.Mods, BuildModsSection(zpkg.Modules, pool, remaps)),
        };

        ushort flags = FlagPacked;
        if (zpkg.Kind == ZpkgKind.Exe) flags |= FlagExe;
        return AssembleFile(flags, sections);
    }

    // ── Indexed mode ──────────────────────────────────────────────────────────

    private static byte[] WriteIndexed(ZpkgFile zpkg, IReadOnlyList<ZbcFile> zbcFiles)
    {
        var pool = new StringPool();
        InternZpkgStrings(pool, zpkg);
        // FILE section strings (source/bytecode/hash paths)
        foreach (var f in zpkg.Files)
        {
            pool.Intern(f.Source);
            pool.Intern(f.Bytecode);
            pool.Intern(f.SourceHash);
            foreach (var e in f.Exports) pool.Intern(e);
        }

        var sections = new List<(byte[] Tag, byte[] Data)>
        {
            (ZpkgTags.Meta, BuildMetaSection(zpkg)),
            (ZpkgTags.Strs, ZbcWriter.BuildStrpSection(pool)),
            (ZpkgTags.Nspc, BuildNspcSection(zpkg.Namespaces, pool)),
            (ZpkgTags.Expt, BuildExptSection(zpkg.Exports, pool)),
            (ZpkgTags.Deps, BuildDepsSection(zpkg.Dependencies, pool)),
            (ZpkgTags.File, BuildFileSection(zpkg.Files, pool)),
        };

        ushort flags = 0;
        if (zpkg.Kind == ZpkgKind.Exe) flags |= FlagExe;
        return AssembleFile(flags, sections);
    }

    // ── String interning helpers ──────────────────────────────────────────────

    private static void InternZpkgStrings(StringPool pool, ZpkgFile zpkg)
    {
        foreach (var ns in zpkg.Namespaces)   pool.Intern(ns);
        foreach (var e  in zpkg.Exports)      pool.Intern(e.Symbol);
        foreach (var dep in zpkg.Dependencies)
        {
            pool.Intern(dep.File);
            foreach (var ns in dep.Namespaces) pool.Intern(ns);
        }
    }

    // ── META section ─────────────────────────────────────────────────────────

    private static byte[] BuildMetaSection(ZpkgFile zpkg)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        void WriteStr(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            w.Write((ushort)b.Length);
            w.Write(b);
        }

        WriteStr(zpkg.Name);
        WriteStr(zpkg.Version);
        WriteStr(zpkg.Entry ?? string.Empty);   // len=0 → no entry (lib)

        return ms.ToArray();
    }

    // ── NSPC section ─────────────────────────────────────────────────────────

    private static byte[] BuildNspcSection(IReadOnlyList<string> namespaces, StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)namespaces.Count);
        foreach (var ns in namespaces) w.Write((uint)pool.Idx(ns));
        return ms.ToArray();
    }

    // ── EXPT section ─────────────────────────────────────────────────────────

    private static byte[] BuildExptSection(IReadOnlyList<ZpkgExport> exports, StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)exports.Count);
        foreach (var e in exports)
        {
            w.Write((uint)pool.Idx(e.Symbol));
            w.Write(KindByte(e.Kind));
        }
        return ms.ToArray();
    }

    private static byte KindByte(string kind) => kind switch
    {
        "type"  => 1,
        "const" => 2,
        _       => 0,   // "func" (default)
    };

    // ── DEPS section ─────────────────────────────────────────────────────────

    private static byte[] BuildDepsSection(IReadOnlyList<ZpkgDep> deps, StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)deps.Count);
        foreach (var dep in deps)
        {
            w.Write((uint)pool.Idx(dep.File));
            w.Write((ushort)dep.Namespaces.Count);
            foreach (var ns in dep.Namespaces) w.Write((uint)pool.Idx(ns));
        }
        return ms.ToArray();
    }

    // ── SIGS section (packed: global function signatures) ─────────────────────

    private static byte[] BuildSigsSection(IReadOnlyList<ZbcFile> modules, StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        uint total = (uint)modules.Sum(m => m.Module.Functions.Count);
        w.Write(total);

        foreach (var zbc in modules)
            foreach (var fn in zbc.Module.Functions)
            {
                w.Write((uint)pool.Idx(fn.Name));
                w.Write((ushort)fn.ParamCount);
                w.Write(TypeTags.FromString(fn.RetType));
                w.Write(ExecModes.FromString(fn.ExecMode));
                w.Write((byte)(fn.IsStatic ? 1 : 0));
            }

        return ms.ToArray();
    }

    // ── MODS section (packed: per-module FUNC+TYPE bodies) ────────────────────

    private static byte[] BuildModsSection(
        IReadOnlyList<ZbcFile> modules,
        StringPool pool,
        int[][] remaps)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write((uint)modules.Count);

        uint firstSigIdx = 0;
        for (int mi = 0; mi < modules.Count; mi++)
        {
            var zbc  = modules[mi];
            var mod  = zbc.Module;

            // FUNC section bytes using global pool indices
            byte[] funcData = ZbcWriter.BuildFuncSection(mod.Functions, pool, remaps[mi]);
            // TYPE section bytes (0 bytes if no classes)
            byte[] typeData = mod.Classes.Count > 0
                ? ZbcWriter.BuildTypeSection(mod.Classes, pool)
                : [];

            w.Write((uint)pool.Idx(zbc.Namespace));
            w.Write((uint)pool.Idx(zbc.SourceFile));
            w.Write((uint)pool.Idx(zbc.SourceHash));
            w.Write((ushort)mod.Functions.Count);
            w.Write(firstSigIdx);
            w.Write((uint)funcData.Length);
            w.Write(funcData);
            w.Write((uint)typeData.Length);
            if (typeData.Length > 0) w.Write(typeData);

            firstSigIdx += (uint)mod.Functions.Count;
        }

        return ms.ToArray();
    }

    // ── FILE section (indexed: per-file path references) ──────────────────────

    private static byte[] BuildFileSection(IReadOnlyList<ZpkgFileEntry> files, StringPool pool)
    {
        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write((uint)files.Count);
        foreach (var f in files)
        {
            w.Write((uint)pool.Idx(f.Source));
            w.Write((uint)pool.Idx(f.Bytecode));
            w.Write((uint)pool.Idx(f.SourceHash));
            w.Write((ushort)f.Exports.Count);
            foreach (var e in f.Exports) w.Write((uint)pool.Idx(e));
        }
        return ms.ToArray();
    }

    // ── File assembly ─────────────────────────────────────────────────────────

    private static byte[] AssembleFile(ushort flags, List<(byte[] Tag, byte[] Data)> sections)
    {
        int headerSize  = 16;
        int dirSize     = sections.Count * 12;
        uint nextOffset = (uint)(headerSize + dirSize);

        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // Header
        w.Write(Magic);
        w.Write(VersionMajor);
        w.Write(VersionMinor);
        w.Write(flags);
        w.Write((ushort)sections.Count);
        w.Write((uint)0); // reserved[4]

        // Directory
        foreach (var (tag, data) in sections)
        {
            w.Write(tag);
            w.Write(nextOffset);
            w.Write((uint)data.Length);
            nextOffset += (uint)data.Length;
        }

        // Section data
        foreach (var (_, data) in sections)
            w.Write(data);

        w.Flush();
        return ms.ToArray();
    }
}

// ── Section tag constants for zpkg ────────────────────────────────────────────

internal static class ZpkgTags
{
    public static readonly byte[] Meta = "META"u8.ToArray();
    public static readonly byte[] Strs = "STRS"u8.ToArray();
    public static readonly byte[] Nspc = "NSPC"u8.ToArray();
    public static readonly byte[] Expt = "EXPT"u8.ToArray();
    public static readonly byte[] Deps = "DEPS"u8.ToArray();
    public static readonly byte[] Sigs = "SIGS"u8.ToArray();
    public static readonly byte[] Mods = "MODS"u8.ToArray();
    public static readonly byte[] File = "FILE"u8.ToArray();
}
