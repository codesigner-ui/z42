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

    /// Qualify a class name using its imported namespace if available, else local namespace.
    string QualifyClassName(string className);
    /// Maps imported class short names to their dependency namespaces.
    IReadOnlyDictionary<string, string> ImportedClassNamespaces { get; }

    // ── Lambda lifting (impl-lambda-l2) ──────────────────────────────────────

    /// Register a lifted function created during expression emission (e.g. an
    /// L2 no-capture lambda). Lifted functions are appended to the module's
    /// function list after primary emission completes.
    /// See docs/design/closure.md §6 + design.md Decision 1.
    void RegisterLiftedFunction(IrFunction fn);

    /// Allocate a unique index for a lifted lambda within `containerName`. The
    /// returned index is monotonically increasing per container; the IR emitter
    /// composes the lifted name as `<containerName>__lambda_<index>`.
    int NextLambdaIndex(string containerName);
}
