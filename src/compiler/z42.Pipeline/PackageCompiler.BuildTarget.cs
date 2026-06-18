using Z42.Core;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Core.Text;
using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Project;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Pipeline;

/// 单 target 编译入口 + 各阶段子步骤（lib / zbc 扫描、Phase 1 解析 + Pass-0
/// 收集、Phase 2 全编译、依赖映射）。与 PackageCompiler.cs 的 CLI 入口分离。
public static partial class PackageCompiler
{
    // ── Single target build ───────────────────────────────────────────────────

    static int BuildTarget(
        string                name,
        string                version,
        ZpkgKind              kind,
        string?               entry,
        IReadOnlyList<string> sourceFiles,
        bool                  pack,
        string                projectDir,
        string                outDir,
        DependencySection?    declaredDeps = null,
        string?               explicitCacheDir = null,
        bool                  useIncremental = true,
        bool                  stripSymbols = false,
        IReadOnlyList<string>? workspaceLibDirs = null,
        string?               publishDir = null)
    {
        // L3-G4g: libsDirs includes projectDir/libs, projectDir/artifacts/z42/libs,
        // plus a walk-up search for the repo-level `artifacts/z42/libs` so stdlib
        // packages compiling against each other (e.g. z42.collections → z42.core)
        // can resolve their dependencies.
        var libsDirs    = BuildLibsDirs(projectDir, workspaceLibDirs);
        var tsigCache   = new TsigCache();
        var nsMap       = ScanLibsForNamespaces(libsDirs, tsigCache, declaredDeps);
        var depIndex    = BuildDepIndex(libsDirs, declaredDeps);
        ScanZbcForNamespaces(BuildZbcScanDirs(), nsMap);

        // Conclusive diagnostic for the cross-platform "E0602: using `Std`:
        // no loaded package provides this namespace" build failures that
        // reproduce only on CI. When a declared dependency package was NOT
        // discovered by the libs scan (its name never appears as a zpkg in
        // any scanned dir), dump exactly what WAS scanned so the next CI run
        // pinpoints the cause (wrong cwd / walk-up miss / unwritten dist /
        // parse failure already warned above) instead of a baffling E0602.
        if (declaredDeps is { IsDeclared: true })
        {
            var foundPkgs = libsDirs
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.zpkg"))
                .Select(Path.GetFileNameWithoutExtension)
                .ToHashSet(StringComparer.Ordinal);
            var missing = declaredDeps.Entries
                .Select(e => e.Name)
                .Where(n => !foundPkgs.Contains(n))
                .ToList();
            if (missing.Count > 0)
            {
                Console.Error.WriteLine(
                    $"warning: package `{name}` declares dependencies {string.Join(", ", missing)} " +
                    $"but no matching .zpkg was found in any scanned libs dir. " +
                    $"Scanned dirs: [{string.Join(", ", libsDirs)}]. " +
                    $"This is the root cause of any subsequent `E0602: no loaded package provides this namespace`.");
            }
        }

        // C5: 增量编译查询 —— 比对上次 zpkg 与 cache zbc 的 source_hash
        string cacheDir  = explicitCacheDir ?? Path.Combine(projectDir, ".cache");
        string lastZpkg  = Path.Combine(outDir, $"{name}.zpkg");
        var probe = useIncremental
            ? new IncrementalBuild().Probe(sourceFiles, projectDir, cacheDir, lastZpkg)
            : IncrementalBuild.ProbeResult.AllFresh(sourceFiles);

        Console.Error.WriteLine($"cached: {probe.CachedCount}/{probe.TotalCount} files");

        // 2026-04-27 fix-incremental-cache-invalidation：100% 命中且 lastZpkg 存在
        // → 完全跳过重建，保留现有 zpkg 文件不动。
        //
        // 原因：cached 文件单独重组 zpkg 时，zpkg.ExportedModules 来自上次 zpkg
        // 的 per-namespace TSIG record；ExportedTypeExtractor 重新写出时可能丢失
        // 部分元数据（具体路径未追溯，但实测：clean build → 720/720 通过；
        // 紧接着 no-change incremental rebuild → 6/720 失败）。
        //
        // 既然 cached path 没有任何"新"信息要写入 zpkg（fresh files = 0 + 上次
        // zpkg 完整在盘上），最稳妥的做法就是：什么都不写。下次 incremental 时
        // lastZpkg 仍存在，cache zbc 仍存在，cached 命中 100%，再次跳过。
        if (useIncremental && probe.FreshFiles.Count == 0 && File.Exists(lastZpkg))
        {
            Console.Error.WriteLine($"    Finished → {outDir} (no changes; preserved existing zpkg)");
            return 0;
        }

        // 仅 fresh files 走完整 parse + typecheck + irgen；cached 注入 ExportedModule
        var freshUnits = TryCompileSourceFiles(probe.FreshFiles, name, depIndex, tsigCache, probe.CachedExportsByNs);
        if (freshUnits is null) return 1;

        // 重建 cached CU：cache zbc 是 fullMode（含 STRS/TYPE/SIGS/EXPT/IMPT 完整 sections），
        // ZbcReader.Read 直接反序列化为完整 IrModule。UsedDepNamespaces 从上次 zpkg.Dependencies
        // 恢复（让 BuildDependencyMap 能回填 deps；否则 cached path 写出的 zpkg dependencies 为空，
        // VM 找不到外部 zpkg 中的函数）。
        var lastDepNs = probe.LastZpkgDepNamespaces.ToList();
        var cachedUnits = new List<CompiledUnit>(probe.CachedZbcByFile.Count);
        foreach (var (sourceFile, zbcBytes) in probe.CachedZbcByFile)
        {
            var module = ZbcReader.Read(zbcBytes);
            string ns  = probe.CachedNamespaceByFile[sourceFile];
            var exportedMod = probe.CachedExportsByNs.TryGetValue(ns, out var em) ? em : null;
            var exports = module.Functions.Select(f => f.Name).ToList();
            string sourceHash;
            try { sourceHash = Sha256Hex(File.ReadAllText(sourceFile)); }
            catch { sourceHash = ""; }
            cachedUnits.Add(new CompiledUnit(
                sourceFile, ns, sourceHash, exports, module,
                Usings: [], UsedDepNamespaces: lastDepNs, ExportedTypes: exportedMod));
        }

        var units = freshUnits.Concat(cachedUnits).ToList();

        WarnUnresolvedUsings(units, nsMap);

        var dependencies = BuildDependencyMap(units, nsMap);
        var zbcFiles     = units.Select(u => u.ToZbcFile()).ToList();
        var exportedModules = units
            .Where(u => u.ExportedTypes != null)
            .Select(u => u.ExportedTypes!)
            .ToList();

        // 2026-05-14 auto-detect-main: `[project].entry` / `[[exe]].entry` is
        // optional. When unset for exe targets, scan compiled units for a
        // `Main` candidate and bake it into the zpkg. No candidate → error.
        if (kind == ZpkgKind.Exe && string.IsNullOrWhiteSpace(entry))
        {
            var (autoEntry, ambiguityError) = AutoDetectEntry(units);
            if (ambiguityError is not null)
            {
                Console.Error.WriteLine($"error: {ambiguityError}");
                return 1;
            }
            if (autoEntry is null)
            {
                Console.Error.WriteLine(
                    $"error: no `Main()` function found in exe target `{name}`. " +
                    $"Define a `Main()` (optionally inside a namespace) in source, " +
                    $"or set `[project].entry` / `[[exe]].entry` explicitly.");
                return 1;
            }
            entry = autoEntry;
            Console.Error.WriteLine($"    Entry: {entry} (auto-detected)");
        }
        else if (kind == ZpkgKind.Exe && !string.IsNullOrWhiteSpace(entry))
        {
            // add-manifest-hygiene-warnings (2026-06-04): WS009 — if the
            // explicit `entry` is what auto-detect would have picked
            // anyway, nudge the user to drop the line.
            var (autoEntry, _) = AutoDetectEntry(units);
            if (autoEntry is not null && string.Equals(autoEntry, entry, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    Z42.Project.Z42Errors.RedundantEntryKey(
                        $"(target {name})", "project", entry!).Message);
            }
        }

        ZpkgFile zpkg;
        if (pack)
        {
            // packed 模式同时写 zbc 散文件到 cache（增量编译物质基础；zpkg 内仍 inline）
            zpkg = ZpkgBuilder.BuildPacked(
                name, version, kind, entry, zbcFiles, dependencies, exportedModules,
                projectDir: projectDir, cacheDir: cacheDir);
            foreach (var zbc in zbcFiles)
            {
                string relSrc  = Path.GetRelativePath(projectDir, zbc.SourceFile);
                string zbcPath = Path.Combine(cacheDir, Path.ChangeExtension(relSrc, ".zbc"));
                Console.Error.WriteLine($"cached → {zbcPath}");
            }
        }
        else
        {
            var (indexedZpkg, writtenPaths) = ZpkgBuilder.BuildIndexed(
                name, version, kind, entry, zbcFiles, dependencies,
                projectDir, cacheDir, outDir);
            foreach (var p in writtenPaths)
                Console.Error.WriteLine($"wrote → {p}");
            zpkg = indexedZpkg;
        }

        var (zpkgPath, sidecarPath) = ZpkgBuilder.WriteZpkgWithSidecar(zpkg, name, outDir, stripSymbols);
        Console.Error.WriteLine($"wrote → {zpkgPath}");
        if (sidecarPath is not null)
            Console.Error.WriteLine($"wrote → {sidecarPath}");
        Console.Error.WriteLine($"    Finished → {outDir}");

        // restructure-publish-output-dirs (2026-06-19): auto-publish step.
        // For exe: copy exe.zpkg + .zsym (if present) + non-stdlib deps to publishDir.
        // For lib: copy only own .zpkg + .zsym when publishDir is set explicitly.
        if (publishDir is not null)
            PublishToDir(zpkgPath, sidecarPath, kind, dependencies, libsDirs, publishDir);

        return 0;
    }

