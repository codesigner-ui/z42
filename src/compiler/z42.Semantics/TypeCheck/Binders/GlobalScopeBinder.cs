using Z42.Semantics.Symbols;

namespace Z42.Semantics.TypeCheck.Binders;

/// <summary>
/// Root of the binder chain. Holds module-level free functions, top-level
/// classes, and imported names. Equivalent to <see cref="TypeEnv"/>'s
/// `_funcs` + `_classes` + `_importedClassNames` slots.
///
/// Phase 1 stub: a name dictionary backed by <c>Dictionary&lt;string, ISymbol&gt;</c>;
/// Phase 2+ will replace this with the actual SymbolTable / DependencyIndex
/// that TypeChecker already populates.
/// </summary>
public sealed class GlobalScopeBinder : Binder
{
    private readonly Dictionary<string, ISymbol> _globals;

    /// Construct a root global scope binder. <see cref="Binder.Next"/> is
    /// always null at this level (the global scope has no enclosing scope).
    public GlobalScopeBinder(IEnumerable<KeyValuePair<string, ISymbol>>? globals = null)
        : base(next: null)
    {
        _globals = globals != null
            ? new Dictionary<string, ISymbol>(globals)
            : new Dictionary<string, ISymbol>();
    }

    /// Define a top-level symbol. Phase 1 helper used by tests; future
    /// production wiring will populate from the SymbolTable directly.
    public void Define(ISymbol symbol)
    {
        _globals[symbol.Name] = symbol;
    }

    public override ISymbol? LookupSymbol(string name)
    {
        if (_globals.TryGetValue(name, out var s)) return s;
        return base.LookupSymbol(name);  // null at root
    }
}
