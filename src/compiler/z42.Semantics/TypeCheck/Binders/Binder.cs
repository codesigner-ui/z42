using Z42.Semantics.Symbols;

namespace Z42.Semantics.TypeCheck.Binders;

/// <summary>
/// Roslyn `Binder` parallel — abstract base for a lexically-scoped symbol
/// lookup chain. review.md F2.4 Phase 1 (2026-06-03,
/// add-binder-hierarchy-phase1) introduces the contract; concrete
/// subclasses (<see cref="GlobalScopeBinder"/>, <see cref="InMethodBinder"/>,
/// <see cref="InBlockBinder"/>) are stubs that demonstrate the chain
/// dispatch. **Phase 1 binders are NOT yet consumed by TypeChecker** —
/// they live as parallel infrastructure that future Phase 2-N specs will
/// progressively migrate the existing <see cref="TypeEnv"/>-based lookup
/// sites onto.
///
/// See <c>docs/design/compiler/binder-hierarchy.md</c> for the long-form
/// design (Roslyn comparison, TypeEnv → Binder migration plan, scope-class
/// taxonomy).
///
/// # Lookup dispatch
///
/// Each binder owns its scope's name table; <see cref="LookupSymbol"/> first
/// consults its own table, then forwards to <see cref="Next"/>. A typical
/// chain at a deep call site looks like:
///
/// <code>
/// InBlockBinder (innermost for / if / switch)
///   → InMethodBinder (method body — params, return-type context)
///     → InClassBinder (Phase 2 — class members, base lookup)
///       → InNamespaceBinder (Phase 2 — same-namespace types)
///         → GlobalScopeBinder (top-level functions / classes)
///           → null
/// </code>
///
/// # Why not just patch TypeEnv
///
/// `TypeEnv` is a single sealed class with dictionary-backed slots for every
/// scope kind (vars / local funcs / param mods / func aliases / ...). Each
/// new scope concern means a new dictionary on every TypeEnv instance, even
/// scopes that don't care about it. The Binder pattern lets each scope
/// allocate only the slots it actually uses, and lets a future spec add
/// `InCatchBinder` or `InLambdaBinder` as a new class without touching the
/// `for`-loop or method-body binders.
/// </summary>
public abstract class Binder
{
    /// Parent (enclosing) binder in the lookup chain. Null at the global
    /// scope root.
    public Binder? Next { get; }

    protected Binder(Binder? next)
    {
        Next = next;
    }

    /// Look up a symbol by name. Default implementation forwards to
    /// <see cref="Next"/>; concrete subclasses override to consult their
    /// own scope first.
    public virtual ISymbol? LookupSymbol(string name) => Next?.LookupSymbol(name);
}