    /// <summary>
    /// Stage compiled artifacts (and for exe: non-stdlib deps) to <paramref name="publishDir"/>.
    /// For exe: copies exe.zpkg + .zsym + all non-stdlib dependency .zpkg files.
    /// For lib: copies only own lib.zpkg + .zsym (caller is responsible for deciding when to call).
    /// </summary>
    static void PublishToDir(
        string zpkgPath,
        string? sidecarPath,
        ZpkgKind kind,
        IReadOnlyList<ZpkgDep> dependencies,
        string[] libsDirs,
        string publishDir)
    {
        Directory.CreateDirectory(publishDir);

        // Copy own artifact
        string destZpkg = Path.Combine(publishDir, Path.GetFileName(zpkgPath));
        File.Copy(zpkgPath, destZpkg, overwrite: true);
        Console.Error.WriteLine($"publish → {destZpkg}");

        if (sidecarPath is not null && File.Exists(sidecarPath))
        {
            string destSym = Path.Combine(publishDir, Path.GetFileName(sidecarPath));
            File.Copy(sidecarPath, destSym, overwrite: true);
            Console.Error.WriteLine($"publish → {destSym}");
        }

        // For exe: copy non-stdlib dependencies (stdlib = name starts with "z42.")
        if (kind == ZpkgKind.Exe)
        {
            foreach (var dep in dependencies)
            {
                string depFile = dep.File;
                string depName = Path.GetFileNameWithoutExtension(depFile);
                if (depName.StartsWith("z42.", StringComparison.Ordinal)) continue;

                // Find the dep in libsDirs
                string? depPath = null;
                foreach (var dir in libsDirs)
                {
                    string candidate = Path.Combine(dir, depFile);
                    if (File.Exists(candidate)) { depPath = candidate; break; }
                }
                if (depPath is null) continue;

                string destDep = Path.Combine(publishDir, depFile);
                File.Copy(depPath, destDep, overwrite: true);
                Console.Error.WriteLine($"publish → {destDep}");

                // Also copy .zsym sidecar for the dep if present
                string depSymFile = Path.ChangeExtension(depFile, ".zsym");
                string? depSymPath = null;
                foreach (var dir in libsDirs)
                {
                    string candidate = Path.Combine(dir, depSymFile);
                    if (File.Exists(candidate)) { depSymPath = candidate; break; }
                }
                if (depSymPath is not null)
                {
                    string destDepSym = Path.Combine(publishDir, depSymFile);
                    File.Copy(depSymPath, destDepSym, overwrite: true);
                    Console.Error.WriteLine($"publish → {destDepSym}");
                }
            }
        }
    }

