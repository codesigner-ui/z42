using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Project;

namespace Z42.Pipeline;

/// <summary>
/// 增量编译查询：给定 sourceFiles + 上次 zpkg + cacheDir，比对 source_hash 决定
/// 哪些文件可复用 cache（cached）、哪些需要重新编译（fresh）。
///
/// 失效条件（任一不满足即视为 fresh）：
///   1. 上次 zpkg 文件存在
///   2. 上次 zpkg 中含该 sourceFile 的 SourceHash 记录
///   3. SHA-256(当前文件内容) == 记录的 SourceHash
///   4. cache/&lt;rel&gt;.zbc 文件存在
///   5. zpkg.ExportedModules 中含该文件 namespace 对应的 ExportedModule（fresh CU 重建符号需要）
///
/// 设计哲学：宁可 fresh 不可错误命中（错误命中会导致编译产物逻辑不一致）。
/// </summary>
public sealed class IncrementalBuild
{
    /// <summary>
    /// CachedZbcByFile: cache 中 fullMode zbc 的字节内容；ZbcReader.Read 可独立反序列化为
    /// 完整 IrModule（含 fn.Name / TypeParams / LocalVarTable 等）。
    /// CachedExportsByNs: 上次 zpkg 中对应 namespace 的 ExportedModule。
    /// LastZpkgDepNamespaces: 上次 zpkg.Dependencies 列出的所有 namespace 集合，
    /// cached CU 重建时塞入 UsedDepNamespaces，让 BuildDependencyMap 能正确回填依赖。
    /// </summary>
    public sealed record ProbeResult(
        IReadOnlyDictionary<string, byte[]> CachedZbcByFile,
        IReadOnlyDictionary<string, ExportedModule> CachedExportsByNs,
        IReadOnlyDictionary<string, string> CachedNamespaceByFile,
        IReadOnlyList<string> LastZpkgDepNamespaces,
        IReadOnlyList<string> FreshFiles,
        int CachedCount,
        int TotalCount)
    {
        public static ProbeResult AllFresh(IReadOnlyList<string> sourceFiles) =>
            new(
                CachedZbcByFile:       new Dictionary<string, byte[]>(StringComparer.Ordinal),
                CachedExportsByNs:     new Dictionary<string, ExportedModule>(StringComparer.Ordinal),
                CachedNamespaceByFile: new Dictionary<string, string>(StringComparer.Ordinal),
                LastZpkgDepNamespaces: Array.Empty<string>(),
                FreshFiles:            sourceFiles,
                CachedCount:           0,
                TotalCount:            sourceFiles.Count);
    }

