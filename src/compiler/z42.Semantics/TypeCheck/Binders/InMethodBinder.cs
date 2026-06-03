using Z42.Semantics.Symbols;

namespace Z42.Semantics.TypeCheck.Binders;

/// <summary>
/// Binder for the scope of a method body: parameters + outer-scope
/// references (`this` capture, etc.). Corresponds to <see cref="TypeEnv"/>'s
/// per-method param-modifier table and "current class" context, but
/// polymorphic — Phase 2 will give `InMethodBinder` specific knowledge of
/// the enclosing <see cref="Symbols.IMethodSymbol"/> for return-type
/// tracking, async/generator state, etc.
///
/// Phase 1 stub: parameter slots only.
/// </summary>
public sealed class InMethodBinder : Binder
{
    private readonly Dictionary<string, ISymbol> _parameters;

    public InMethodBinder(Binder next, IEnumerable<KeyValuePair<string, ISymbol>>? parameters = null)
        : base(next)
    {
        _parameters = parameters != null
            ? new Dictionary<string, ISymbol>(parameters)
            : new Dictionary<string, ISymbol>();
    }

    /// Define a parameter symbol in the method's scope. Phase 1 helper;
    /// Phase 2 wiring will populate from `FunctionDecl.Params`.
    public void DefineParameter(ISymbol parameter)
    {
        _parameters[parameter.Name] = parameter;
    }

    public override ISymbol? LookupSymbol(string name)
    {
        if (_parameters.TryGetValue(name, out var s)) return s;
        return base.LookupSymbol(name);
    }
}
