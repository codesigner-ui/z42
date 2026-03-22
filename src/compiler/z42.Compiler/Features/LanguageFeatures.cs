namespace Z42.Compiler.Features;

/// <summary>
/// Controls which language features are enabled during compilation.
///
/// Internally stored as a string→bool dictionary so that:
///   • Adding a new feature gate requires exactly TWO lines (one in Minimal, one in Phase1).
///   • <see cref="WithOverrides"/> and <see cref="IsEnabled"/> need no maintenance.
///   • <see cref="KnownFeatureNames"/> enumerates all declared gates for validation.
///
/// Feature names are snake_case strings matching [feat:NAME] tags in grammar.peg
/// and the Feature field of ParseRule / StmtRule.
/// </summary>
public sealed class LanguageFeatures
{
    private readonly IReadOnlyDictionary<string, bool> _flags;

    private LanguageFeatures(Dictionary<string, bool> flags, LanguagePhase phase = LanguagePhase.Phase1)
    {
        _flags = flags;
        Phase  = phase;
    }

    // ── Pre-built profiles ─────────────────────────────────────────────────

    /// Minimal runnable subset — only what Hello World needs.
    public static readonly LanguageFeatures Minimal = new(new()
    {
        ["interpolated_str"] = true,
        ["control_flow"]     = false,
        ["oop"]              = false,
        ["generics"]         = false,
        ["arrays"]           = false,
        ["exceptions"]       = false,
        ["pattern_match"]    = false,
        ["lambda"]           = false,
        ["async"]            = false,
        ["nullable"]         = false,
        ["null_coalesce"]    = false,
        ["ternary"]          = true,
        ["cast"]             = true,
        ["tuples"]           = false,
        ["delegates"]        = false,
        ["reflection"]       = false,
        ["list_patterns"]    = false,
        ["using_stmt"]       = false,
        ["threading"]        = false,
        ["bitwise"]          = true,
    });

    /// Phase 1 full feature set — complete C# 12 subset.
    public static readonly LanguageFeatures Phase1 = new(new()
    {
        ["interpolated_str"] = true,
        ["control_flow"]     = true,
        ["oop"]              = true,
        ["generics"]         = true,
        ["arrays"]           = true,
        ["exceptions"]       = true,
        ["pattern_match"]    = true,
        ["lambda"]           = true,
        ["async"]            = true,
        ["nullable"]         = true,
        ["null_coalesce"]    = true,
        ["ternary"]          = true,
        ["cast"]             = true,
        ["tuples"]           = true,
        ["delegates"]        = true,
        ["reflection"]       = true,
        ["list_patterns"]    = true,
        ["using_stmt"]       = true,
        ["threading"]        = true,
        ["bitwise"]          = true,
    });

    // ── Phase tracking ─────────────────────────────────────────────────────────

    public LanguagePhase Phase { get; }

    // ── Known feature names (for validation) ──────────────────────────────────

    /// All feature names declared in the Phase1 preset.
    /// Used by GrammarSyncTests to verify that grammar.peg [feat:NAME] tags are recognised.
    public static IReadOnlySet<string> KnownFeatureNames { get; } =
        new HashSet<string>(Phase1._flags.Keys);

    // ── Query ──────────────────────────────────────────────────────────────────

    /// Returns true if the named feature (snake_case) is enabled.
    /// Unknown feature names default to true (forwards-compatible).
    public bool IsEnabled(string key) =>
        _flags.TryGetValue(key, out var v) ? v : true;

    // ── Configuration ──────────────────────────────────────────────────────────

    /// Returns a new instance with the given overrides merged in.
    /// Keys are lowercase feature names (e.g. "control_flow").
    public LanguageFeatures WithOverrides(IReadOnlyDictionary<string, bool> overrides)
    {
        var merged = new Dictionary<string, bool>(_flags);
        foreach (var (k, v) in overrides)
            merged[k] = v;
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
