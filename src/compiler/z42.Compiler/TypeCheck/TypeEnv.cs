namespace Z42.Compiler.TypeCheck;

/// <summary>
/// Lexically-scoped symbol table.
/// Tracks:
///   - Local variables (pushed via <see cref="Define"/>)
///   - Functions (global, shared across all scopes)
/// </summary>
internal sealed class TypeEnv
{
    // ── Known built-in class / namespace names (never local variables) ───────
    private static readonly HashSet<string> BuiltinClasses = new(StringComparer.Ordinal)
    {
        "Console", "Math", "String", "Convert", "Environment",
        "StringBuilder", "Enumerable", "List", "Array", "Dictionary",
        "File", "Path", "Directory", "Stream", "Regex",
        "GC", "Thread", "Task", "DateTime", "TimeSpan",
    };

    // ── State ────────────────────────────────────────────────────────────────

    private readonly TypeEnv?                             _parent;
    private readonly Dictionary<string, Z42FuncType>      _funcs;    // global function table
    private readonly Dictionary<string, Z42ClassType>     _classes;  // global class table
    private readonly Dictionary<string, Z42Type>           _vars = new();

    // Root env: owns both function and class tables.
    internal TypeEnv(Dictionary<string, Z42FuncType> funcs, Dictionary<string, Z42ClassType> classes)
    {
        _parent  = null;
        _funcs   = funcs;
        _classes = classes;
    }

    private TypeEnv(TypeEnv parent)
    {
        _parent  = parent;
        _funcs   = parent._funcs;
        _classes = parent._classes;
    }

    // ── Scope management ─────────────────────────────────────────────────────

    /// Returns a new child scope.
    internal TypeEnv PushScope() => new(this);

    // ── Variable operations ───────────────────────────────────────────────────

    /// Define (or redefine) a variable in this scope.
    internal void Define(string name, Z42Type type) => _vars[name] = type;

    /// Look up a variable type.
    /// Returns <see langword="null"/> if not found and not a known builtin class.
    internal Z42Type? LookupVar(string name)
    {
        if (_vars.TryGetValue(name, out var t)) return t;
        if (_parent != null) return _parent.LookupVar(name);
        // Builtin classes are always "in scope" with unknown type
        if (BuiltinClasses.Contains(name)) return Z42Type.Unknown;
        return null;
    }

    // ── Function operations ───────────────────────────────────────────────────

    internal Z42FuncType? LookupFunc(string name) =>
        _funcs.TryGetValue(name, out var f) ? f : null;

    // ── Class operations ──────────────────────────────────────────────────────

    internal Z42ClassType? LookupClass(string name) =>
        _classes.TryGetValue(name, out var c) ? c : null;
}