    // ── Build target sub-steps ────────────────────────────────────────────────

    /// 2026-05-14 auto-detect-main: scan `CompiledUnit.Exports` for a Main
    /// candidate. Priority (mirrors `src/runtime/src/vm.rs::resolve_entry`):
    ///   1. any FQ name ending in `.Main`
    ///   2. bare `Main`
    ///   3. any FQ name ending in `.main`
    ///   4. bare `main`
    /// Returns (entry, error). error != null → ambiguity (multiple .Main); the
    /// caller aborts. Both null → no candidate at all.
    static (string? Entry, string? Error) AutoDetectEntry(IEnumerable<CompiledUnit> units)
    {
        var fnNames = units.SelectMany(u => u.Exports).ToHashSet(StringComparer.Ordinal);

        static (string?, string?) PickFrom(List<string> candidates, string kindLabel)
        {
            if (candidates.Count == 1) return (candidates[0], null);
            if (candidates.Count >  1) return (null,
                $"multiple `{kindLabel}` functions found ({string.Join(", ", candidates)}); " +
                $"set `[project].entry` (or `[[exe]].entry`) explicitly to pick one");
            return (null, null);
        }

        var qualifiedMain = fnNames.Where(n => n.EndsWith(".Main", StringComparison.Ordinal))
                                   .OrderBy(s => s, StringComparer.Ordinal)
                                   .ToList();
        var (e1, err1) = PickFrom(qualifiedMain, "Main");
        if (err1 is not null || e1 is not null) return (e1, err1);

        if (fnNames.Contains("Main")) return ("Main", null);

        var qualifiedMainLc = fnNames.Where(n => n.EndsWith(".main", StringComparison.Ordinal))
                                     .OrderBy(s => s, StringComparer.Ordinal)
                                     .ToList();
        var (e2, err2) = PickFrom(qualifiedMainLc, "main");
        if (err2 is not null || e2 is not null) return (e2, err2);

        if (fnNames.Contains("main")) return ("main", null);

        return (null, null);
    }


