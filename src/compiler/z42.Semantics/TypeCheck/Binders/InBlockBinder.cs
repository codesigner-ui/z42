using Z42.Semantics.Symbols;

namespace Z42.Semantics.TypeCheck.Binders;

/// <summary>
/// Binder for a block-scope `{ ... }` introducing local variables. The
/// innermost binder at most TypeChecker sites. Corresponds to
/// <see cref="TypeEnv"/>'s `_vars` dictionary, but each block gets its own
/// binder instance — making shadowing semantics fall out of the chain
/// dispatch (inner binder's `LookupSymbol` finds its own slot before
/// forwarding to the parent).
///
/// Phase 1 stub: local-variable slots only.
/// </summary>
public sealed class InBlockBinder : Binder
{
    private readonly Dictionary<string, ISymbol> _locals = new();

    public InBlockBinder(Binder next) : base(next) { }

    /// Define a local variable. Returns false if `name` is already defined
    /// in **this** block (caller should emit a duplicate-decl diagnostic).
    public bool DefineLocal(ISymbol local)
    {
        if (_locals.ContainsKey(local.Name)) return false;
        _locals[local.Name] = local;
        return true;
    }

    /// True if `name` is defined in this block (not searching parents).
    /// Mirrors `TypeEnv.DefinedInCurrentScope`.
    public bool DefinedInCurrentScope(string name) => _locals.ContainsKey(name);

    public override ISymbol? LookupSymbol(string name)
    {
        if (_locals.TryGetValue(name, out var s)) return s;
        return base.LookupSymbol(name);
    }
}
