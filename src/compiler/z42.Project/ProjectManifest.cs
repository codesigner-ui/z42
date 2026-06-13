using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

namespace Z42.Project;

// ── TOML model ────────────────────────────────────────────────────────────────

public sealed class ProjectManifest
{
    public ProjectSection        Project      { get; init; } = new();
    public SourcesSection        Sources      { get; init; } = new();
    public BuildSection          Build        { get; init; } = new();
    public ProfileSection        Debug        { get; init; } = ProfileSection.DefaultDebug;
    public ProfileSection        Release      { get; init; } = ProfileSection.DefaultRelease;
    public IReadOnlyList<ExeTarget> ExeTargets { get; init; } = [];
    /// Declared dependencies from `[dependencies]`. Empty list = no section (auto-scan mode).
    public DependencySection     Dependencies { get; init; } = new();

    // add-tests-bench-manifest-config (2026-06-06): [tests] / [bench] / [[test]] / [[bench]] schema.
    /// `[tests]` table: shared test config (include/exclude globs + dev-deps).
    /// Defaults to `tests/*.z42` + `tests/*/source.z42` convention discovery.
    public TestBenchConfig       Tests        { get; init; } = TestBenchConfig.DefaultTests;
    /// `[bench]` table: shared bench config. Mirrors `[tests]` but rooted at `bench/`.
    public TestBenchConfig       Bench        { get; init; } = TestBenchConfig.DefaultBench;
    /// `[[test]]` array: explicit per-test overrides (name + src + sources glob + per-entry deps).
    public IReadOnlyList<TestBenchEntry> TestEntries  { get; init; } = [];
    /// `[[bench]]` array: explicit per-bench overrides.
    public IReadOnlyList<TestBenchEntry> BenchEntries { get; init; } = [];

    // ── Discovery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Find the .z42.toml file to use. Throws <see cref="ManifestException"/> on ambiguity or absence.
    /// </summary>
    public static string Discover(string dir, string? explicitPath = null)
    {
        if (explicitPath is not null)
        {
            if (!File.Exists(explicitPath))
                throw new ManifestException($"error: project file not found: {explicitPath}");
            return explicitPath;
        }

        var files = Directory.GetFiles(dir, "*.z42.toml");
        return files.Length switch
        {
            0 => throw new ManifestException("error: no .z42.toml found in current directory"),
            1 => files[0],
            _ => throw new ManifestException(
                    $"error: multiple .z42.toml files found, please specify one:\n" +
                    string.Join("\n", files.Select(f => $"  z42c build {Path.GetFileName(f)}"))),
        };
    }

    // ── Loading ───────────────────────────────────────────────────────────────

    public static ProjectManifest Load(string tomlPath) =>
        LoadWithWarnings(tomlPath).Manifest;

