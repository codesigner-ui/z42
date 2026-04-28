using Z42.Core;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
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
        bool                  useIncremental = true)
    {
        // L3-G4g: libsDirs includes projectDir/libs, projectDir/artifacts/z42/libs,
        // plus a walk-up search for the repo-level `artifacts/z42/libs` so stdlib
        // packages compiling against each other (e.g. z42.collections → z42.core)
        // can resolve their dependencies.
        var libsDirs    = BuildLibsDirs(projectDir);
        var tsigCache   = new TsigCache();
        var nsMap       = ScanLibsForNamespaces(libsDirs, tsigCache, declaredDeps);
        var depIndex    = BuildDepIndex(libsDirs, declaredDeps);
        ScanZbcForNamespaces(BuildZbcScanDirs(), nsMap);

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
        var freshUnits = TryCompileSourceFiles(probe.FreshFiles, depIndex, tsigCache, probe.CachedExportsByNs);
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

        string zpkgPath = ZpkgBuilder.WriteZpkg(zpkg, name, outDir);
        Console.Error.WriteLine($"wrote → {zpkgPath}");
        Console.Error.WriteLine($"    Finished → {outDir}");
        return 0;
    }

    // ── Build target sub-steps ────────────────────────────────────────────────

    /// Scan .zpkg files in libs dirs and build a namespace → filename map.
    /// Build the list of directories to scan for dependency `.zpkg` files.
    ///
    /// 搜索路径包括：
    ///   - project-local `libs/`
    ///   - `artifacts/z42/libs/`（legacy + 分发版扁平布局）
    ///   - `artifacts/libraries/`（workspace 扁平布局兼容；C4c 旧）
    ///   - `artifacts/libraries/&lt;member&gt;/dist/`（workspace 子目录布局；C4c+ 当前）
    ///
    /// 沿目录树向上搜索，让 stdlib 包先后编译时能找到上游 zpkg。
    static string[] BuildLibsDirs(string projectDir)
    {
        var dirs = new List<string>();

        void AddCandidate(string baseDir)
        {
            // 直接加 baseDir（兼容旧扁平布局）
            if (Directory.Exists(baseDir) && !dirs.Contains(baseDir))
                dirs.Add(baseDir);
            // 子目录布局：扫一层，加每个 <member>/dist/
            if (Directory.Exists(baseDir))
            {
                foreach (var sub in Directory.EnumerateDirectories(baseDir))
                {
                    string distSub = Path.Combine(sub, "dist");
                    if (Directory.Exists(distSub) && !dirs.Contains(distSub))
                        dirs.Add(distSub);
                }
            }
        }

        // project-local
        AddCandidate(Path.Combine(projectDir, "libs"));
        AddCandidate(Path.Combine(projectDir, "artifacts", "z42", "libs"));
        AddCandidate(Path.Combine(projectDir, "artifacts", "libraries"));

        // walk-up search
        var dir = new DirectoryInfo(projectDir).Parent;
        while (dir != null)
        {
            AddCandidate(Path.Combine(dir.FullName, "artifacts", "z42", "libs"));
            AddCandidate(Path.Combine(dir.FullName, "artifacts", "libraries"));
            dir = dir.Parent;
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
            foreach (var zpkgFile in Directory.EnumerateFiles(libsDir, "*.zpkg"))
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
                catch { /* skip malformed zpkg */ }
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
            foreach (var zbcFile in Directory.EnumerateFiles(dir, "*.zbc"))
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
        var intraSymbols = sharedSymbols is null
            ? ImportedSymbolLoader.Empty()
            : sharedSymbols.ExtractIntraSymbols(
                parsedCus.FirstOrDefault().ns ?? "main", classNamespaces);

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

    /// Emit warnings for `using` declarations that cannot be resolved in <paramref name="nsMap"/>.
    static void WarnUnresolvedUsings(
        IReadOnlyList<CompiledUnit>  units,
        Dictionary<string, string>   nsMap)
    {
        if (nsMap.Count == 0) return;
        foreach (var unit in units)
            foreach (var usingNs in unit.Usings)
                if (!nsMap.ContainsKey(usingNs))
                    Console.Error.WriteLine(
                        $"warning: using '{usingNs}' in {Path.GetFileName(unit.SourceFile)}: namespace not found in any library");
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
