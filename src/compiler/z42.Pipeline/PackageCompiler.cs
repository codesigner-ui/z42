using Z42.IR;
using Z42.Project;

namespace Z42.Pipeline;

/// <summary>
/// Compiles a complete z42 package (one or more source files) to a .zpkg artifact.
///
/// 拆分为多个 partial 文件：
/// • PackageCompiler.cs              — CLI 入口 (Run / RunResolved / RunCheck / BuildMultiExe)
/// • PackageCompiler.BuildTarget.cs  — 单 target 编译流水线 + 各阶段子步骤
/// • PackageCompiler.Helpers.cs      — per-file / shared 工具方法 (CompileFile / Sha256 / ExtractUsings / BuildDepIndex)
///
/// 配套类型：
/// • CompiledUnit.cs — 单文件编译结果 record
/// • TsigCache.cs    — 按需懒加载 zpkg TSIG 的缓存
/// </summary>
public static partial class PackageCompiler
{
    // ── Build ─────────────────────────────────────────────────────────────────

    public static int Run(
        string?               explicitToml,
        bool                  useRelease,
        string?               binFilter,
        bool                  useIncremental = true,
        bool?                 cliStripOverride = null)
    {
        if (!TryLoadManifest(explicitToml, out var tomlPath, out var manifest)) return 1;

        string profileLabel = useRelease ? "release" : "debug";
        string projectDir   = Path.GetDirectoryName(Path.GetFullPath(tomlPath))!;

        // restructure-build-output-dirs (2026-06-06): single-project mode reuses
        // CentralizedBuildLayout for consistent cascade defaults
        // (`output_dir` → projectDir, `cache_dir` → `${output_dir}/.cache`,
        // `dist_dir` → `${output_dir}/dist`). Member name + profile threaded
        // for template expansion symmetry with workspace mode.
        var layout = new CentralizedBuildLayout().Resolve(
            workspace:        null,
            workspaceRoot:    projectDir,
            memberName:       manifest.Project.Name,
            memberDir:        projectDir,
            profile:          profileLabel,
            memberLocalBuild: manifest.Build,
            expander:         new PathTemplateExpander());

        string outDir          = layout.EffectiveDistDir;
        string? explicitCacheDir = layout.EffectiveCacheDir;

        // 1.5b split-debug-symbols: effective `strip` resolution priority:
        //   1) CLI `--strip-symbols=...` override
        //   2) toml `[profile.*].strip`
        //   3) Built-in default (debug=false / release=true) — already in ProfileSection.DefaultDebug/Release
        bool stripSymbols = cliStripOverride ?? manifest.SelectProfile(useRelease).Strip;

        if (manifest.Project.Kind == ProjectKind.Multi)
            return BuildMultiExe(manifest, projectDir, outDir, useRelease, profileLabel, binFilter, stripSymbols);

        if (binFilter is not null)
        {
            Console.Error.WriteLine("error: --bin is only valid for projects with [[exe]] targets");
            return 1;
        }

        if (!TryResolveFiles(manifest, projectDir, null, out var sourceFiles)) return 1;

        Console.Error.WriteLine(
            $"   Compiling {manifest.Project.Name} v{manifest.Project.Version} [{profileLabel}]");

        bool pack = manifest.ResolvePack(useRelease);
        return BuildTarget(
            manifest.Project.Name,
            manifest.Project.Version,
            manifest.Project.Kind == ProjectKind.Lib ? ZpkgKind.Lib : ZpkgKind.Exe,
            manifest.Project.Entry,
            sourceFiles,
            pack,
            projectDir,
            outDir,
            manifest.Dependencies,
            explicitCacheDir: explicitCacheDir,
            useIncremental:   useIncremental,
            stripSymbols:     stripSymbols);
    }

    // ── Workspace mode entry (C4a) ────────────────────────────────────────────

    /// <summary>
    /// 编译一个 workspace member。基于 ResolvedManifest（已应用 workspace 共享 + include +
    /// policy + 集中产物布局）。供 WorkspaceBuildOrchestrator 调用。
    /// </summary>
    public static int RunResolved(ResolvedManifest member, bool useRelease, bool checkOnly,
        bool useIncremental = true, bool stripSymbols = false)
    {
        string profileLabel = useRelease ? "release" : "debug";
        string memberDir    = Path.GetDirectoryName(Path.GetFullPath(member.ManifestPath))!;
        // restructure-build-output-dirs (2026-06-06): EffectiveDistDir is
        // always populated by ManifestLoader (uses CentralizedBuildLayout
        // for both workspace and single-project paths), so the IsCentralized
        // split is no longer needed here — read directly.
        string outDir       = member.EffectiveDistDir;

        Console.Error.WriteLine(
            checkOnly
                ? $"    Checking {member.MemberName} v{member.Version}"
                : $"   Compiling {member.MemberName} v{member.Version} [{profileLabel}]");

        // 解析源文件（基于 ResolvedManifest.Sources）
        var sourceFiles = ResolveSourceFilesFromResolved(member, memberDir);
        if (sourceFiles.Count == 0)
        {
            Console.Error.WriteLine(
                $"error: no source files found in member '{member.MemberName}' " +
                $"(include: [{string.Join(", ", member.Sources.Include)}])");
            return 1;
        }

        if (checkOnly)
        {
            int errors = sourceFiles.Count(file => !CheckFile(file));
            if (errors > 0) { Console.Error.WriteLine($"error: check failed ({errors} file(s) with errors)"); return 1; }
            return 0;
        }

        // pack: workspace 模式默认随 profile（debug=false / release=true），可被 member.Pack 覆盖
        bool pack = member.Pack ?? useRelease;
        var kind = member.Kind == ProjectKind.Lib ? ZpkgKind.Lib : ZpkgKind.Exe;

        // 构造一个最小 DependencySection 供 BuildTarget 使用（已声明依赖列表）
        var declaredDeps = ResolvedDependenciesToDeclared(member.Dependencies);

        // restructure-build-output-dirs (2026-06-06): EffectiveCacheDir is
        // always populated by ManifestLoader; passing it through reaches
        // the incremental-build cache layer for both workspace and
        // single-project paths.
        string? cacheDir = member.EffectiveCacheDir;

        return BuildTarget(
            member.MemberName,
            member.Version,
            kind,
            member.Entry,
            sourceFiles,
            pack,
            memberDir,
            outDir,
            declaredDeps,
            explicitCacheDir: cacheDir,
            useIncremental: useIncremental,
            stripSymbols:   stripSymbols);
    }

