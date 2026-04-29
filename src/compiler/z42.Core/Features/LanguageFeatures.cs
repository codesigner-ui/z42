namespace Z42.Core.Features;

/// <summary>
/// All known language feature gates.
/// Names match snake_case [feat:NAME] tags in grammar.peg and feature.toml overrides.
/// </summary>
public enum LanguageFeature
{
    InterpolatedStr,
    ControlFlow,
    Oop,
    Generics,
    Arrays,
    Exceptions,
    PatternMatch,
    Lambda,
    Async,
    Nullable,
    NullCoalesce,
    Ternary,
    Cast,
    Tuples,
    Delegates,
    Reflection,
    ListPatterns,
    UsingStmt,
    Threading,
    Bitwise,
    /// Spec C5 — `pinned p = s { ... }` block for native interop borrows.
    NativeInterop,
}

/// <summary>
/// Per-feature metadata: snake_case name (for TOML / grammar tags), phase, and dependencies.
/// </summary>
public sealed record FeatureMetadata(
    string         Name,
    LanguagePhase  Since,
    LanguageFeature[]  DependsOn = default!)
{
    public FeatureMetadata(string name, LanguagePhase since)
        : this(name, since, []) { }
}

/// <summary>
/// Controls which language features are enabled during compilation.
///
/// Internally stored as a <see cref="LanguageFeature"/> → bool dictionary.
///   • Unknown features default to <c>false</c> (safe: typos are caught, not silently enabled).
///   • <see cref="KnownFeatureNames"/> enumerates all declared gates for grammar-sync validation.
///   • String-based <see cref="IsEnabled(string)"/> overload parses to enum for TOML / parser-table compat.
/// </summary>
public sealed class LanguageFeatures
{
    private readonly IReadOnlyDictionary<LanguageFeature, bool> _flags;

    private LanguageFeatures(Dictionary<LanguageFeature, bool> flags, LanguagePhase phase = LanguagePhase.Phase1)
    {
        _flags = flags;
        Phase  = phase;
    }

    // ── Feature metadata (single source of truth) ─────────────────────────────

    /// Per-feature metadata: snake_case name, phase, dependencies.
    public static IReadOnlyDictionary<LanguageFeature, FeatureMetadata> Metadata { get; } =
        new Dictionary<LanguageFeature, FeatureMetadata>
        {
            [LanguageFeature.InterpolatedStr] = new("interpolated_str", LanguagePhase.Phase1),
            [LanguageFeature.ControlFlow]     = new("control_flow",     LanguagePhase.Phase1),
            [LanguageFeature.Oop]             = new("oop",              LanguagePhase.Phase1),
            [LanguageFeature.Generics]        = new("generics",         LanguagePhase.Phase1),
            [LanguageFeature.Arrays]          = new("arrays",           LanguagePhase.Phase1),
            [LanguageFeature.Exceptions]      = new("exceptions",       LanguagePhase.Phase1),
            [LanguageFeature.PatternMatch]    = new("pattern_match",    LanguagePhase.Phase1),
            [LanguageFeature.Lambda]          = new("lambda",           LanguagePhase.Phase1),
            [LanguageFeature.Async]           = new("async",            LanguagePhase.Phase1),
            [LanguageFeature.Nullable]        = new("nullable",         LanguagePhase.Phase1, [LanguageFeature.Oop]),
            [LanguageFeature.NullCoalesce]    = new("null_coalesce",    LanguagePhase.Phase1, [LanguageFeature.Nullable]),
            [LanguageFeature.Ternary]         = new("ternary",          LanguagePhase.Phase1),
            [LanguageFeature.Cast]            = new("cast",             LanguagePhase.Phase1),
            [LanguageFeature.Tuples]          = new("tuples",           LanguagePhase.Phase1),
            [LanguageFeature.Delegates]       = new("delegates",        LanguagePhase.Phase1),
            [LanguageFeature.Reflection]      = new("reflection",       LanguagePhase.Phase1),
            [LanguageFeature.ListPatterns]    = new("list_patterns",    LanguagePhase.Phase1, [LanguageFeature.PatternMatch]),
            [LanguageFeature.UsingStmt]       = new("using_stmt",       LanguagePhase.Phase1),
            [LanguageFeature.Threading]       = new("threading",        LanguagePhase.Phase1, [LanguageFeature.Async]),
            [LanguageFeature.Bitwise]         = new("bitwise",          LanguagePhase.Phase1),
            [LanguageFeature.NativeInterop]   = new("native_interop",   LanguagePhase.Phase1),
        };

