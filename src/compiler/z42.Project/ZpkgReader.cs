using System.Text;
using Z42.IR;
using Z42.IR.BinaryFormat;

namespace Z42.Project;

/// <summary>
/// Deserializes binary zpkg v0.1 data.
///
/// Provides three focused entry points:
///   <see cref="ReadModules"/>    — returns IrModule + namespace pairs (for DependencyIndex, VM loading)
///   <see cref="ReadNamespaces"/> — returns namespace list only (for fast dependency scanning)
///   <see cref="ReadMeta"/>       — returns package metadata (name, version, namespaces, deps, entry)
///   <see cref="ReadTsig"/>       — type signatures (for cross-package compile, see ZpkgReader.Tsig.cs)
///
/// 拆分为多个 partial 文件：
/// • ZpkgReader.cs              — 入口 + 公开 API + Header/Directory + STRS + 工具
/// • ZpkgReader.Sections.cs     — META / NSPC / DEPS / SIGS + Packed/Indexed module reading
/// • ZpkgReader.Tsig.cs         — TSIG / IMPL 类型签名段
/// </summary>
public static partial class ZpkgReader
{
    /// Magic bytes expected at the start of a binary zpkg file.
    private static readonly byte[] ZpkgMagic = [(byte)'Z', (byte)'P', (byte)'K', (byte)'\0'];

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Returns (IrModule, namespace) pairs for all modules in the package.</summary>
    public static IReadOnlyList<(IrModule Module, string Namespace)> ReadModules(byte[] data)
    {
        var (flags, dir) = ParseHeaderAndDirectory(data);
        if ((flags & 0x04) != 0)
            throw new InvalidDataException(
                "zpkg has SymOnly flag set: it is a debug-symbol sidecar (.zsym), " +
                "not a loadable package. Use ReadSidecar() / ApplyDebugInfo() instead.");
        bool packed = (flags & 0x01) != 0;

        string[] pool = ReadStrs(data, dir);

        return packed
            ? ReadPackedModules(data, dir, pool)
            : ReadIndexedModules(data, dir, pool);
    }

    // ── Sidecar API (1.5b split-debug-symbols, zpkg 0.3+) ─────────────────────

    /// <summary>
    /// Per-module debug info parsed from a zpkg sidecar (.zsym): namespace +
    /// LineTable + LocalVarTable. Order matches the main zpkg's MODS section.
    /// </summary>
    public sealed record ZpkgModuleDebug(
        string Namespace,
        IReadOnlyList<(List<IrLineEntry>? Lines, List<IrLocalVarEntry>? Vars)> Functions);

    /// <summary>Decoded contents of a zpkg sidecar.</summary>
    public sealed record ZpkgSidecarData(
        byte[] BuildId,
        IReadOnlyList<ZpkgModuleDebug> Modules);

    /// <summary>Reads only the BLID section (16-byte build_id) from any zpkg.</summary>
    public static byte[]? ReadBuildId(byte[] data)
    {
        var (_, dir) = ParseHeaderAndDirectory(data);
        if (!dir.TryGetValue("BLID", out var e)) return null;
        if (e.Size < BuildId.Size) return null;
        return data.AsSpan(e.Offset, BuildId.Size).ToArray();
    }

    /// <summary>
    /// Parses a SymOnly zpkg sidecar (.zsym): META + STRS + MDBG + BLID.
    /// Throws when the file lacks the SymOnly flag, has no BLID, or content
    /// is malformed.
    /// </summary>
    public static ZpkgSidecarData ReadSidecar(byte[] data)
    {
        var (flags, dir) = ParseHeaderAndDirectory(data);
        if ((flags & 0x04) == 0)
            throw new InvalidDataException(
                "expected SymOnly flag set; this is not a debug-symbol sidecar.");

        if (!dir.TryGetValue("BLID", out var bldEntry) || bldEntry.Size < BuildId.Size)
            throw new InvalidDataException("sidecar is missing BLID section.");
        byte[] buildId = data.AsSpan(bldEntry.Offset, BuildId.Size).ToArray();

        string[] pool = ReadStrs(data, dir);

        if (!dir.TryGetValue("MDBG", out var mdbgEntry))
            throw new InvalidDataException("sidecar is missing MDBG section.");
        var mdbg = ReadMdbgSection(data, mdbgEntry.Offset, mdbgEntry.Size, pool);
        return new ZpkgSidecarData(buildId, mdbg);
    }