    /// <summary>
    /// Same as <see cref="Load(string)"/> but also returns hygiene
    /// warnings (WS008 unknown-key, WS009 redundant-entry).
    /// add-manifest-hygiene-warnings (2026-06-04).
    /// </summary>
    public static ProjectManifestLoadResult LoadWithWarnings(string tomlPath)
    {
        string toml = File.ReadAllText(tomlPath);
        var model   = TomlSerializer.Deserialize<TomlTable>(toml)
                      ?? throw new ManifestException($"error: failed to parse {tomlPath}");

        var warnings    = new List<ManifestException>();
        var exeTargets  = ParseExeTargets(model, tomlPath, warnings);
        var project     = ParseProject(model, tomlPath, exeTargets.Count > 0, warnings);
        var sources     = ParseSources(model, tomlPath, warnings);
        var build       = ParseBuild(model, tomlPath, warnings);
        var debug       = ParseProfile(model, "debug",   ProfileSection.DefaultDebug, tomlPath, warnings);
        var release     = ParseProfile(model, "release", ProfileSection.DefaultRelease, tomlPath, warnings);
        var deps        = ParseDependencies(model);
        var tests       = ParseTestBenchConfig(model, "tests", TestBenchConfig.DefaultTests,
                                               tomlPath, warnings);
        var bench       = ParseTestBenchConfig(model, "bench", TestBenchConfig.DefaultBench,
                                               tomlPath, warnings);
        var testEntries = ParseTestBenchEntries(model, "test",  tomlPath, warnings);
        var benchEntries= ParseTestBenchEntries(model, "bench", tomlPath, warnings);
        // add-tests-bench-manifest-config Phase 5 (2026-06-06): suppress
        // WS012 for synthetic test/bench harness projects. xtask's
        // dir-mode path generates `<lib>.test.<unit>` / `<lib>.bench.<unit>`
        // mini-manifests that legitimately declare z42.test in
        // [dependencies] (a synthetic test project genuinely depends on
        // the framework — there is no "release artifact" to keep clean).
        // The .test. / .bench. infix is xtask's own naming convention,
        // not a thing user-authored manifests would collide with.
        if (!IsSyntheticHarnessProject(project.Name))
            ScanDepsForTestOnlyLeaks(deps, tomlPath, warnings);
        ScanForRedundantStdlibDeps(project.Name, deps, tests, bench, tomlPath, warnings);
        ScanTopLevelKeys(model, tomlPath, warnings);
        // apphost-out-path (2026-06-10): scan [apphost] keys (consumed by the
        // z42 patcher, not z42c) so a stray key still surfaces as WS008.
        if (model.TryGetValue("apphost", out var apphostRaw) && apphostRaw is TomlTable apphostTbl)
            ScanUnknownKeys(apphostTbl, KnownApphostKeys, "apphost", tomlPath, warnings);
        // add-export-command (2026-06-14): scan [platform] subsections consumed by
        // `z42 export ios/android/wasm`; z42c does NOT consume [platform] — registered
        // only to suppress WS008 unknown-key.
        if (model.TryGetValue("platform", out var platformRaw) && platformRaw is TomlTable platformTbl)
        {
            ScanUnknownKeys(platformTbl, KnownPlatformSubsections, "platform", tomlPath, warnings);
            if (platformTbl.TryGetValue("ios", out var iosRaw) && iosRaw is TomlTable iosTbl)
                ScanUnknownKeys(iosTbl, KnownPlatformIosKeys, "platform.ios", tomlPath, warnings);
            if (platformTbl.TryGetValue("android", out var androidRaw) && androidRaw is TomlTable androidTbl)
                ScanUnknownKeys(androidTbl, KnownPlatformAndroidKeys, "platform.android", tomlPath, warnings);
            if (platformTbl.TryGetValue("wasm", out var wasmRaw) && wasmRaw is TomlTable wasmTbl)
                ScanUnknownKeys(wasmTbl, KnownPlatformWasmKeys, "platform.wasm", tomlPath, warnings);
        }

        var manifest = new ProjectManifest
        {
            Project      = project,
            Sources      = sources,
            Build        = build,
            Debug        = debug,
            Release      = release,
            ExeTargets   = exeTargets,
            Dependencies = deps,
            Tests        = tests,
            Bench        = bench,
            TestEntries  = testEntries,
            BenchEntries = benchEntries,
        };
        return new ProjectManifestLoadResult(manifest, warnings);
    }

    // ── Manifest hygiene: WS008 unknown-key scan ──────────────────────────────
    //
    // add-manifest-hygiene-warnings (2026-06-04): scan each TOML table for
    // keys outside the known set; emit WS008 per stray key. Levenshtein
    // suggestion when within edit distance 2 of a known key.