    /// Reverse lookup: snake_case name → enum value.
    private static readonly IReadOnlyDictionary<string, LanguageFeature> s_nameToEnum =
        Metadata.ToDictionary(kv => kv.Value.Name, kv => kv.Key);

    /// Try to parse a snake_case feature name to its enum value.
    public static bool TryParse(string name, out LanguageFeature feature) =>
        s_nameToEnum.TryGetValue(name, out feature);

    // ── Pre-built profiles ─────────────────────────────────────────────────

    /// Minimal runnable subset — only what Hello World needs.
    public static readonly LanguageFeatures Minimal = new(new()
    {
        [LanguageFeature.InterpolatedStr] = true,
        [LanguageFeature.ControlFlow]     = false,
        [LanguageFeature.Oop]             = false,
        [LanguageFeature.Generics]        = false,
        [LanguageFeature.Arrays]          = false,
        [LanguageFeature.Exceptions]      = false,
        [LanguageFeature.PatternMatch]    = false,
        [LanguageFeature.Lambda]          = false,
        [LanguageFeature.Async]           = false,
        [LanguageFeature.Nullable]        = false,
        [LanguageFeature.NullCoalesce]    = false,
        [LanguageFeature.Ternary]         = true,
        [LanguageFeature.Cast]            = true,
        [LanguageFeature.Tuples]          = false,
        [LanguageFeature.Delegates]       = false,
        [LanguageFeature.Reflection]      = false,
        [LanguageFeature.ListPatterns]    = false,
        [LanguageFeature.UsingStmt]       = false,
        [LanguageFeature.Threading]       = false,
        [LanguageFeature.Bitwise]         = true,
    });

    /// Phase 1 full feature set — complete C# 12 subset.
    public static readonly LanguageFeatures Phase1 = new(new()
    {
        [LanguageFeature.InterpolatedStr] = true,
        [LanguageFeature.ControlFlow]     = true,
        [LanguageFeature.Oop]             = true,
        [LanguageFeature.Generics]        = true,
        [LanguageFeature.Arrays]          = true,
        [LanguageFeature.Exceptions]      = true,
        [LanguageFeature.PatternMatch]    = true,
        [LanguageFeature.Lambda]          = true,
        [LanguageFeature.Async]           = true,
        [LanguageFeature.Nullable]        = true,
        [LanguageFeature.NullCoalesce]    = true,
        [LanguageFeature.Ternary]         = true,
        [LanguageFeature.Cast]            = true,
        [LanguageFeature.Tuples]          = true,
        [LanguageFeature.Delegates]       = true,
        [LanguageFeature.Reflection]      = true,
        [LanguageFeature.ListPatterns]    = true,
        [LanguageFeature.UsingStmt]       = true,
        [LanguageFeature.Threading]       = true,
        [LanguageFeature.Bitwise]         = true,
    });

    // ── Phase tracking ─────────────────────────────────────────────────────────

    public LanguagePhase Phase { get; }

    // ── Known feature names (for grammar-sync validation) ─────────────────────

    /// All feature names declared in the metadata table.
    /// Used by GrammarSyncTests to verify that grammar.peg [feat:NAME] tags are recognised.
    public static IReadOnlySet<string> KnownFeatureNames { get; } =
        new HashSet<string>(Metadata.Values.Select(m => m.Name));

    // ── Query ──────────────────────────────────────────────────────────────────

    /// Returns true if the named feature is enabled.
    /// Unknown features default to false (safe: typos are caught, not silently enabled).
    public bool IsEnabled(LanguageFeature f) =>
        _flags.TryGetValue(f, out var v) && v;

    /// String overload for TOML overrides and parser-table feature gates.
    /// Unknown feature names default to false.
    public bool IsEnabled(string key) =>
        TryParse(key, out var f) && IsEnabled(f);

    // ── Configuration ──────────────────────────────────────────────────────────

    /// Returns a new instance with the given overrides merged in.
    /// Keys are snake_case feature names (e.g. "control_flow").
    public LanguageFeatures WithOverrides(IReadOnlyDictionary<string, bool> overrides)
    {
        var merged = new Dictionary<LanguageFeature, bool>(_flags);
        foreach (var (k, v) in overrides)
        {
            if (TryParse(k, out var f))
                merged[f] = v;
            // Unknown keys are silently ignored (TOML forward-compat).
        }
        return new LanguageFeatures(merged, Phase);
    }
}

public enum LanguagePhase
{
    /// C# 9–12 syntax subset (current).
    Phase1,
    /// Rust-influenced: ownership, Result<T,E>, match exhaustiveness.
    Phase2,
}