    /// <summary>
    /// Merges sidecar debug info into a list of (module, ns) pairs after
    /// verifying that the build_id matches the main zpkg. Returns a new list
    /// with each module's functions enriched with LineTable / LocalVarTable.
    /// Throws on build_id mismatch or module count mismatch.
    /// </summary>
    public static IReadOnlyList<(IrModule Module, string Namespace)> ApplyDebugInfo(
        IReadOnlyList<(IrModule Module, string Namespace)> mainModules,
        byte[] mainZpkgBytes,
        ZpkgSidecarData sidecar)
    {
        byte[]? mainBlid = ReadBuildId(mainZpkgBytes);
        if (mainBlid is null)
            throw new InvalidOperationException(
                "main zpkg has no BLID; cannot pair with sidecar (was it built with stripSymbols=true?).");
        if (!mainBlid.AsSpan().SequenceEqual(sidecar.BuildId))
            throw new InvalidOperationException(
                $"build_id mismatch: main = {BuildId.ShortHex(mainBlid)}…, " +
                $"sidecar = {BuildId.ShortHex(sidecar.BuildId)}…");
        if (mainModules.Count != sidecar.Modules.Count)
            throw new InvalidOperationException(
                $"module count mismatch: main has {mainModules.Count}, " +
                $"sidecar has {sidecar.Modules.Count}");

        var result = new List<(IrModule, string)>(mainModules.Count);
        for (int mi = 0; mi < mainModules.Count; mi++)
        {
            var (mainMod, ns) = mainModules[mi];
            var symMod = sidecar.Modules[mi];
            if (mainMod.Functions.Count != symMod.Functions.Count)
                throw new InvalidOperationException(
                    $"function count mismatch in module '{ns}': main has " +
                    $"{mainMod.Functions.Count}, sidecar has {symMod.Functions.Count}");
            var newFns = new List<IrFunction>(mainMod.Functions.Count);
            for (int fi = 0; fi < mainMod.Functions.Count; fi++)
            {
                var (lines, vars) = symMod.Functions[fi];
                newFns.Add(mainMod.Functions[fi] with
                {
                    LineTable     = lines,
                    LocalVarTable = vars,
                });
            }
            result.Add((mainMod with { Functions = newFns }, ns));
        }
        return result;
    }