    static readonly HashSet<string> KnownTopLevelKeys = new(StringComparer.Ordinal)
    {
        "project", "sources", "build", "exe", "dependencies", "profile",
        // add-tests-bench-manifest-config (2026-06-06):
        // [tests] / [bench] tables + [[test]] / [[bench]] arrays
        "tests", "bench", "test", "benchmark",
        // apphost-out-path (2026-06-10): [apphost] declares native-apphost
        // publish. z42c does NOT consume it (the `z42 apphost build <toml>`
        // patcher reads it); registered here only so the section + its keys
        // don't trip WS008 unknown-key.
        "apphost",
        // add-export-command (2026-06-14): [platform] groups per-platform config
        // (ios/android/wasm). Consumed by `z42 export`; z42c does not read it.
        "platform",
    };
    static readonly HashSet<string> KnownApphostKeys = new(StringComparer.Ordinal)
    {
        "publish_dir",
    };
    static readonly HashSet<string> KnownPlatformSubsections = new(StringComparer.Ordinal)
    {
        "ios", "android", "wasm",
    };
    static readonly HashSet<string> KnownPlatformIosKeys = new(StringComparer.Ordinal)
    {
        "bundle_id", "display_name", "version", "min_ios", "team_id", "device_families",
    };
    static readonly HashSet<string> KnownPlatformAndroidKeys = new(StringComparer.Ordinal)
    {
        "app_id", "display_name", "version_code", "version_name", "min_sdk", "target_sdk",
    };
    static readonly HashSet<string> KnownPlatformWasmKeys = new(StringComparer.Ordinal)
    {
        "title",
    };
    static readonly HashSet<string> KnownProjectKeys = new(StringComparer.Ordinal)
    {
        "name", "version", "kind", "entry", "description", "pack",
    };
    static readonly HashSet<string> KnownExeKeys = new(StringComparer.Ordinal)
    {
        "name", "entry", "src", "pack",
    };
    static readonly HashSet<string> KnownSourcesKeys = new(StringComparer.Ordinal)
    {
        "include", "exclude",
    };
    static readonly HashSet<string> KnownBuildKeys = new(StringComparer.Ordinal)
    {
        // restructure-build-output-dirs (2026-06-06): legacy `out_dir`
        // removed; old field surfaces as WS008 unknown-key + Levenshtein
        // suggestion → `dist_dir`.
        "output_dir", "cache_dir", "dist_dir", "mode", "incremental",
    };
    static readonly HashSet<string> KnownProfileKeys = new(StringComparer.Ordinal)
    {
        "mode", "optimize", "debug", "strip", "pack",
    };

    // add-tests-bench-manifest-config (2026-06-06): known schema for the
    // new tables + entry arrays. `dependencies` is allowed under both
    // `[tests]` and `[bench]` (dev-deps; the value is a sub-table of
    // package = "version" pairs, same shape as top-level [dependencies]).
    static readonly HashSet<string> KnownTestsBenchKeys = new(StringComparer.Ordinal)
    {
        "include", "exclude", "dependencies",
    };
    static readonly HashSet<string> KnownTestBenchEntryKeys = new(StringComparer.Ordinal)
    {
        "name", "src", "sources", "dependencies",
    };

    /// <summary>
    /// True if `name` matches xtask's synthetic harness naming convention
    /// (`<lib>.test.<unit>` / `<lib>.bench.<unit>`). Used to skip
    /// hygiene checks that don't apply to xtask-generated mini-manifests.
    /// add-tests-bench-manifest-config Phase 5 (2026-06-06).
    /// </summary>
    static bool IsSyntheticHarnessProject(string name) =>
        name.Contains(".test.", StringComparison.Ordinal)
            || name.Contains(".bench.", StringComparison.Ordinal);

    // Names that designate "test-only" dependencies that should live under
    // [tests.dependencies] / [bench.dependencies] rather than [dependencies].
    // Surface WS012 if any appear at the top-level [dependencies] table.
    // Curated, not heuristic: lying low to keep false-positive rate at 0%.
    static readonly HashSet<string> KnownTestOnlyDeps = new(StringComparer.Ordinal)
    {
        "z42.test",
    };

    static void ScanUnknownKeys(
        TomlTable table, HashSet<string> known, string section,
        string tomlPath, List<ManifestException> warnings)
    {
        foreach (var key in table.Keys)
        {
            if (known.Contains(key)) continue;
            string? suggestion = NearestKnown(key, known);
            warnings.Add(Z42Errors.UnknownManifestKey(tomlPath, section, key, suggestion));
        }
    }

    static void ScanTopLevelKeys(
        TomlTable model, string tomlPath, List<ManifestException> warnings)
    {
        foreach (var key in model.Keys)
        {
            if (KnownTopLevelKeys.Contains(key)) continue;
            string? suggestion = NearestKnown(key, KnownTopLevelKeys);
            warnings.Add(Z42Errors.UnknownManifestKey(tomlPath, "(top-level)", key, suggestion));
        }
    }

    /// <summary>
    /// Known schema renames — when a retired key is spelled exactly, give
    /// the canonical migration target rather than relying on Levenshtein
    /// (which won't catch e.g. `out_dir` → `dist_dir`, distance ≈ 4).
    /// restructure-build-output-dirs (2026-06-06) introduced the first
    /// entry. Stays a curated map, not a blanket alias table — pre-1.0
    /// "no compatibility" still holds (we don't accept the old key, we
    /// just tell the user which new key replaces it).
    /// </summary>
    static readonly Dictionary<string, string> KnownRenames = new(StringComparer.Ordinal)
    {
        ["out_dir"] = "dist_dir",
    };

