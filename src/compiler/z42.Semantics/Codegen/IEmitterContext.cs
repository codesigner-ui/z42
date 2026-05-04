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

    /// 2026-05-04 fix-default-param-cross-cu (D-9)：按 qualifiedName 查方法的
    /// `Z42FuncType` 签名（local + imported 都覆盖）。FillDefaults 用作 fallback
    /// 当 FuncParams miss 时（imported 方法无 AST `Param`）。
    bool TryGetMethodSignature(string qualifiedName, out Z42FuncType sig);

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

    /// 2026-05-02 add-method-group-conversion (D1b): allocate a module-level
    /// FuncRef cache slot for a fully-qualified function / static-method name.
    /// Shared across all call sites of the same `fqName` (去重 — design.md
    /// Decision 1). Returned slot id is consumed by `LoadFnCachedInstr`.
    int GetOrAllocFuncRefSlot(string fqName);
}
