using Z42.IR;
using Z42.IR.BinaryFormat;

namespace Z42.Project;

/// Assembles a <see cref="ZpkgFile"/> from a collection of compiled <see cref="ZbcFile"/> units.
///
/// Two packaging modes are supported:
///   <b>Packed</b>  — all ZbcFiles are inlined into the zpkg's <c>modules[]</c> array.
///                    Produces a single self-contained artifact; used for release builds.
///   <b>Indexed</b> — stripped .zbc files are written to <c>cacheDir</c>; the zpkg stores
///                    relative path references in <c>files[]</c>.
///                    Preferred for incremental debug builds.
///
/// 中间产物：两种模式下都把 .zbc 散文件写到 <c>cacheDir</c>（packed 仅写文件，
/// 不在 zpkg 中引用；indexed 写文件并在 zpkg.files[] 中引用）。这是后续真正
/// 增量编译（查 source_hash 跳过未变文件）的物质基础。
public static class ZpkgBuilder
{
    /// Build a packed <see cref="ZpkgFile"/> with all <paramref name="zbcFiles"/> inlined.
    /// 同时把 zbc 散文件写到 cacheDir（如提供）以支持后续增量编译。
    public static ZpkgFile BuildPacked(
        string                    name,
        string                    version,
        ZpkgKind                  kind,
        string?                   entry,
        IReadOnlyList<ZbcFile>    zbcFiles,
        IReadOnlyList<ZpkgDep>    dependencies,
        List<ExportedModule>?     exportedModules = null,
        string?                   projectDir = null,
        string?                   cacheDir = null)
    {
        var namespaces = zbcFiles.Select(z => z.Namespace).Distinct().ToList();
        var exports    = BuildExports(zbcFiles);

        // 中间 zbc 写到 cache（仅当 caller 提供 projectDir + cacheDir 时；与
        // indexed 模式行为一致，但 zpkg 里仍 inline 到 modules[]）。
        if (projectDir is not null && cacheDir is not null)
        {
            foreach (var zbc in zbcFiles)
            {
                string relSrc  = Path.GetRelativePath(projectDir, zbc.SourceFile);
                string zbcPath = Path.Combine(cacheDir, Path.ChangeExtension(relSrc, ".zbc"));
                Directory.CreateDirectory(Path.GetDirectoryName(zbcPath)!);
                File.WriteAllBytes(zbcPath, ZbcWriter.Write(zbc.Module, ZbcFlags.Stripped));
            }
        }

        return new ZpkgFile(
            Name:            name,
            Version:         version,
            Kind:            kind,
            Mode:            ZpkgMode.Packed,
            Namespaces:      namespaces,
            Exports:         exports,
            Dependencies:    dependencies.ToList(),
            Files:           [],
            Modules:         zbcFiles.ToList(),
            Entry:           entry,
            ExportedModules: exportedModules
        );
    }

    /// Build an indexed <see cref="ZpkgFile"/>: writes stripped .zbc files to
    /// <paramref name="cacheDir"/> and records their relative paths in the manifest.
    ///
    /// Returns the assembled <see cref="ZpkgFile"/> and the list of .zbc paths written.
    public static (ZpkgFile Zpkg, IReadOnlyList<string> WrittenZbcPaths) BuildIndexed(
        string                    name,
        string                    version,
        ZpkgKind                  kind,
        string?                   entry,
        IReadOnlyList<ZbcFile>    zbcFiles,
        IReadOnlyList<ZpkgDep>    dependencies,
        string                    projectDir,
        string                    cacheDir,
        string                    outDir)
    {
        var namespaces   = zbcFiles.Select(z => z.Namespace).Distinct().ToList();
        var exports      = BuildExports(zbcFiles);
        var fileEntries  = new List<ZpkgFileEntry>();
        var writtenPaths = new List<string>();

        foreach (var zbc in zbcFiles)
        {
            string relSrc  = Path.GetRelativePath(projectDir, zbc.SourceFile);
            string zbcPath = Path.Combine(cacheDir, Path.ChangeExtension(relSrc, ".zbc"));
            Directory.CreateDirectory(Path.GetDirectoryName(zbcPath)!);
            File.WriteAllBytes(zbcPath, ZbcWriter.Write(zbc.Module, ZbcFlags.Stripped));
            writtenPaths.Add(zbcPath);

            string zbcRel = Path.GetRelativePath(outDir, zbcPath);
            fileEntries.Add(new ZpkgFileEntry(zbc.SourceFile, zbcRel, zbc.SourceHash, zbc.Exports));
        }

        var zpkg = new ZpkgFile(
            Name:         name,
            Version:      version,
            Kind:         kind,
            Mode:         ZpkgMode.Indexed,
            Namespaces:   namespaces,
            Exports:      exports,
            Dependencies: dependencies.ToList(),
            Files:        fileEntries,
            Modules:      [],
            Entry:        entry
        );

        return (zpkg, writtenPaths);
    }

    /// Serialise <paramref name="zpkg"/> to <c><paramref name="outDir"/>/<paramref name="name"/>.zpkg</c>
    /// in binary format (ZPK_MAGIC + section directory + sections).
    /// Creates <paramref name="outDir"/> if it does not exist.
    /// Returns the full path of the written file.
    public static string WriteZpkg(ZpkgFile zpkg, string name, string outDir)
    {
        Directory.CreateDirectory(outDir);
        string zpkgPath = Path.Combine(outDir, name + ".zpkg");
        File.WriteAllBytes(zpkgPath, ZpkgWriter.Write(zpkg));
        return zpkgPath;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// Build the flat ZpkgExport list from all ZbcFile exports, qualifying each
    /// symbol with its namespace.
    static List<ZpkgExport> BuildExports(IReadOnlyList<ZbcFile> zbcFiles) =>
        zbcFiles
            .SelectMany(z => z.Exports.Select(e => new ZpkgExport($"{z.Namespace}.{e}", "func")))
            .ToList();
}