    /// <summary>
    /// Best-effort suggestion for an unknown manifest key. Checks the
    /// curated rename map first (handles schema migrations across major
    /// names), then falls back to Levenshtein-≤2 fuzzy match against
    /// `known`.
    /// </summary>
    static string? NearestKnown(string candidate, HashSet<string> known)
    {
        if (KnownRenames.TryGetValue(candidate, out var renamed) && known.Contains(renamed))
            return renamed;

        string? best = null;
        int bestDist = 3;     // strict: only suggest when within edit distance 2
        foreach (var k in known)
        {
            int d = LevenshteinDistance(candidate, k);
            if (d < bestDist) { bestDist = d; best = k; }
        }
        return best;
    }

    static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var v0 = new int[b.Length + 1];
        var v1 = new int[b.Length + 1];
        for (int i = 0; i <= b.Length; i++) v0[i] = i;
        for (int i = 0; i < a.Length; i++)
        {
            v1[0] = i + 1;
            for (int j = 0; j < b.Length; j++)
            {
                int cost = a[i] == b[j] ? 0 : 1;
                v1[j + 1] = Math.Min(Math.Min(v1[j] + 1, v0[j + 1] + 1), v0[j] + cost);
            }
            (v0, v1) = (v1, v0);
        }
        return v0[b.Length];
    }

    // ── Source file resolution ─────────────────────────────────────────────────

    /// Resolve source files for the project (shared sources).
    public IReadOnlyList<string> ResolveSourceFiles(string projectDir) =>
        ResolveSourceFiles(projectDir, null);

    /// Resolve source files for a specific exe target.
    /// Uses target's own src if specified, otherwise falls back to shared [sources].
    public IReadOnlyList<string> ResolveSourceFiles(string projectDir, ExeTarget? target)
    {
        IReadOnlyList<string> include;
        IReadOnlyList<string> exclude;

        if (target?.Src is { Count: > 0 } targetSrc)
        {
            include = targetSrc;
            exclude = [];
        }
        else
        {
            include = Sources.Include;
            exclude = Sources.Exclude;
        }

        var matcher = new Matcher();
        foreach (var pattern in include) matcher.AddInclude(pattern);
        foreach (var pattern in exclude) matcher.AddExclude(pattern);

        var result = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(projectDir)));
        var files  = result.Files
            .Select(f => Path.GetFullPath(Path.Combine(projectDir, f.Path)))
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
            throw new ManifestException(
                $"error: no source files found (include: [{string.Join(", ", include)}])");

        return files;
    }

    // ── Effective build paths (single-project mode helper) ────────────────────
    //
    // restructure-build-output-dirs (2026-06-06): single-project consumers
    // (z42c build / z42c clean / z42c run / QueryCommands) reach into
    // BuildSection's three raw nullable dir fields then need to apply the
    // same cascade defaults as workspace mode. Centralise here so callers
    // don't reimplement the `${output_dir}/.cache` / `${output_dir}/dist`
    // fallbacks each time.

    /// <summary>
    /// Resolve effective absolute output paths for a single-project build.
    /// Mirrors what `CentralizedBuildLayout.Resolve(workspace: null, ...)`
    /// would return, but available without a member name / profile context.
    /// </summary>
    public CentralizedBuildLayout.Layout ResolveBuildLayout(
        string projectDir, string profileLabel = "debug") =>
        new CentralizedBuildLayout().Resolve(
            workspace:        null,
            workspaceRoot:    projectDir,
            memberName:       Project.Name,
            memberDir:        projectDir,
            profile:          profileLabel,
            memberLocalBuild: Build,
            expander:         new PathTemplateExpander());

    // ── Profile selection ──────────────────────────────────────────────────────

    public ProfileSection SelectProfile(bool release) => release ? Release : Debug;

    // ── Pack resolution (3-level priority) ────────────────────────────────────

    /// Resolve the effective `pack` flag for a build.
    ///
    /// Priority (highest → lowest):
    ///   1. [profile.debug/release].pack
    ///   2. [[exe]].pack  (target-level override)
    ///   3. [project].pack
    ///   4. Built-in default: debug=false, release=true
    public bool ResolvePack(bool releaseProfile, bool? targetPack = null)
    {
        var profile = SelectProfile(releaseProfile);
        return profile.Pack
            ?? targetPack
            ?? Project.Pack
            ?? releaseProfile;          // built-in default
    }

    // ── Private parsers ────────────────────────────────────────────────────────

    static ProjectSection ParseProject(
        TomlTable model, string tomlPath, bool hasExeTargets,
        List<ManifestException> warnings)
    {
        if (!model.TryGetValue("project", out var raw) || raw is not TomlTable t)
            throw new ManifestException("error: [project] section is required");
        ScanUnknownKeys(t, KnownProjectKeys, "project", tomlPath, warnings);

        // Infer name from filename if not provided
        string inferredName = Path.GetFileName(tomlPath)
            .Replace(".z42.toml", "", StringComparison.OrdinalIgnoreCase);

        string  name    = t.TryGetString("name")        ?? inferredName;
        string  version = t.TryGetString("version")     ?? "0.1.0";
        string? kindStr = t.TryGetString("kind");
        string? entry   = t.TryGetString("entry");
        string? desc    = t.TryGetString("description");
        bool?   pack    = t.TryGetBool("pack");

        // [[exe]] and kind="exe" cannot coexist
        if (hasExeTargets && kindStr == "exe")
            throw new ManifestException(
                "error: cannot use [[exe]] together with [project] kind = \"exe\"; use one or the other");

        // When [[exe]] is used, kind defaults to Multi; otherwise default exe
        ProjectKind kind;
        if (hasExeTargets)
        {
            kind = ProjectKind.Multi;
        }
        else
        {
            string resolvedKindStr = kindStr ?? "exe";
            if (!Enum.TryParse<ProjectKind>(resolvedKindStr, ignoreCase: true, out kind))
                throw new ManifestException(
                    $"error: [project].kind must be 'exe' or 'lib', got '{resolvedKindStr}'");
        }

        // 2026-05-14 auto-detect-main: `[project].entry` is optional even for
        // exe targets. PackageCompiler.BuildTarget auto-detects `Main()`;
        // missing-Main is a compile-time error there.
        return new ProjectSection(name, version, kind, entry, desc, pack);
    }

    static IReadOnlyList<ExeTarget> ParseExeTargets(
        TomlTable model, string tomlPath, List<ManifestException> warnings)
    {
        if (!model.TryGetValue("exe", out var raw)) return [];

        // [[exe]] is parsed by Tomlyn as TomlTableArray, not TomlArray
        var tables = raw switch
        {
            TomlTableArray tba => tba.Cast<TomlTable>().ToList(),
            TomlArray arr      => arr.OfType<TomlTable>().ToList(),
            _                  => null,
        };
        if (tables is null) return [];

        var targets = new List<ExeTarget>();
        for (int i = 0; i < tables.Count; i++)
        {
            var t = tables[i];
            ScanUnknownKeys(t, KnownExeKeys, $"[exe]] #{i}", tomlPath, warnings);
            string? name  = t.TryGetString("name");
            string? entry = t.TryGetString("entry");

            if (string.IsNullOrWhiteSpace(name))
                throw new ManifestException($"error: [[exe]] entry is missing required field 'name'");
            // 2026-05-14 auto-detect-main: `entry` optional; PackageCompiler auto-detects Main.

            var src  = t.TryGetStringArray("src");
            bool? pack = t.TryGetBool("pack");
            targets.Add(new ExeTarget(name, entry, src, pack));
        }
        return targets;
    }

    static SourcesSection ParseSources(
        TomlTable model, string tomlPath, List<ManifestException> warnings)
    {
        if (!model.TryGetValue("sources", out var raw) || raw is not TomlTable t)
            return new SourcesSection(["src/**/*.z42"], []);
        ScanUnknownKeys(t, KnownSourcesKeys, "sources", tomlPath, warnings);

        var include = t.TryGetStringArray("include") ?? ["src/**/*.z42"];
        var exclude = t.TryGetStringArray("exclude") ?? [];
        return new SourcesSection(include, exclude);
    }

    static BuildSection ParseBuild(
        TomlTable model, string tomlPath, List<ManifestException> warnings)
    {
        if (!model.TryGetValue("build", out var raw) || raw is not TomlTable t)
            return new BuildSection();
        ScanUnknownKeys(t, KnownBuildKeys, "build", tomlPath, warnings);

        // restructure-build-output-dirs (2026-06-06): three dir fields are
        // raw (unset = null); effective absolute paths are computed later
        // in CentralizedBuildLayout / ResolvedManifest. Old `out_dir` is
        // gone — KnownBuildKeys does not list it, so any toml that still
        // sets it surfaces as WS008 unknown-key + Levenshtein suggestion.
        string? outputDir  = t.TryGetString("output_dir");
        string? cacheDir   = t.TryGetString("cache_dir");
        string? distDir    = t.TryGetString("dist_dir");
        string  mode       = t.TryGetString("mode")        ?? "interp";
        bool    incremental = t.TryGetBool("incremental")  ?? true;
        return new BuildSection(outputDir, cacheDir, distDir, mode, incremental);
    }

    static ProfileSection ParseProfile(
        TomlTable model, string name, ProfileSection defaults,
        string tomlPath, List<ManifestException> warnings)
    {
        if (!model.TryGetValue("profile", out var profilesRaw) || profilesRaw is not TomlTable profiles)
            return defaults;
        if (!profiles.TryGetValue(name, out var raw) || raw is not TomlTable t)
            return defaults;
        ScanUnknownKeys(t, KnownProfileKeys, $"profile.{name}", tomlPath, warnings);

        string mode     = t.TryGetString("mode")    ?? defaults.Mode;
        int    optimize = (int)(t.TryGetLong("optimize") ?? defaults.Optimize);
        bool   debug    = t.TryGetBool("debug")     ?? defaults.Debug;
        bool   strip    = t.TryGetBool("strip")     ?? defaults.Strip;
        bool?  pack     = t.TryGetBool("pack");
        return new ProfileSection(mode, optimize, debug, strip, pack);
    }

    static DependencySection ParseDependencies(TomlTable model)
    {
        if (!model.TryGetValue("dependencies", out var raw) || raw is not TomlTable t)
            return new DependencySection([], false);

        var entries = new List<DeclaredDep>();
        foreach (var kv in t)
        {
            string name    = kv.Key;
            string version = kv.Value is string s ? s : "*";
            entries.Add(new DeclaredDep(name, version));
        }
        return new DependencySection(entries, true);
    }

    // ── tests/bench schema parsers (add-tests-bench-manifest-config) ──────────

    /// Parse a `[tests]` or `[bench]` shared-config table. Falls back to
    /// `defaultConfig` (which carries convention-discovery globs) when absent.
    static TestBenchConfig ParseTestBenchConfig(
        TomlTable model, string section, TestBenchConfig defaultConfig,
        string tomlPath, List<ManifestException> warnings)
    {
        if (!model.TryGetValue(section, out var raw) || raw is not TomlTable t)
            return defaultConfig;

        ScanUnknownKeys(t, KnownTestsBenchKeys, section, tomlPath, warnings);

        var include = t.TryGetStringArray("include") ?? defaultConfig.Include.ToArray();
        var exclude = t.TryGetStringArray("exclude") ?? defaultConfig.Exclude.ToArray();
        var deps    = ParseInlineDependencies(t, "dependencies");
        return new TestBenchConfig(include, exclude, deps);
    }

    /// Parse `[[test]]` (entryKind="test") or `[[bench]]` (entryKind="bench")
    /// arrays. Emits WS040 (missing name) / WS041 (missing src) / WS042
    /// (duplicate name) / WS043 (src not found).
    static IReadOnlyList<TestBenchEntry> ParseTestBenchEntries(
        TomlTable model, string entryKind, string tomlPath,
        List<ManifestException> warnings)
    {
        if (!model.TryGetValue(entryKind, out var raw)) return [];

        var tables = raw switch
        {
            TomlTableArray tba => tba.Cast<TomlTable>().ToList(),
            TomlArray arr      => arr.OfType<TomlTable>().ToList(),
            _                  => null,
        };
        if (tables is null) return [];

        string projectDir = Path.GetDirectoryName(Path.GetFullPath(tomlPath)) ?? "";
        var entries  = new List<TestBenchEntry>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < tables.Count; i++)
        {
            var t = tables[i];
            ScanUnknownKeys(t, KnownTestBenchEntryKeys, $"[[{entryKind}]] #{i}",
                            tomlPath, warnings);

            string? name = t.TryGetString("name");
            string? src  = t.TryGetString("src");

            if (string.IsNullOrWhiteSpace(name))
                throw Z42Errors.TestEntryMissingName(tomlPath, entryKind, i);

            if (!seenNames.Add(name!))
                throw Z42Errors.TestBenchEntryDuplicateName(tomlPath, entryKind, name!);

            if (string.IsNullOrWhiteSpace(src))
                throw Z42Errors.TestEntryMissingSrc(tomlPath, entryKind, name!);

            // WS043: src path existence — best-effort relative to manifest dir.
            // Tolerate empty projectDir (single-file edge cases via Discover).
            if (projectDir.Length > 0)
            {
                string resolved = Path.Combine(projectDir, src!);
                if (!File.Exists(resolved))
                    throw Z42Errors.TestEntrySrcNotFound(
                        tomlPath, entryKind, name!, src!, resolved);
            }

            var sources = t.TryGetStringArray("sources") ?? [];
            var deps    = ParseInlineDependencies(t, "dependencies");
            entries.Add(new TestBenchEntry(name!, src!, sources, deps));
        }
        return entries;
    }

    /// Parse a `dependencies = { ... }` inline-table inside an entry block,
    /// or a `[X.dependencies]` sub-table. Same value shape as top-level
    /// [dependencies] but materialised as a list (no auto-scan flag).
    static IReadOnlyList<DeclaredDep> ParseInlineDependencies(
        TomlTable parent, string key)
    {
        if (!parent.TryGetValue(key, out var raw) || raw is not TomlTable t)
            return [];

        var entries = new List<DeclaredDep>();
        foreach (var kv in t)
        {
            string name    = kv.Key;
            string version = kv.Value is string s ? s : "*";
            entries.Add(new DeclaredDep(name, version));
        }
        return entries;
    }

    /// Scan top-level `[dependencies]` for known test-only packages and
    /// emit WS012 per leak. Migration warning, never throws.
    static void ScanDepsForTestOnlyLeaks(
        DependencySection deps, string tomlPath,
        List<ManifestException> warnings)
    {
        if (!deps.IsDeclared) return;
        foreach (var dep in deps.Entries)
        {
            if (!KnownTestOnlyDeps.Contains(dep.Name)) continue;
            // Heuristic: if the name contains "bench" → suggest bench section,
            // otherwise default to tests (covers z42.test).
            string section = dep.Name.Contains(".bench", StringComparison.Ordinal)
                ? "bench" : "tests";
            warnings.Add(Z42Errors.TestDepInProductionDeps(tomlPath, dep.Name, section));
        }
    }

    /// Emit WS013 for redundant standard-library deps. stdlib (z42.* /
    /// Std.*) is toolchain-bundled and always available (resolved from each
    /// zpkg's NSPC regardless of declaration, like Rust's `std`), so a
    /// non-stdlib project never needs to declare it. z42.*-named packages
    /// are exempt — the stdlib's own inter-package deps are genuine
    /// build-order edges, and synthetic `<lib>.test.<unit>` harnesses (also
    /// z42.*-prefixed) legitimately list z42.test.
    /// simplify-stdlib-auto-import (2026-06-06). Migration warning, never throws.
    static void ScanForRedundantStdlibDeps(
        string projectName, DependencySection deps,
        TestBenchConfig tests, TestBenchConfig bench,
        string tomlPath, List<ManifestException> warnings)
    {
        if (projectName.StartsWith("z42.", StringComparison.Ordinal)) return;

        void Scan(IEnumerable<string> names, string section)
        {
            foreach (var name in names)
                if (name.StartsWith("z42.", StringComparison.Ordinal))
                    warnings.Add(Z42Errors.RedundantStdlibDep(tomlPath, name, section));
        }
        if (deps.IsDeclared) Scan(deps.Entries.Select(e => e.Name), "dependencies");
        Scan(tests.Dependencies.Select(d => d.Name), "tests.dependencies");
        Scan(bench.Dependencies.Select(d => d.Name), "bench.dependencies");
    }
}

