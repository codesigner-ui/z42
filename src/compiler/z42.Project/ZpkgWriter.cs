using System.Text;
using Z42.IR;
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
///     TSIG   — type signatures (cross-package generic / interface refs)
///     IMPL   — `impl Trait for Type` declarations (L3-Impl2)
///
/// Packed-mode string optimization: a single unified STRS heap is built from
/// ALL modules (metadata + body strings). Duplicate strings across modules are
/// stored only once, and individual module bodies reference global pool indices.
///
/// 拆分为多个 partial 文件：
/// • ZpkgWriter.cs              — 入口 + 模式分发 + 文件装配
/// • ZpkgWriter.Sections.cs     — 基础段（META/NSPC/EXPT/DEPS/SIGS/MODS/FILE）+ 字符串内联
/// • ZpkgWriter.Tsig.cs         — TSIG / IMPL 类型签名段
/// </summary>
public static partial class ZpkgWriter
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

        if (zpkg.ExportedModules is { Count: > 0 })
        {
            sections.Add((ZpkgTags.Tsig, BuildTsigSection(zpkg.ExportedModules, pool)));
            // L3-Impl2: IMPL section emitted whenever there are exported modules,
            // even if no impl blocks (writes empty record list — small fixed overhead).
            // This keeps decoder logic uniform: TSIG present ⇒ IMPL present.
            sections.Add((ZpkgTags.Impl, BuildImplSection(zpkg.ExportedModules, pool)));
        }

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
    public static readonly byte[] Tsig = "TSIG"u8.ToArray();
    public static readonly byte[] Impl = "IMPL"u8.ToArray();
}
