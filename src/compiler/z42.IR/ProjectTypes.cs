using System.Text.Json.Serialization;

namespace Z42.IR;

/// Canonical file name for all z42 project / workspace manifests.
public static class Z42TomlFileName
{
    public const string Value = "z42.toml";
}

// ── Shared enums ──────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter<ProjectKind>))]
public enum ProjectKind { Exe, Lib }

[JsonConverter(typeof(JsonStringEnumConverter<EmitKind>))]
public enum EmitKind { Ir, Zbc, Zasm }

[JsonConverter(typeof(JsonStringEnumConverter<ExecModeConfig>))]
public enum ExecModeConfig { Interp, Jit, Aot }

// ── .z42proj ──────────────────────────────────────────────────────────────────

/// `[project]` table.
public sealed class ProjectMeta
{
    public string       Name        { get; set; } = "";
    public string       Version     { get; set; } = "0.1.0";
    public ProjectKind  Kind        { get; set; } = ProjectKind.Exe;
    /// Required when Kind = Exe. Fully-qualified entry, e.g. "Hello.Main".
    public string?      Entry       { get; set; }
    /// Root namespace; defaults to Name.
    public string?      Namespace   { get; set; }
    public string?      Description { get; set; }
    public List<string> Authors     { get; set; } = [];
    public string?      License     { get; set; }
    /// Default pack mode for all targets. Overridable per [[exe]] and per [profile.*].
    public bool?        Pack        { get; set; }

    public string EffectiveNamespace => Namespace ?? Name;
}

/// `[sources]` table.
public sealed class SourcesConfig
{
    /// Glob patterns relative to the .z42proj file. Default: src/**/*.z42
    public List<string> Include { get; set; } = ["src/**/*.z42"];
    public List<string> Exclude { get; set; } = [];
}

/// `[build]` table.
public sealed class BuildConfig
{
    public ExecModeConfig Mode        { get; set; } = ExecModeConfig.Interp;
    public bool           Incremental { get; set; } = true;
    public string         OutDir      { get; set; } = "dist";
}

/// `[[dependency]]` array entry.
public sealed class ProjectDependency
{
    public string  Name    { get; set; } = "";
    /// SemVer constraint, e.g. ">=0.1". Null when using path-only reference.
    public string? Version { get; set; }
    /// Local path to a .z42proj directory or file.
    public string? Path    { get; set; }
}

/// `[profile.<name>]` table — overrides applied on top of [build].
public sealed class ProfileConfig
{
    public ExecModeConfig? Mode     { get; set; }
    /// Optimisation level 0–3.
    public int?            Optimize { get; set; }
    public bool?           Debug    { get; set; }
    public bool?           Strip    { get; set; }
    /// Pack output into a single .zpkg (packed mode). Null = use lower-priority default.
    public bool?           Pack     { get; set; }
}

/// Fully-resolved profile after merging [build] + [profile.<name>].
public sealed record ResolvedProfile(
    ExecModeConfig Mode,
    int            Optimize,
    bool           Debug,
    bool           Strip
);

/// Root model for a `z42.toml` file containing a <c>[project]</c> table.
/// Use Tomlyn: <c>Toml.ToModel&lt;Z42Proj&gt;(text)</c>
/// File name constant: <see cref="Z42TomlFileName"/>
public sealed class Z42Proj
{
    public ProjectMeta                       Project      { get; set; } = new();
    public SourcesConfig                     Sources      { get; set; } = new();
    public BuildConfig                       Build        { get; set; } = new();
    public List<ProjectDependency>           Dependencies { get; set; } = [];
    public Dictionary<string, ProfileConfig> Profile      { get; set; } = new();

    /// Effective namespace: explicit value or falls back to project name.
    public string Namespace => Project.EffectiveNamespace;

    /// Resolve the active profile, merging [build] defaults with [profile.<name>].
    public ResolvedProfile ResolveProfile(string profileName = "debug")
    {
        Profile.TryGetValue(profileName, out var p);
        return new ResolvedProfile(
            Mode:     p?.Mode     ?? Build.Mode,
            Optimize: p?.Optimize ?? (profileName == "release" ? 3 : 0),
            Debug:    p?.Debug    ?? (profileName == "debug"),
            Strip:    p?.Strip    ?? false
        );
    }
}

// ── workspace ─────────────────────────────────────────────────────────────────

/// Workspace-level shared dependency entry.
public sealed class WorkspaceDep
{
    public string? Path    { get; set; }
    public string? Version { get; set; }
}

/// `[workspace]` table.
public sealed class WorkspaceMeta
{
    /// Relative paths to directories that each contain a <c>z42.toml</c>.
    public List<string>                     Members      { get; set; } = [];
    public Dictionary<string, WorkspaceDep> Dependencies { get; set; } = new();
}

/// Root model for a `z42.toml` file containing a <c>[workspace]</c> table.
/// Use Tomlyn: <c>Toml.ToModel&lt;Z42Sln&gt;(text)</c>
public sealed class Z42Sln
{
    public WorkspaceMeta                     Workspace { get; set; } = new();
    public Dictionary<string, ProfileConfig> Profile   { get; set; } = new();
}