// ── Section records ────────────────────────────────────────────────────────────

public enum ProjectKind { Exe, Lib, Multi }

public sealed record ExeTarget(
    string                 Name,
    string?                Entry,  // 2026-05-14 auto-detect-main: optional
    IReadOnlyList<string>? Src,    // null = inherit [sources]
    bool?                  Pack    // null = inherit [project].pack
);

public sealed record ProjectSection(
    string      Name,
    string      Version,
    ProjectKind Kind,
    string?     Entry,
    string?     Description,
    bool?       Pack        // null = use profile/built-in default
)
{
    public ProjectSection() : this("", "0.1.0", ProjectKind.Exe, null, null, null) { }
}

public sealed record SourcesSection(
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude
)
{
    public SourcesSection() : this(["src/**/*.z42"], []) { }
}

/// <summary>
/// `[build]` section of `.z42.toml`. All three directory fields are raw
/// (unset = `null`); effective absolute paths are computed in
/// `CentralizedBuildLayout.Resolve` / `ResolvedManifest`.
///
/// restructure-build-output-dirs (2026-06-06): legacy `out_dir` field
/// retired; replaced by `output_dir` (root) / `cache_dir` (intermediate)
/// / `dist_dir` (final artifacts). Old `out_dir` triggers WS008 via
/// `KnownBuildKeys` with a Levenshtein suggestion toward `dist_dir`.
/// </summary>
public sealed record BuildSection(
    string? OutputDir   = null,
    string? CacheDir    = null,
    string? DistDir     = null,
    string  Mode        = "interp",
    bool    Incremental = true
);