    private static IReadOnlyList<ZpkgModuleDebug> ReadMdbgSection(
        byte[] data, int offset, int size, string[] pool)
    {
        using var ms = new MemoryStream(data, offset, size, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        uint modCount = r.ReadUInt32();
        var result    = new List<ZpkgModuleDebug>((int)modCount);
        for (int mi = 0; mi < modCount; mi++)
        {
            string ns      = P(pool, r.ReadUInt32());
            uint dbugLen   = r.ReadUInt32();
            byte[] dbugRaw = dbugLen > 0 ? r.ReadBytes((int)dbugLen) : [];
            var fns = dbugRaw.Length > 0
                ? ZbcReader.DecodeDbugSectionPublic(dbugRaw, pool)
                : new List<(List<IrLineEntry>?, List<IrLocalVarEntry>?)>();
            result.Add(new ZpkgModuleDebug(ns, fns));
        }
        return result;
    }

    /// <summary>Returns the namespace list from the NSPC section (fast path, no body decode).</summary>
    public static IReadOnlyList<string> ReadNamespaces(byte[] data)
    {
        var (_, dir) = ParseHeaderAndDirectory(data);
        string[] pool = ReadStrs(data, dir);
        return ReadNspcSection(data, dir, pool);
    }

    /// <summary>
    /// 读取每个 source file 的 SourceHash + Namespace（不解码 IR module bodies）。
    /// indexed 模式从 FILE section 读；packed 模式从 MODS section 读（仅 header，跳过 funcData/typeData）。
    /// 用于增量编译命中判定（IncrementalBuild）。
    /// </summary>
    public static IReadOnlyList<(string SourceFile, string SourceHash, string Namespace)>
        ReadSourceHashes(byte[] data)
    {
        var (flags, dir) = ParseHeaderAndDirectory(data);
        string[] pool    = ReadStrs(data, dir);
        bool packed      = (flags & 0x01) != 0;

        if (packed)
        {
            if (!dir.TryGetValue("MODS", out var modsEntry)) return [];
            using var ms = new MemoryStream(data, modsEntry.Offset, modsEntry.Size, writable: false);
            using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

            uint modCount = r.ReadUInt32();
            var result    = new List<(string, string, string)>((int)modCount);
            for (int i = 0; i < modCount; i++)
            {
                string ns          = P(pool, r.ReadUInt32());
                string sourceFile  = P(pool, r.ReadUInt32());
                string sourceHash  = P(pool, r.ReadUInt32());
                /*ushort funcCount*/  r.ReadUInt16();
                /*uint firstSigIdx*/  r.ReadUInt32();
                uint funcBodySize  = r.ReadUInt32();
                r.BaseStream.Seek(funcBodySize, SeekOrigin.Current);
                uint typeBodySize  = r.ReadUInt32();
                r.BaseStream.Seek(typeBodySize, SeekOrigin.Current);
                // 1.2 split-debug-symbols: per-member DBUG body trailing typeData.
                uint dbugBodySize  = r.ReadUInt32();
                r.BaseStream.Seek(dbugBodySize, SeekOrigin.Current);
                result.Add((sourceFile, sourceHash, ns));
            }
            return result;
        }
        else
        {
            // indexed: FILE section 含 source/source_hash；NSPC 仅含 namespaces 列表
            // 每文件的 namespace 从 MODS 单独取（indexed 模式 MODS 也存在，含 ns 信息但无 IR body）
            // 简化：indexed 暂时无 namespace 信息（IncrementalBuild 调用方按需匹配 ExportedModules）
            return ReadFileEntries(data, dir, pool);
        }
    }

    static IReadOnlyList<(string SourceFile, string SourceHash, string Namespace)>
        ReadFileEntries(byte[] data, Dictionary<string, (int Offset, int Size)> dir, string[] pool)
    {
        if (!dir.TryGetValue("FILE", out var entry)) return [];
        using var ms = new MemoryStream(data, entry.Offset, entry.Size, writable: false);
        using var r  = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        uint count = r.ReadUInt32();
        var result = new List<(string, string, string)>((int)count);
        for (int i = 0; i < count; i++)
        {
            string source     = P(pool, r.ReadUInt32());
            /*string bytecode*/ r.ReadUInt32();
            string sourceHash = P(pool, r.ReadUInt32());
            ushort expCount   = r.ReadUInt16();
            for (int j = 0; j < expCount; j++) r.ReadUInt32();   // skip exports
            // indexed FILE 不直接含 namespace；调用方需结合 ExportedModules 匹配
            result.Add((source, sourceHash, ""));
        }
        return result;
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

        ushort major    = BitConverter.ToUInt16(data, 4);
        ushort minor    = BitConverter.ToUInt16(data, 6);
        ushort flags    = BitConverter.ToUInt16(data, 8);
        ushort secCount = BitConverter.ToUInt16(data, 10);

        // 1.5b split-debug-symbols: bumped to 0.3 (inner zbc 1.2 + per-member
        // DBUG body + sidecar form). Pre-0.3 zpkg not supported per CLAUDE.md
        // "不为旧版本提供兼容".
        if (major == 0 && minor < 3)
            throw new InvalidDataException(
                $"zpkg {major}.{minor} not supported; requires 0.3+. " +
                "Run scripts/build-stdlib.sh + scripts/regen-golden-tests.sh to upgrade.");

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

    // ── String pool helper ────────────────────────────────────────────────────

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