    /// Scan .zpkg files in libs dirs and build a namespace → filename map.
    /// Build the list of directories to scan for dependency `.zpkg` files.
    ///
    /// 搜索路径包括（current layout, redesign-artifact-layout 2026-05-12）：
    ///   - project-local `libs/`
    ///   - `artifacts/build/libraries/&lt;member&gt;/&lt;profile&gt;/dist/`（workspace 当前布局）
    ///   - `artifacts/packages/&lt;pkg&gt;/libs/`（已组装包；分发版扁平布局）
    ///
    /// Legacy 兼容（pre-2026-05-12，过渡期保留）：
    ///   - `artifacts/z42/libs/`（旧 package.sh 同步目标）
    ///   - `artifacts/libraries/&lt;member&gt;/dist/`（旧 workspace 布局）
    ///
    /// 沿目录树向上搜索，让 stdlib 包先后编译时能找到上游 zpkg。
    internal static string[] BuildLibsDirs(string projectDir, IReadOnlyList<string>? workspaceLibDirs = null)
    {
        var dirs = new List<string>();

        void AddDirIfExists(string path)
        {
            if (Directory.Exists(path) && !dirs.Contains(path))
                dirs.Add(path);
        }

        // 当前 workspace 布局：<root>/artifacts/build/libraries/<member>/<profile>/dist/
        // common-pitfalls.md §1: EnumerateDirectories 顺序随 OS / FS 变化，必须显式
        // 排序——否则 dirs 顺序非确定，下游 ScanLibsForNamespaces 的 first-wins
        // (nsMap.TryAdd) 在不同平台解析到不同 zpkg，导致跨平台编译结果漂移。
        void AddNewWorkspaceLayout(string baseDir)
        {
            if (!Directory.Exists(baseDir)) return;
            foreach (var memberDir in Directory.EnumerateDirectories(baseDir).OrderBy(p => p, StringComparer.Ordinal))
                foreach (var profileDir in Directory.EnumerateDirectories(memberDir).OrderBy(p => p, StringComparer.Ordinal))
                    AddDirIfExists(Path.Combine(profileDir, "dist"));
        }

        // 当前 packages 布局：<root>/artifacts/packages/<pkg>/libs/
        void AddPackagesLayout(string baseDir)
        {
            if (!Directory.Exists(baseDir)) return;
            foreach (var pkgDir in Directory.EnumerateDirectories(baseDir).OrderBy(p => p, StringComparer.Ordinal))
                AddDirIfExists(Path.Combine(pkgDir, "libs"));
        }

        // Legacy 布局：直接加 baseDir + 扫一层 <member>/dist/
        void AddLegacyCandidate(string baseDir)
        {
            AddDirIfExists(baseDir);
            if (Directory.Exists(baseDir))
                foreach (var sub in Directory.EnumerateDirectories(baseDir).OrderBy(p => p, StringComparer.Ordinal))
                    AddDirIfExists(Path.Combine(sub, "dist"));
        }

        // project-local
        AddDirIfExists(Path.Combine(projectDir, "libs"));
        AddNewWorkspaceLayout(Path.Combine(projectDir, "artifacts", "build", "libraries"));
        AddPackagesLayout(Path.Combine(projectDir, "artifacts", "packages"));
        AddLegacyCandidate(Path.Combine(projectDir, "artifacts", "z42", "libs"));
        AddLegacyCandidate(Path.Combine(projectDir, "artifacts", "libraries"));

        // walk-up search
        var dir = new DirectoryInfo(projectDir).Parent;
        while (dir != null)
        {
            AddNewWorkspaceLayout(Path.Combine(dir.FullName, "artifacts", "build", "libraries"));
            AddPackagesLayout(Path.Combine(dir.FullName, "artifacts", "packages"));
            AddLegacyCandidate(Path.Combine(dir.FullName, "artifacts", "z42", "libs"));
            AddLegacyCandidate(Path.Combine(dir.FullName, "artifacts", "libraries"));
            dir = dir.Parent;
        }

        // Current-workspace sibling members (scaffold-z42c-selfhost, 2026-06-07):
        // a workspace's members must resolve their *declared* sibling deps from the
        // workspace being built — regardless of where that workspace outputs. The
        // AddNewWorkspaceLayout scans above only cover artifacts/build/libraries
        // (stdlib's output root); a workspace that outputs elsewhere (e.g. src/z42c
        // → artifacts/build/z42c) would otherwise never find its siblings. The
        // WorkspaceBuildOrchestrator passes every member's resolved dist dir here.
        //
        // Deduped by NORMALIZED full path so a workspace already outputting under a
        // scanned root (stdlib) sees NO change to the dirs list (its sibling dists
        // are already present) — zero byte drift for existing artifacts. Only a
        // workspace outputting elsewhere actually gains entries. Sorted for
        // cross-platform determinism (common-pitfalls.md §1) since dirs order feeds
        // the first-wins nsMap / BuildDepIndex downstream. Single-project builds
        // pass null → behaviour unchanged.
        if (workspaceLibDirs is { Count: > 0 })
        {
            static string Norm(string p) { try { return Path.GetFullPath(p); } catch { return p; } }
            var seen = new HashSet<string>(dirs.Select(Norm), StringComparer.Ordinal);
            foreach (var raw in workspaceLibDirs.OrderBy(p => p, StringComparer.Ordinal))
            {
                string full = Norm(raw);
                if (Directory.Exists(full) && seen.Add(full))
                    dirs.Add(full);
            }
        }

        // Z42_LIBS fallback: honor the runtime lib-search env at compile time too,
        // added LAST so it only supplies dependencies the layout/walk-up scan did
        // not already find. This lets a prebuilt / downloaded stdlib (e.g. the
        // install-z42 `.z42/libs` flat dir) satisfy `using Std.*` without first
        // rebuilding stdlib from source — used by the CI xtask-bootstrap composite
        // to compile xtask.zpkg against the downloaded nightly stdlib. Split on the
        // platform path separator so several dirs may be supplied.
        var z42Libs = Environment.GetEnvironmentVariable("Z42_LIBS");
        if (!string.IsNullOrWhiteSpace(z42Libs))
        {
            foreach (var d in z42Libs.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AddDirIfExists(d);
        }

        return dirs.ToArray();
    }

    /// Also populates the TsigCache with namespace → zpkg path mappings for on-demand loading.
    /// When `declaredDeps` has entries, only declared packages + stdlib (z42.*) are visible.
    static Dictionary<string, string> ScanLibsForNamespaces(
        string[] libsDirs, TsigCache? tsigCache = null,
        DependencySection? declaredDeps = null)
    {
        var allowedPkgs = declaredDeps is { IsDeclared: true }
            ? declaredDeps.Entries.Select(d => d.Name).ToHashSet(StringComparer.Ordinal)
            : null; // null = auto-scan (allow all)

        var nsMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var libsDir in libsDirs)
        {
            if (!Directory.Exists(libsDir)) continue;
            // fix-depindex-nondeterministic-order (2026-05-17): same as in
            // BuildDepIndex — sort prelude (z42.core) first to pin the
            // `<ShortClass>.<Method>` first-wins resolution.
            var sortedPaths = Directory.EnumerateFiles(libsDir, "*.zpkg")
                .OrderBy(p => {
                    string name = Path.GetFileNameWithoutExtension(p);
                    return PreludePackages.Names.Contains(name) ? "0_" + name : "1_" + name;
                }, StringComparer.Ordinal);
            foreach (var zpkgFile in sortedPaths)
            {
                try
                {
                    var bytes = File.ReadAllBytes(zpkgFile);
                    var meta  = ZpkgReader.ReadMeta(bytes);
                    // Filter: stdlib (z42.*) always visible; third-party only if declared.
                    bool isStdlib = meta.Name.StartsWith("z42.", StringComparison.Ordinal);
                    if (allowedPkgs != null && !isStdlib && !allowedPkgs.Contains(meta.Name))
                        continue;

                    string fname = Path.GetFileName(zpkgFile);
                    foreach (var n in meta.Namespaces)
                    {
                        nsMap.TryAdd(n, fname);
                        tsigCache?.RegisterNamespace(n, zpkgFile);
                        // strict-using-resolution: W0603 reserved-prefix warning.
                        // 非 stdlib 包（非 z42.* 开头）声明 Std / Std.* 命名空间 → warn-only。
                        if (!PreludePackages.IsStdlibPackage(meta.Name)
                            && PreludePackages.IsReservedNamespace(n))
                        {
                            Console.Error.WriteLine(
                                $"warning W0603: package `{meta.Name}` declares reserved namespace `{n}`; " +
                                $"`Std` / `Std.*` is reserved for stdlib");
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Do NOT silently swallow: a failed read of a stdlib zpkg
                    // (e.g. z42.core providing `Std`) makes its namespaces
                    // vanish from nsMap, which surfaces downstream as a
                    // confusing `E0602: using Std: no loaded package provides
                    // this namespace` cascade in dependent packages. Surface
                    // the real cause so CI shows WHY a namespace went missing.
                    Console.Error.WriteLine(
                        $"warning: failed to read zpkg metadata from `{zpkgFile}`: {ex.Message}");
                }
            }
        }
        return nsMap;
    }

    /// Return the list of directories to scan for .zbc files (Z42_PATH + cwd + cwd/modules).
    static List<string> BuildZbcScanDirs()
    {
        var dirs   = new List<string>();
        var z42Path = Environment.GetEnvironmentVariable("Z42_PATH");
        if (!string.IsNullOrEmpty(z42Path))
            dirs.AddRange(z42Path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        var cwd = Directory.GetCurrentDirectory();
        dirs.Add(cwd);
        dirs.Add(Path.Combine(cwd, "modules"));
        return dirs;
    }

    /// Scan .zbc files and add/override namespace → filename entries in <paramref name="nsMap"/>.
    static void ScanZbcForNamespaces(IEnumerable<string> dirs, Dictionary<string, string> nsMap)
    {
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            // common-pitfalls.md §1: sort for cross-platform determinism.
            foreach (var zbcFile in Directory.EnumerateFiles(dir, "*.zbc").OrderBy(p => p, StringComparer.Ordinal))
            {
                try
                {
                    var bytes = File.ReadAllBytes(zbcFile);
                    var ns    = ZbcReader.ReadNamespace(bytes);
                    if (string.IsNullOrEmpty(ns)) continue;
                    nsMap[ns] = Path.GetFileName(zbcFile);
                }
                catch { /* skip malformed zbc */ }
            }
        }
    }

    /// Compile all source files; returns null and prints an error count if any file fails.
    /// <paramref name="cachedExports"/> 当 IncrementalBuild 命中 cache 时由 caller 提供，
    /// 用于把 cached CU 的 ExportedModule 注入 sharedCollector（与跨包 imports 同路径）。
    ///
    /// strict-using-resolution (2026-04-28): 重构为"先 parse all → 收集 cu.Usings →
    /// LoadExternalImported(activated)"两步，让 Pass-0 collect 看到的 imported 是
    /// 按用户 using 严格过滤后的（prelude + activated 包），与运行时语义一致。
    static List<CompiledUnit>? TryCompileSourceFiles(
        IReadOnlyList<string>     sourceFiles,
        string                    packageName,
        DependencyIndex           depIndex,
        TsigCache?                tsigCache = null,
        IReadOnlyDictionary<string, ExportedModule>? cachedExports = null)
    {
        // ── Phase 0: parse all source files (no Collect yet, need cu.Usings first) ──
        var parsedCus = new List<(string file, string source, CompilationUnit cu, string ns)>();
        int parseErrors = 0;
        foreach (var sourceFile in sourceFiles)
        {
            string source;
            try   { source = File.ReadAllText(sourceFile); }
            catch { Console.Error.WriteLine($"error: cannot read {sourceFile}"); parseErrors++; continue; }

            var diags  = new DiagnosticBag();
            var tokens = new Lexer(source, sourceFile).Tokenize();
            var parser = new Parser(tokens, LanguageFeatures.Phase1);
            CompilationUnit cu;
            try   { cu = parser.ParseCompilationUnit(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: parse failed in {sourceFile}: {ex.Message}");
                parseErrors++;
                continue;
            }
            foreach (var d in parser.Diagnostics.All) diags.Add(d);
            if (diags.HasErrors)
            {
                foreach (var d in diags.All)
                    Console.Error.WriteLine(d.ToString());
                parseErrors++;
                continue;
            }
            // C11b — synthesize ClassDecls for `import T from "lib";` declarations.
            try
            {
                Z42.Semantics.Synthesis.NativeImportSynthesizer.Run(
                    cu,
                    new Z42.Semantics.Synthesis.DefaultNativeManifestLocator(),
                    sourceDir: Path.GetDirectoryName(sourceFile));
            }
            catch (Z42.Semantics.Synthesis.NativeImportException ex)
            {
                Console.Error.WriteLine($"error[{ex.Code}]: {ex.Message}");
                parseErrors++;
                continue;
            }
            catch (Z42.Project.NativeManifestException ex)
            {
                Console.Error.WriteLine($"error[{ex.Code}]: {ex.Message}");
                parseErrors++;
                continue;
            }

            // simplify-stdlib-auto-import (2026-06-06): `Std` / `Std.*` is reserved
            // for the standard library — a third-party package declaring it in its
            // own source is a hard error (E0605). See CheckReservedNamespaceDeclaration.
            var reservedNsDiag = CheckReservedNamespaceDeclaration(packageName, cu.Namespace, cu.Span);
            if (reservedNsDiag is not null)
            {
                Console.Error.WriteLine(reservedNsDiag.ToString());
                parseErrors++;
                continue;
            }

            string ns = cu.Namespace ?? "main";
            parsedCus.Add((sourceFile, source, cu, ns));
        }
        if (parseErrors > 0)
        {
            Console.Error.WriteLine($"error: build failed ({parseErrors} file(s) with parse errors)");
            return null;
        }

        // 收集所有 CU 的 using 声明，得到本次编译需激活的 namespace 全集。
        var allUserUsings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, _, cu, _) in parsedCus)
            foreach (var u in cu.Usings)
                allUserUsings.Add(u);

        // ── Load external imports filtered by activated packages (prelude + user usings) ──
        var externalImported = LoadExternalImported(tsigCache, allUserUsings, cachedExports);

        // ── Phase 1: Pass-0 collect intra-package declarations ──
        var sharedDiags     = new DiagnosticBag();
        var sharedCollector = new SymbolCollector(sharedDiags);
        SymbolTable? sharedSymbols = null;
        foreach (var (_, _, cu, _) in parsedCus)
        {
            sharedSymbols = sharedCollector.Collect(cu, externalImported);
        }

        // Global inheritance merge: ensures derived classes carry their full
        // base-chain Fields/Methods regardless of CU processing order.
        sharedCollector.FinalizeInheritance();
        // fix-memorystream-override-visibility (2026-05-24): run deferred override
        // validation now that all CUs are collected. Per-CU run missed same-package
        // cross-file base classes (e.g. `MemoryStream : Stream` in z42.io where
        // alphabetical order processes MemoryStream first).
        sharedCollector.FinalizeOverrideChecks();
        // Re-snapshot SymbolTable so ExtractIntraSymbols sees finalized classes.
        // FinalizeInheritance mutates _classes in place; the SymbolTable returned
        // from the last Collect() call holds references to the same dictionaries,
        // but to be explicit we extract from a fresh snapshot.
        // 2026-04-28 fix-intra-package-namespace：构建 per-class namespace map，
        // 同包多 namespace（z42.core 同时含 `Std` / `Std.Collections` / `Std.IO`）
        // 时各 class 携带其本身的 namespace，否则跨 namespace 的同包 `new T(...)`
        // 会被发射成错误前缀（runtime 找不到类型，构造器不写字段）。
        var classNamespaces = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (_, _, cu, ns) in parsedCus)
            foreach (var cls in cu.Classes)
                classNamespaces.TryAdd(cls.Name, ns);
        // fix-intra-package-resolved-ns (2026-05-28): collect every namespace
        // declared by any unit in this package (including impl-only files
        // with no classes — they wouldn't show up in classNamespaces).
        // Without this, a unit's `using` declaration referencing a sibling
        // namespace within the same package raises E0602 on a clean build.
        var intraPkgNs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, _, _, ns) in parsedCus)
            if (ns is not null) intraPkgNs.Add(ns);
        var intraSymbols = sharedSymbols is null
            ? ImportedSymbolLoader.Empty()
            : sharedSymbols.ExtractIntraSymbols(
                parsedCus.FirstOrDefault().ns ?? "main", classNamespaces, intraPkgNs);

        // ── Phase 2: full compile each CU with combined imports ──
        var combined = ImportedSymbolLoader.Combine(externalImported, intraSymbols);

        var units  = new List<CompiledUnit>();
        int errors = 0;
        foreach (var (sourceFile, source, _, _) in parsedCus)
        {
            var unit = CompileFile(sourceFile, depIndex, source, combined);
            if (unit is null) { errors++; continue; }
            units.Add(unit);
        }
        if (errors > 0)
        { Console.Error.WriteLine($"error: build failed ({errors} file(s) with errors)"); return null; }
        return units;
    }

    /// 加载所有外部 zpkg 的 ImportedSymbols（来自 tsigCache），按用户 using 严格过滤。
    ///
    /// strict-using-resolution (2026-04-28): 只激活
    ///   - prelude 包（PreludePackages.Names，当前仅 z42.core）
    ///   - 用户 using 指向的 namespace 所在的所有包
    /// 其他包的类型不进入 ImportedSymbols；TypeChecker 引用时报 E0401。
    /// 同 (namespace, class-name) 多 package → 写入 ImportedSymbols.Collisions，
    /// TypeChecker 报 E0601。
    private static ImportedSymbols LoadExternalImported(
        TsigCache?                                  tsigCache,
        IReadOnlyCollection<string>                 userUsings,
        IReadOnlyDictionary<string, ExportedModule>? cachedExports = null)
    {
        if (tsigCache is null) return ImportedSymbolLoader.Empty();

        // 计算激活包：prelude ∪ (用户 using 指向的 namespace 所在的所有包)
        var activatedPkgs = new HashSet<string>(PreludePackages.Names, StringComparer.Ordinal);
        foreach (var ns in userUsings)
            foreach (var pkg in tsigCache.PackagesProvidingNamespace(ns))
                activatedPkgs.Add(pkg);

        var (tsigModules, packageOf) = tsigCache.LoadForPackages(activatedPkgs);
        if (cachedExports is not null)
        {
            // cached intra-package modules：去重后追加。cached 模块没有具体 zpkg
            // 来源（同包 fresh build），用 "<intra>" 占位作为 packageName，确保
            // 进入 activated 集合（intra 包永远是"prelude 之外的当前包"）。
            const string IntraPkg = "<intra>";
            activatedPkgs.Add(IntraPkg);
            foreach (var (ns, exp) in cachedExports)
            {
                tsigModules.RemoveAll(m => string.Equals(m.Namespace, ns, StringComparison.Ordinal));
                tsigModules.Add(exp);
                packageOf[exp] = IntraPkg;
            }
        }
        if (tsigModules.Count == 0) return ImportedSymbolLoader.Empty();
        return ImportedSymbolLoader.Load(tsigModules, packageOf, activatedPkgs,
            preludePackages: PreludePackages.Names);
    }

    /// Two-kind warning for `using` declarations:
    ///   - UNRESOLVED: namespace not provided by any library (and not
    ///     declared intra-package). Likely a typo or missing dep.
    ///   - UNUSED:     namespace IS resolved (external) but no symbol from
    ///     it was actually referenced. Likely a dead import.
    /// Intra-package usings are always exempt — they're load-bearing for
    /// the name-only method resolver (see HttpServer.Stop comment).
    static void WarnUnresolvedUsings(
        IReadOnlyList<CompiledUnit>  units,
        Dictionary<string, string>   nsMap)
    {
        foreach (var diag in FindUsingDiagnostics(units, nsMap))
        {
            string kind = diag.Kind == UsingDiagKind.Unresolved
                        ? "namespace not found in any library"
                        : "imported namespace is not used";
            Console.Error.WriteLine(
                $"warning: using '{diag.UsingNs}' in {Path.GetFileName(diag.Unit.SourceFile)}: {kind}");
        }
    }

    internal enum UsingDiagKind { Unresolved, Unused }

    internal readonly record struct UsingDiag(CompiledUnit Unit, string UsingNs, UsingDiagKind Kind);

    /// Decision-logic half of `WarnUnresolvedUsings`, exposed for unit testing.
    /// fix-warn-unresolved-intrapkg (2026-05-28) + fix-warn-unused-import
    /// (2026-05-28).
    ///
    /// Rules:
    ///   - intra-package using   → no warning
    ///   - external + in nsMap + used → no warning
    ///   - external + in nsMap + NOT in UsedDepNamespaces → Unused
    ///   - external + NOT in nsMap → Unresolved
    internal static IEnumerable<UsingDiag> FindUsingDiagnostics(
        IReadOnlyList<CompiledUnit>          units,
        IReadOnlyDictionary<string, string>  nsMap)
    {
        var intraPkgNs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in units)
            intraPkgNs.Add(unit.Namespace);

        foreach (var unit in units)
        {
            // Per-unit set of usings that actually contributed symbols.
            var usedNs = new HashSet<string>(unit.UsedDepNamespaces, StringComparer.Ordinal);
            // The unit's own namespace is implicitly "used" (members reference
            // each other without needing a `using` declaration).
            usedNs.Add(unit.Namespace);
            foreach (var usingNs in unit.Usings)
            {
                if (intraPkgNs.Contains(usingNs)) continue;   // always OK
                if (!nsMap.ContainsKey(usingNs))
                {
                    yield return new UsingDiag(unit, usingNs, UsingDiagKind.Unresolved);
                    continue;
                }
                if (!usedNs.Contains(usingNs))
                    yield return new UsingDiag(unit, usingNs, UsingDiagKind.Unused);
            }
        }
    }

    /// simplify-stdlib-auto-import (2026-06-06): the `Std` / `Std.*` namespace
    /// prefix is reserved for the standard library (z42.* packages), the same way
    /// Rust reserves `std` / `core` / `alloc`. A third-party package (name not
    /// starting with `z42.`) declaring such a namespace in its own source is a hard
    /// error (E0605) — this guarantees every `Std.*` a program references resolves
    /// to the official, auto-available stdlib and can never be shadowed by a
    /// third-party package.
    ///
    /// W0603 is the softer dependency-scan counterpart (warns when *consuming* an
    /// already-built zpkg that squats the prefix); this is the source-level gate
    /// that prevents producing such a package in the first place.
    ///
    /// Returns the E0605 diagnostic to emit, or null when the declaration is fine
    /// (stdlib package, no namespace, or a non-reserved namespace). Exposed
    /// `internal` for unit testing the decision logic without a full build.
    internal static Diagnostic? CheckReservedNamespaceDeclaration(
        string packageName, string? declaredNamespace, Span span)
    {
        if (PreludePackages.IsStdlibPackage(packageName)) return null;     // z42.* may use Std.*
        if (declaredNamespace is null) return null;                       // default namespace
        if (!PreludePackages.IsReservedNamespace(declaredNamespace)) return null;

        return Diagnostic.Error(
            DiagnosticCodes.ReservedNamespaceDeclaration,
            $"namespace `{declaredNamespace}` is reserved for the standard library; " +
            $"package `{packageName}` (third-party) must not declare it — " +
            $"`Std` / `Std.*` belong to z42.* stdlib packages only. " +
            $"Rename it to your own prefix.",
            span);
    }

    /// Back-compat alias for tests that only care about unresolved (not unused).
    internal static IEnumerable<(CompiledUnit Unit, string UsingNs)> FindUnresolvedUsings(
        IReadOnlyList<CompiledUnit>          units,
        IReadOnlyDictionary<string, string>  nsMap)
    {
        foreach (var d in FindUsingDiagnostics(units, nsMap))
            if (d.Kind == UsingDiagKind.Unresolved)
                yield return (d.Unit, d.UsingNs);
    }

    /// Build the dependency list (file → namespaces) from resolved usings and dependency calls.
    static List<ZpkgDep> BuildDependencyMap(
        IReadOnlyList<CompiledUnit>  units,
        Dictionary<string, string>   nsMap)
    {
        var depMap = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void AddDepNs(string ns)
        {
            if (!nsMap.TryGetValue(ns, out var depFile)) return;
            if (!depMap.TryGetValue(depFile, out var nsList))
            {
                nsList = [];
                depMap[depFile] = nsList;
            }
            if (!nsList.Contains(ns)) nsList.Add(ns);
        }

        foreach (var unit in units)
        {
            foreach (var usingNs in unit.Usings)     AddDepNs(usingNs);
            foreach (var stdNs in unit.UsedDepNamespaces) AddDepNs(stdNs);
        }
        return [.. depMap.Select(kv => new ZpkgDep(kv.Key, kv.Value))];
    }
}
