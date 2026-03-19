namespace Z42.Compiler.Features;

/// <summary>
/// Controls which language features are enabled during compilation.
/// Gates correspond to [feat:NAME] tags in docs/design/grammar.peg.
/// </summary>
public sealed class LanguageFeatures
{
    // ── Pre-built profiles ─────────────────────────────────────────────────

    /// Minimal runnable subset — only what Hello World needs.
    public static readonly LanguageFeatures Minimal = new()
    {
        InterpolatedStr  = true,
        ControlFlow      = false,
        Oop              = false,
        Generics         = false,
        Arrays           = false,
        Exceptions       = false,
        PatternMatch     = false,
        Lambda           = false,
        Async            = false,
        Nullable         = false,
        NullCoalesce     = false,
        Ternary          = true,
        Cast             = true,
        Tuples           = false,
        Delegates        = false,
        Reflection       = false,
        ListPatterns     = false,
        UsingStmt        = false,
        Threading        = false,
    };

    /// Phase 1 full feature set — complete C# 12 subset.
    public static readonly LanguageFeatures Phase1 = new()
    {
        InterpolatedStr  = true,
        ControlFlow      = true,
        Oop              = true,
        Generics         = true,
        Arrays           = true,
        Exceptions       = true,
        PatternMatch     = true,
        Lambda           = true,
        Async            = true,
        Nullable         = true,
        NullCoalesce     = true,
        Ternary          = true,
        Cast             = true,
        Tuples           = true,
        Delegates        = true,
        Reflection       = true,
        ListPatterns     = true,
        UsingStmt        = true,
        Threading        = true,
    };

    // ── Per-feature flags ──────────────────────────────────────────────────

    /// `$"Hello, {name}!"` interpolated string literals.
    public bool InterpolatedStr  { get; init; } = true;

    /// `if`, `while`, `for`, `foreach`, `do`, `break`, `continue`.
    public bool ControlFlow      { get; init; } = true;

    /// `class`, `struct`, `record`, `interface`, `enum`, constructors, properties.
    public bool Oop              { get; init; } = true;

    /// `List<T>`, `Dictionary<K,V>`, type parameters.
    public bool Generics         { get; init; } = true;

    /// Array types `T[]` and array creation `new T[n]`.
    public bool Arrays           { get; init; } = true;

    /// `try`/`catch`/`finally`/`throw`.
    public bool Exceptions       { get; init; } = true;

    /// `switch` expressions and `is` pattern matching.
    public bool PatternMatch     { get; init; } = true;

    /// Lambda expressions `x => expr` and `(x, y) => block`.
    public bool Lambda           { get; init; } = true;

    /// `async`/`await`, `Task<T>`, `ValueTask<T>`.
    public bool Async            { get; init; } = true;

    /// Nullable reference types `T?` and `!` null-forgiving.
    public bool Nullable         { get; init; } = true;

    /// `??` null-coalescing and `?.` null-conditional operators.
    public bool NullCoalesce     { get; init; } = true;

    /// Ternary `cond ? a : b`.
    public bool Ternary          { get; init; } = true;

    /// Explicit cast `(T)expr`.
    public bool Cast             { get; init; } = true;

    /// Tuple types `(int x, string y)` and tuple literals.
    public bool Tuples           { get; init; } = false;

    /// `Func<T>` / `Action` delegate types.
    public bool Delegates        { get; init; } = false;

    /// `typeof(T)`, `nameof(x)`.
    public bool Reflection       { get; init; } = true;

    /// C# 11 list patterns `[a, b, .. rest]`.
    public bool ListPatterns     { get; init; } = false;

    /// `using` statement / declaration.
    public bool UsingStmt        { get; init; } = true;

    /// `lock` statement.
    public bool Threading        { get; init; } = false;

    // ── Phase tracking ─────────────────────────────────────────────────────

    /// Language phase — Phase 2 enables ownership/Result/match.
    public LanguagePhase Phase   { get; init; } = LanguagePhase.Phase1;

    // ── Configuration ──────────────────────────────────────────────────────

    /// Return a copy of this profile with the given feature flags overridden.
    /// Keys are lowercase feature names (e.g. "control_flow").
    public LanguageFeatures WithOverrides(IReadOnlyDictionary<string, bool> overrides)
    {
        bool Get(string key) => overrides.TryGetValue(key, out var v) ? v : GetByName(key);

        return new LanguageFeatures
        {
            InterpolatedStr = Get("interpolated_str"),
            ControlFlow     = Get("control_flow"),
            Oop             = Get("oop"),
            Generics        = Get("generics"),
            Arrays          = Get("arrays"),
            Exceptions      = Get("exceptions"),
            PatternMatch    = Get("pattern_match"),
            Lambda          = Get("lambda"),
            Async           = Get("async"),
            Nullable        = Get("nullable"),
            NullCoalesce    = Get("null_coalesce"),
            Ternary         = Get("ternary"),
            Cast            = Get("cast"),
            Tuples          = Get("tuples"),
            Delegates       = Get("delegates"),
            Reflection      = Get("reflection"),
            ListPatterns    = Get("list_patterns"),
            UsingStmt       = Get("using_stmt"),
            Threading       = Get("threading"),
            Phase           = Phase,
        };
    }

    /// Returns true if the named feature (snake_case) is enabled.
    public bool IsEnabled(string key) => GetByName(key);

    private bool GetByName(string key) => key switch
    {
        "interpolated_str" => InterpolatedStr,
        "control_flow"     => ControlFlow,
        "oop"              => Oop,
        "generics"         => Generics,
        "arrays"           => Arrays,
        "exceptions"       => Exceptions,
        "pattern_match"    => PatternMatch,
        "lambda"           => Lambda,
        "async"            => Async,
        "nullable"         => Nullable,
        "null_coalesce"    => NullCoalesce,
        "ternary"          => Ternary,
        "cast"             => Cast,
        "tuples"           => Tuples,
        "delegates"        => Delegates,
        "reflection"       => Reflection,
        "list_patterns"    => ListPatterns,
        "using_stmt"       => UsingStmt,
        "threading"        => Threading,
        _                  => true,   // unknown keys default to enabled
    };
}

public enum LanguagePhase
{
    /// C# 9–12 syntax subset (current).
    Phase1,
    /// Rust-influenced: ownership, Result<T,E>, match exhaustiveness.
    Phase2,
}