/// A single declared dependency entry: `"pkg-name" = "version"`.
public sealed record DeclaredDep(string Name, string Version);

/// Parsed `[dependencies]` section.
/// When `IsDeclared` is true, only the listed packages are visible to the compiler
/// (besides implicit stdlib). When false (no `[dependencies]`), auto-scan mode.
public sealed record DependencySection(
    IReadOnlyList<DeclaredDep> Entries,
    bool IsDeclared)
{
    public DependencySection() : this([], false) { }
}

public sealed record ProfileSection(
    string Mode,
    int    Optimize,
    bool   Debug,
    bool   Strip,
    bool?  Pack     // null = not set at profile level
)
{
    public static ProfileSection DefaultDebug   => new("interp", 0, true,  false, null);
    public static ProfileSection DefaultRelease => new("jit",    3, false, true,  null);
}

/// add-tests-bench-manifest-config (2026-06-06): `[tests]` / `[bench]`
/// shared-config record. Both sections share an identical shape; the
/// only difference is the convention root (`tests/` vs `bench/`), captured
/// in `DefaultTests` / `DefaultBench`.
public sealed record TestBenchConfig(
    IReadOnlyList<string>      Include,
    IReadOnlyList<string>      Exclude,
    IReadOnlyList<DeclaredDep> Dependencies)
{
    public TestBenchConfig() : this([], [], []) { }
    public static TestBenchConfig DefaultTests =>
        new(["tests/*.z42", "tests/*/source.z42"], [], []);
    public static TestBenchConfig DefaultBench =>
        new(["bench/*.z42", "bench/*/source.z42"], [], []);
}

