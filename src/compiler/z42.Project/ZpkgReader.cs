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
