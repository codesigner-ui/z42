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
        ScanTopLevelKeys(model, tomlPath, warnings);

        var manifest = new ProjectManifest
        {
            Project      = project,
            Sources      = sources,
            Build        = build,
            Debug        = debug,
            Release      = release,
            ExeTargets   = exeTargets,
            Dependencies = deps,
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
        "out_dir", "mode", "incremental",
    };
    static readonly HashSet<string> KnownProfileKeys = new(StringComparer.Ordinal)
    {
        "mode", "optimize", "debug", "strip", "pack",
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

    /// <summary>Levenshtein-1-or-2 match against a known-key set.</summary>
    static string? NearestKnown(string candidate, HashSet<string> known)
    {
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
            return new BuildSection("dist", "interp", true);
        ScanUnknownKeys(t, KnownBuildKeys, "build", tomlPath, warnings);

        string outDir      = t.TryGetString("out_dir") ?? "dist";
        string mode        = t.TryGetString("mode")    ?? "interp";
        bool   incremental = t.TryGetBool("incremental") ?? true;
        return new BuildSection(outDir, mode, incremental);
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

public sealed record BuildSection(
    string OutDir,
    string Mode,
    bool   Incremental
)
{
    public BuildSection() : this("dist", "interp", true) { }
}

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
