using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

namespace Z42.Build;

// ── TOML model ────────────────────────────────────────────────────────────────

public sealed class ProjectManifest
{
    public ProjectSection       Project     { get; init; } = new();
    public SourcesSection       Sources     { get; init; } = new();
    public BuildSection         Build       { get; init; } = new();
    public ProfileSection       Debug       { get; init; } = ProfileSection.DefaultDebug;
    public ProfileSection       Release     { get; init; } = ProfileSection.DefaultRelease;
    public IReadOnlyList<ExeTarget> ExeTargets { get; init; } = [];

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

    public static ProjectManifest Load(string tomlPath)
    {
        string toml = File.ReadAllText(tomlPath);
        var model   = TomlSerializer.Deserialize<TomlTable>(toml)
                      ?? throw new ManifestException($"error: failed to parse {tomlPath}");

        var exeTargets = ParseExeTargets(model);
        var project    = ParseProject(model, tomlPath, exeTargets.Count > 0);
        var sources    = ParseSources(model);
        var build      = ParseBuild(model, project.Kind);
        var debug      = ParseProfile(model, "debug",   ProfileSection.DefaultDebug);
        var release    = ParseProfile(model, "release", ProfileSection.DefaultRelease);

        return new ProjectManifest
        {
            Project    = project,
            Sources    = sources,
            Build      = build,
            Debug      = debug,
            Release    = release,
            ExeTargets = exeTargets,
        };
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

    // ── Private parsers ────────────────────────────────────────────────────────

    static ProjectSection ParseProject(TomlTable model, string tomlPath, bool hasExeTargets)
    {
        if (!model.TryGetValue("project", out var raw) || raw is not TomlTable t)
            throw new ManifestException("error: [project] section is required");

        // Infer name from filename if not provided
        string inferredName = Path.GetFileName(tomlPath)
            .Replace(".z42.toml", "", StringComparison.OrdinalIgnoreCase);

        string  name    = t.TryGetString("name")        ?? inferredName;
        string  version = t.TryGetString("version")     ?? "0.1.0";
        string? kindStr = t.TryGetString("kind");
        string? entry   = t.TryGetString("entry");
        string? ns      = t.TryGetString("namespace");
        string? desc    = t.TryGetString("description");

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

        if (kind == ProjectKind.Exe && string.IsNullOrWhiteSpace(entry))
            throw new ManifestException(
                "error: [project].entry is required when kind = \"exe\"");

        string resolvedNs = ns ?? KebabToPascal(name);
        return new ProjectSection(name, version, kind, entry, resolvedNs, desc);
    }

    static IReadOnlyList<ExeTarget> ParseExeTargets(TomlTable model)
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
            string? name  = t.TryGetString("name");
            string? entry = t.TryGetString("entry");

            if (string.IsNullOrWhiteSpace(name))
                throw new ManifestException($"error: [[exe]] entry is missing required field 'name'");
            if (string.IsNullOrWhiteSpace(entry))
                throw new ManifestException($"error: [[exe]] '{name}' is missing required field 'entry'");

            var src = t.TryGetStringArray("src");
            targets.Add(new ExeTarget(name, entry, src));
        }
        return targets;
    }

    static SourcesSection ParseSources(TomlTable model)
    {
        if (!model.TryGetValue("sources", out var raw) || raw is not TomlTable t)
            return new SourcesSection(["src/**/*.z42"], []);

        var include = t.TryGetStringArray("include") ?? ["src/**/*.z42"];
        var exclude = t.TryGetStringArray("exclude") ?? [];
        return new SourcesSection(include, exclude);
    }

    static BuildSection ParseBuild(TomlTable model, ProjectKind kind)
    {
        string defaultEmit = kind == ProjectKind.Lib ? "zlib" : "zbc";

        if (!model.TryGetValue("build", out var raw) || raw is not TomlTable t)
            return new BuildSection("dist", defaultEmit, "interp", true);

        string outDir      = t.TryGetString("out_dir")  ?? "dist";
        string emit        = t.TryGetString("emit")     ?? defaultEmit;
        string mode        = t.TryGetString("mode")     ?? "interp";
        bool   incremental = t.TryGetBool("incremental") ?? true;
        return new BuildSection(outDir, emit, mode, incremental);
    }

    static ProfileSection ParseProfile(TomlTable model, string name, ProfileSection defaults)
    {
        if (!model.TryGetValue("profile", out var profilesRaw) || profilesRaw is not TomlTable profiles)
            return defaults;
        if (!profiles.TryGetValue(name, out var raw) || raw is not TomlTable t)
            return defaults;

        string mode     = t.TryGetString("mode")    ?? defaults.Mode;
        int    optimize = (int)(t.TryGetLong("optimize") ?? defaults.Optimize);
        bool   debug    = t.TryGetBool("debug")     ?? defaults.Debug;
        bool   strip    = t.TryGetBool("strip")     ?? defaults.Strip;
        return new ProfileSection(mode, optimize, debug, strip);
    }

    static string KebabToPascal(string kebab) =>
        string.Concat(kebab.Split('-').Select(w =>
            w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}

// ── Section records ────────────────────────────────────────────────────────────

public enum ProjectKind { Exe, Lib, Multi }

public sealed record ExeTarget(
    string                 Name,
    string                 Entry,
    IReadOnlyList<string>? Src    // null = inherit [sources]
);

public sealed record ProjectSection(
    string      Name,
    string      Version,
    ProjectKind Kind,
    string?     Entry,
    string      Namespace,
    string?     Description
)
{
    public ProjectSection() : this("", "0.1.0", ProjectKind.Exe, null, "", null) { }
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
    string Emit,
    string Mode,
    bool   Incremental
)
{
    public BuildSection() : this("dist", "zbc", "interp", true) { }
}

public sealed record ProfileSection(
    string Mode,
    int    Optimize,
    bool   Debug,
    bool   Strip
)
{
    public static ProfileSection DefaultDebug   => new("interp", 0, true,  false);
    public static ProfileSection DefaultRelease => new("jit",    3, false, true);
}

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