/// add-tests-bench-manifest-config (2026-06-06): a single `[[test]]` or
/// `[[bench]]` entry. `Name` becomes the synthetic mini-manifest's
/// package name (`<lib>.test.<name>` or `<lib>.bench.<name>`). `Src`
/// is the entry file (relative to package root). `Sources` is an
/// optional explicit glob set; when empty, the dir-mode convention
/// auto-includes `<dirname>/**/*.z42`.
public sealed record TestBenchEntry(
    string                     Name,
    string                     Src,
    IReadOnlyList<string>      Sources,
    IReadOnlyList<DeclaredDep> Dependencies);

/// add-manifest-hygiene-warnings (2026-06-04): manifest plus any
/// hygiene warnings collected during parsing (WS008 unknown-key,
/// WS009 redundant-entry — the latter emitted later from
/// PackageCompiler.BuildTarget where auto-detect actually runs).
public sealed record ProjectManifestLoadResult(
    ProjectManifest                  Manifest,
    IReadOnlyList<ManifestException> Warnings);

// ── TOML helpers ──────────────────────────────────────────────────────────────

internal static class TomlTableExtensions
{
    public static string? TryGetString(this TomlTable t, string key) =>
        t.TryGetValue(key, out var v) && v is string s ? s : null;

    public static bool? TryGetBool(this TomlTable t, string key) =>
        t.TryGetValue(key, out var v) && v is bool b ? b : null;

    public static long? TryGetLong(this TomlTable t, string key) =>
        t.TryGetValue(key, out var v) && v is long l ? l : null;

    public static string[]? TryGetStringArray(this TomlTable t, string key)
    {
        if (!t.TryGetValue(key, out var v) || v is not TomlArray arr) return null;
        return arr.OfType<string>().ToArray();
    }
}

// ── Exception ─────────────────────────────────────────────────────────────────

public sealed class ManifestException(string message) : Exception(message);
