namespace Z42.Semantics.TypeCheck;

/// <summary>
/// Lexically-scoped symbol table.
/// Tracks:
///   - Local variables (pushed via <see cref="Define"/>)
///   - Functions (global, shared across all scopes)
/// </summary>
internal sealed class TypeEnv
{
    // ── State ────────────────────────────────────────────────────────────────

    private readonly TypeEnv?                                       _parent;
    private readonly IReadOnlyDictionary<string, Z42FuncType>      _funcs;    // global function table
    private readonly IReadOnlyDictionary<string, Z42ClassType>     _classes;  // global class table
    private readonly IReadOnlySet<string>                           _importedClassNames;
    private readonly Dictionary<string, Z42Type>                    _vars = new();

    /// The class currently being checked, or null for top-level functions.
    /// Immutable per scope — when entering a class, create a new env via <see cref="WithClass"/>.
    internal string? CurrentClass { get; }

    // Root env: owns both function and class tables.
    internal TypeEnv(
        IReadOnlyDictionary<string, Z42FuncType> funcs,
        IReadOnlyDictionary<string, Z42ClassType> classes,
        IReadOnlySet<string>? importedClassNames = null)
    {
        _parent             = null;
        _funcs              = funcs;
        _classes            = classes;
        _importedClassNames = importedClassNames ?? new HashSet<string>();
    }

    private TypeEnv(TypeEnv parent, string? currentClass = null)
    {
        _parent             = parent;
        _funcs              = parent._funcs;
        _classes            = parent._classes;
        _importedClassNames = parent._importedClassNames;
        CurrentClass        = currentClass ?? parent.CurrentClass;
    }

    // ── Scope management ─────────────────────────────────────────────────────

    /// Returns a new child scope (inherits CurrentClass).
    internal TypeEnv PushScope() => new(this);

    /// Returns a new child scope with the given class name set.
    internal TypeEnv WithClass(string className) => new(this, className);

    // ── Variable operations ───────────────────────────────────────────────────

    /// Define a variable in this scope.
    internal void Define(string name, Z42Type type) => _vars[name] = type;

    /// Returns true if <paramref name="name"/> is defined in THIS scope only (not parent scopes).
    internal bool DefinedInCurrentScope(string name) => _vars.ContainsKey(name);

    /// Look up a variable type.
    /// Returns <see langword="null"/> if the name is not a variable (including class names).
    /// Class names are NOT returned here — callers should use
    /// <see cref="IsClassName"/> to distinguish "undefined" from "this is a type".
    internal Z42Type? LookupVar(string name)
    {
        if (_vars.TryGetValue(name, out var t)) return t;
        if (_parent != null) return _parent.LookupVar(name);
        return null;
    }

    /// Returns true if <paramref name="name"/> refers to a class (local or imported).
    /// Use this to distinguish "undefined variable" from "this is a type reference"
    /// when <see cref="LookupVar"/> returns null.
    internal bool IsClassName(string name)
        => _classes.ContainsKey(name) || _importedClassNames.Contains(name);

    // ── Function operations ───────────────────────────────────────────────────

    internal Z42FuncType? LookupFunc(string name) =>
        _funcs.TryGetValue(name, out var f) ? f : null;

    // ── Class operations ──────────────────────────────────────────────────────

    internal Z42ClassType? LookupClass(string name) =>
        _classes.TryGetValue(name, out var c) ? c : null;
}
