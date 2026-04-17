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

    private TypeEnv(TypeEnv parent)
    {
        _parent             = parent;
        _funcs              = parent._funcs;
        _classes            = parent._classes;
        _importedClassNames = parent._importedClassNames;
    }

    // ── Scope management ─────────────────────────────────────────────────────

    /// Returns a new child scope.
    internal TypeEnv PushScope() => new(this);

    // ── Variable operations ───────────────────────────────────────────────────

    /// Define a variable in this scope.
    internal void Define(string name, Z42Type type) => _vars[name] = type;

    /// Returns true if <paramref name="name"/> is defined in THIS scope only (not parent scopes).
    internal bool DefinedInCurrentScope(string name) => _vars.ContainsKey(name);

    /// Look up a variable type.
    /// Returns <see langword="null"/> if not found and not a known builtin class.
    internal Z42Type? LookupVar(string name)
    {
        if (_vars.TryGetValue(name, out var t)) return t;
        if (_parent != null) return _parent.LookupVar(name);
        // Class names (both local and imported) are recognizable as type references.
        // Return Z42Type.Unknown to maintain backward compatibility with existing
        // member access and call resolution paths (static field access, constructor calls, etc.).
        if (_classes.ContainsKey(name)) return Z42Type.Unknown;
        // Imported class names that are not in _classes (kept out to avoid
        // ResolveType picking them up as Z42ClassType).
        if (_importedClassNames.Contains(name)) return Z42Type.Unknown;
        return null;
    }

    // ── Function operations ───────────────────────────────────────────────────

    internal Z42FuncType? LookupFunc(string name) =>
        _funcs.TryGetValue(name, out var f) ? f : null;

    // ── Class operations ──────────────────────────────────────────────────────

    internal Z42ClassType? LookupClass(string name) =>
        _classes.TryGetValue(name, out var c) ? c : null;
}
