using Z42.IR;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Parser;

namespace Z42.Semantics.Codegen;

/// <summary>
/// Abstraction over the module-level context that <see cref="FunctionEmitter"/> needs.
/// Decouples per-function emission from <see cref="IrGen"/>'s internal state,
/// making FunctionEmitter independently testable.
/// </summary>
internal interface IEmitterContext
{
    // ── Name qualification ───────────────────────────────────────────────────
    string QualifyName(string name);

    // ── String interning ─────────────────────────────────────────────────────
    int Intern(string s);

    // ── Class metadata ───────────────────────────────────────────────────────
    ClassRegistry ClassRegistry { get; }
    HashSet<string> GetClassInstanceFieldNames(string className);
    string? FindVcallParamsKey(string methodName, int suppliedArgCount);
    string? TryGetStaticFieldKey(string className, string fieldName);

    // ── Semantic model access ────────────────────────────────────────────────
    SemanticModel SemanticModel { get; }

    // ── Top-level symbols ────────────────────────────────────────────────────
    HashSet<string> TopLevelFunctionNames { get; }
    IReadOnlyDictionary<string, long> EnumConstants { get; }
    IReadOnlyDictionary<string, IReadOnlyList<Param>> FuncParams { get; }

    // ── Dependency resolution ────────────────────────────────────────────────
    DependencyIndex DepIndex { get; }
    void TrackDepNamespace(string ns);
}
