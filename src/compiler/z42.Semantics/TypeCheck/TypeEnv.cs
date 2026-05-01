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
    /// Local function signatures defined in this scope. Lexically scoped: a
    /// `LookupLocalFunc` walks the parent chain. See docs/design/closure.md §3.4
    /// + impl-local-fn-l2 design Decision 3.
    private readonly Dictionary<string, Z42FuncType>                _localFuncs = new();

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

    /// L2 capture detection helper: returns true if `name` resolves to a
    /// variable in `this` scope or any descendant scope strictly **below**
    /// (i.e. nearer than) `boundary`. Returns false if walking up reaches
    /// `boundary` without finding the name (which means the name lives at
    /// the boundary or above — i.e. it's a capture).
    /// See docs/design/closure.md §10 + impl-local-fn-l2 design.
    internal bool ResolvesVarBelowBoundary(string name, TypeEnv boundary)
    {
        if (ReferenceEquals(this, boundary)) return false;
        if (_vars.ContainsKey(name)) return true;
        return _parent != null && _parent.ResolvesVarBelowBoundary(name, boundary);
    }

    /// L3 transitive capture helper: returns true if `name` is defined in
    /// `this` scope or any of its ancestors (i.e. visible from `this`).
    /// Used to walk the lambda binding stack for nested-capture propagation.
    /// See impl-closure-l3-core design Decision 6.
    internal bool ResolvesVarBelowBoundaryOrSelf(string name)
    {
        if (_vars.ContainsKey(name)) return true;
        return _parent != null && _parent.ResolvesVarBelowBoundaryOrSelf(name);
    }

    /// Returns true if <paramref name="name"/> refers to a class (local or imported).
    /// Use this to distinguish "undefined variable" from "this is a type reference"
    /// when <see cref="LookupVar"/> returns null.
    internal bool IsClassName(string name)
        => _classes.ContainsKey(name) || _importedClassNames.Contains(name);

    // ── Function operations ───────────────────────────────────────────────────

    /// Lookup a function: prefer scope-local definitions (lexically nearest)
    /// before falling back to the module-level global function table.
    /// Local fn lookup is part of `impl-local-fn-l2` (see closure.md §3.4).
    internal Z42FuncType? LookupFunc(string name)
    {
        if (_localFuncs.TryGetValue(name, out var lf)) return lf;
        if (_parent != null)
        {
            var fromParent = _parent.LookupFunc(name);
            if (fromParent != null) return fromParent;
        }
        return _funcs.TryGetValue(name, out var f) ? f : null;
    }

    /// Define a local function in this scope. Returns false if the name is
    /// already defined in this scope (caller should report duplicate-decl).
    internal bool DefineLocalFunc(string name, Z42FuncType sig)
    {
        if (_localFuncs.ContainsKey(name)) return false;
        _localFuncs[name] = sig;
        return true;
    }

    /// Returns true if `name` is a local function defined in *this* scope or any
    /// ancestor scope (i.e. not the module-level global table). Used by Codegen
    /// to disambiguate `Call` sites without a separate IEmitterContext API.
    internal bool IsLocalFunc(string name)
    {
        if (_localFuncs.ContainsKey(name)) return true;
        return _parent != null && _parent.IsLocalFunc(name);
    }

    // ── Class operations ──────────────────────────────────────────────────────

    internal Z42ClassType? LookupClass(string name) =>
        _classes.TryGetValue(name, out var c) ? c : null;
}