    public ProbeResult Probe(
        IReadOnlyList<string> sourceFiles,
        string                projectDir,
        string                cacheDir,
        string                lastZpkgPath)
    {
        // 1. 上次 zpkg 不存在 → 全 fresh
        if (!File.Exists(lastZpkgPath))
            return ProbeResult.AllFresh(sourceFiles);

        byte[] zpkgBytes;
        try { zpkgBytes = File.ReadAllBytes(lastZpkgPath); }
        catch { return ProbeResult.AllFresh(sourceFiles); }

        // 2. 解析 source_hash 表 + ExportedModules + Dependencies（用于回填 UsedDepNamespaces）
        IReadOnlyList<(string SourceFile, string SourceHash, string Namespace)> hashes;
        List<ExportedModule> exportedModules;
        ZpkgMeta meta;
        try
        {
            hashes          = ZpkgReader.ReadSourceHashes(zpkgBytes);
            exportedModules = ZpkgReader.ReadTsig(zpkgBytes);
            meta            = ZpkgReader.ReadMeta(zpkgBytes);
        }
        catch { return ProbeResult.AllFresh(sourceFiles); }

        if (hashes.Count == 0)
            return ProbeResult.AllFresh(sourceFiles);

        // 收集上次 zpkg.dependencies 中所有 namespace（cached CU 重建用）
        var lastDepNamespaces = meta.Dependencies
            .SelectMany(d => d.Namespaces)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // zpkg 中 sourceFile 可能是绝对路径或相对路径；同时按 abs + rel 索引以兼容
        var hashBySource = new Dictionary<string, (string Hash, string Namespace)>(StringComparer.Ordinal);
        foreach (var (src, hash, ns) in hashes)
        {
            string normalized = NormalizeRel(src);
            hashBySource[normalized] = (hash, ns);
            // 若 zpkg 存的是绝对路径，把"相对 projectDir 的路径"也加入 key
            if (Path.IsPathRooted(src))
            {
                try
                {
                    string rel = NormalizeRel(Path.GetRelativePath(projectDir, src));
                    hashBySource[rel] = (hash, ns);
                }
                catch { /* path 跨盘等异常路径忽略 */ }
            }
        }

        var exportsByNs = exportedModules
            .GroupBy(m => m.Namespace, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // 3. 对每个当前 sourceFile 比对
        var cachedZbcByFile   = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var cachedNsByFile    = new Dictionary<string, string>(StringComparer.Ordinal);
        var cachedExportsByNs = new Dictionary<string, ExportedModule>(StringComparer.Ordinal);
        var freshFiles        = new List<string>();

        bool debug = Environment.GetEnvironmentVariable("Z42_INCR_DEBUG") == "1";
        foreach (var sourceFile in sourceFiles)
        {
            string absSource = NormalizeRel(Path.GetFullPath(sourceFile));
            string relSource = NormalizeRel(Path.GetRelativePath(projectDir, sourceFile));

            // 失效条件 2: zpkg 中无此文件记录（同时按 abs + rel 查）
            if (!hashBySource.TryGetValue(absSource, out var rec) &&
                !hashBySource.TryGetValue(relSource, out rec))
            {
                if (debug) Console.Error.WriteLine($"  [miss/no-record] {sourceFile} (abs='{absSource}' rel='{relSource}')");
                freshFiles.Add(sourceFile);
                continue;
            }

            // 失效条件 3: hash 不匹配
            string currentHash;
            try { currentHash = Sha256Hex(File.ReadAllText(sourceFile)); }
            catch { freshFiles.Add(sourceFile); continue; }

            if (!string.Equals(currentHash, rec.Hash, StringComparison.Ordinal))
            {
                if (debug) Console.Error.WriteLine($"  [miss/hash-diff] {sourceFile} cur={currentHash[..8]} rec={rec.Hash[..8]}");
                freshFiles.Add(sourceFile);
                continue;
            }

            // 失效条件 4: cache zbc 不存在
            string zbcPath = Path.Combine(cacheDir, Path.ChangeExtension(relSource, ".zbc"));
            if (!File.Exists(zbcPath))
            {
                if (debug) Console.Error.WriteLine($"  [miss/no-zbc] {sourceFile} expected at {zbcPath}");
                freshFiles.Add(sourceFile);
                continue;
            }

            byte[] zbcBytes;
            try { zbcBytes = File.ReadAllBytes(zbcPath); }
            catch { freshFiles.Add(sourceFile); continue; }

            // 失效条件 5: namespace 对应的 ExportedModule 不存在
            // indexed 模式 ReadSourceHashes 返回 ns="";调用方需查 ZbcReader.ReadNamespace 兜底
            string ns = rec.Namespace;
            if (string.IsNullOrEmpty(ns))
            {
                try { ns = ZbcReader.ReadNamespace(zbcBytes); }
                catch { freshFiles.Add(sourceFile); continue; }
            }

            if (!exportsByNs.TryGetValue(ns, out var exportedMod))
            {
                if (debug) Console.Error.WriteLine($"  [miss/no-export-mod] {sourceFile} ns='{ns}' exports={string.Join(",", exportsByNs.Keys)}");
                freshFiles.Add(sourceFile);
                continue;
            }

            // 全部命中：cache zbc 是 fullMode（C5 BuildPacked 已切换），ZbcReader.Read 可
            // 独立反序列化为完整 IrModule（含 fn.Name / TypeParams / LocalVarTable 等）。
            cachedZbcByFile[sourceFile]   = zbcBytes;
            cachedNsByFile[sourceFile]    = ns;
            cachedExportsByNs[ns]         = exportedMod;
            if (debug) Console.Error.WriteLine($"  [hit] {sourceFile} ns='{ns}' impls={exportedMod.Impls?.Count ?? 0}");
        }

        return new ProbeResult(
            CachedZbcByFile:       cachedZbcByFile,
            CachedExportsByNs:     cachedExportsByNs,
            CachedNamespaceByFile: cachedNsByFile,
            LastZpkgDepNamespaces: lastDepNamespaces,
            FreshFiles:            freshFiles,
            CachedCount:           cachedZbcByFile.Count,
            TotalCount:            sourceFiles.Count);
    }

    static string NormalizeRel(string path) => path.Replace('\\', '/');

    /// 复用 CompilerUtils.Sha256Hex 保证与 ZpkgBuilder 写入的格式一致（"sha256:<hex>"）。
    static string Sha256Hex(string text) => CompilerUtils.Sha256Hex(text);
}