    static IReadOnlyList<string> ResolveSourceFilesFromResolved(ResolvedManifest member, string memberDir)
    {
        var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher();
        foreach (var p in member.Sources.Include) matcher.AddInclude(p);
        foreach (var p in member.Sources.Exclude) matcher.AddExclude(p);

        var result = matcher.Execute(
            new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(
                new DirectoryInfo(memberDir)));

        return result.Files
            .Select(f => Path.GetFullPath(Path.Combine(memberDir, f.Path)))
            .OrderBy(f => f)
            .ToList();
    }

    static DependencySection ResolvedDependenciesToDeclared(IReadOnlyList<ResolvedDependency> deps)
    {
        if (deps.Count == 0) return new DependencySection([], false);
        var entries = deps.Select(d => new DeclaredDep(d.Name, d.Version)).ToList();
        return new DependencySection(entries, true);
    }

    // ── Check ─────────────────────────────────────────────────────────────────

    public static int RunCheck(string? explicitToml, string? binFilter)
    {
        if (!TryLoadManifest(explicitToml, out var tomlPath, out var manifest)) return 1;

        string projectDir = Path.GetDirectoryName(Path.GetFullPath(tomlPath))!;

        Console.Error.WriteLine(
            $"    Checking {manifest.Project.Name} v{manifest.Project.Version}");

        var fileSets = new List<IReadOnlyList<string>>();

        if (manifest.Project.Kind == ProjectKind.Multi)
        {
            var targets = manifest.ExeTargets;
            if (binFilter is not null)
            {
                targets = targets.Where(t => t.Name == binFilter).ToList();
                if (targets.Count == 0)
                { Console.Error.WriteLine($"error: no [[exe]] named '{binFilter}'"); return 1; }
            }
            foreach (var t in targets)
            {
                if (!TryResolveFiles(manifest, projectDir, t, out var f)) return 1;
                fileSets.Add(f);
            }
        }
        else
        {
            if (binFilter is not null)
            { Console.Error.WriteLine("error: --bin is only valid for projects with [[exe]] targets"); return 1; }
            if (!TryResolveFiles(manifest, projectDir, null, out var f)) return 1;
            fileSets.Add(f);
        }

        int errors = fileSets
            .SelectMany(s => s)
            .Count(file => !CheckFile(file));

        if (errors > 0) { Console.Error.WriteLine($"error: check failed ({errors} file(s) with errors)"); return 1; }
        Console.Error.WriteLine($"    Finished checking → ok");
        return 0;
    }

    // ── Multi-exe build ───────────────────────────────────────────────────────

    static int BuildMultiExe(
        ProjectManifest       manifest,
        string                projectDir,
        string                outDir,
        bool                  useRelease,
        string                profileLabel,
        string?               binFilter,
        bool                  stripSymbols = false)
    {
        var targets = manifest.ExeTargets;
        if (binFilter is not null)
        {
            targets = targets.Where(t => t.Name == binFilter).ToList();
            if (targets.Count == 0)
            { Console.Error.WriteLine($"error: no [[exe]] named '{binFilter}'"); return 1; }
        }

        Console.Error.WriteLine(
            $"   Compiling {manifest.Project.Name} v{manifest.Project.Version} " +
            $"[{profileLabel}] ({targets.Count} target(s))");

        int errors = 0;
        foreach (var target in targets)
        {
            string entryLabel = target.Entry ?? "Main (auto)";
            Console.Error.WriteLine($"   Compiling {target.Name} ({entryLabel})");
            if (!TryResolveFiles(manifest, projectDir, target, out var sourceFiles))
            { errors++; continue; }
            bool pack = manifest.ResolvePack(useRelease, target.Pack);
            if (BuildTarget(target.Name, manifest.Project.Version, ZpkgKind.Exe,
                    target.Entry, sourceFiles, pack, projectDir, outDir,
                    manifest.Dependencies, stripSymbols: stripSymbols) != 0)
                errors++;
        }

        if (errors > 0) { Console.Error.WriteLine($"error: build failed ({errors} error(s))"); return 1; }
        Console.Error.WriteLine($"    Finished [{profileLabel}] → {outDir}");
        return 0;
    }
}
