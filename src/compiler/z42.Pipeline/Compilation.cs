using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Parser;

namespace Z42.Pipeline;

/// <summary>
/// Immutable snapshot of a compilation's inputs + lazily-computed semantic
/// output. Roslyn `CSharpCompilation` parallel — review.md F2.1 Phase 1
/// (2026-06-03, add-compilation-snapshot-phase1).
///
/// Wraps the procedural <see cref="PipelineCore.CheckOnly"/> entry point
/// behind a value-object API so callers that hold a `Compilation` reference
/// can:
///
/// • re-query <see cref="Model"/> / <see cref="Diagnostics"/> without
///   redoing the typecheck pass (lazy + cached on first access);
/// • produce a derivative snapshot via <see cref="WithCompilationUnit"/>
///   or <see cref="WithImported"/> without mutating the original;
/// • thread a single Compilation reference through layered tooling
///   (analyzer / formatter / future IDE LSP) and rely on its contract
///   that no input field changes after construction.
///
/// Phase 1 scope (this file): wrapper + lazy cache + With-style updates +
/// hand-off to existing PipelineCore.CheckOnly. Phase 2+ (separate specs)
/// adds SyntaxTree wrapping, multi-tree Compilation, References,
/// incremental rebind on single-tree substitution, and Emit.
///
/// # Thread safety
///
/// <see cref="System.Threading.LazyThreadSafetyMode.PublicationOnly"/> for
/// <see cref="Model"/> + <see cref="Diagnostics"/>: at worst, racing
/// concurrent first-readers redo the typecheck, but only one result wins
/// + is observed by all subsequent readers. No locking on the hot path.
/// </summary>
public sealed class Compilation
{
    /// The single AST tree this compilation snapshots. Phase 1 is single-CU;
    /// Phase 2 will replace this with `IReadOnlyList&lt;SyntaxTree&gt;`.
    public CompilationUnit CompilationUnit { get; }

    /// Dependency index resolved at construction time. Treated as immutable —
    /// passing the same DepIndex to two Compilations is legal; mutating it
    /// between accesses is not (no test or production caller does today).
    public DependencyIndex DepIndex { get; }

    /// Active language-feature gates.
    public LanguageFeatures Features { get; }

    /// Imported symbols from dependency modules. Null means "no cross-module
    /// imports" — same semantics as the PipelineCore overload that takes
    /// `imported: null`.
    public ImportedSymbols? Imported { get; }

    private readonly Lazy<(SemanticModel? Model, DiagnosticBag Diags)> _bindResult;

    /// Resolved <see cref="SemanticModel"/>, or `null` if typecheck produced
    /// errors. First access triggers binding; subsequent accesses hit the
    /// cache. To distinguish "no errors but no model" from "errors", check
    /// <see cref="Diagnostics"/>.<see cref="DiagnosticBag.HasErrors"/>.
    public SemanticModel? Model => _bindResult.Value.Model;

    /// Diagnostics emitted during binding. Includes all errors that
    /// prevented Model from being produced.
    public DiagnosticBag Diagnostics => _bindResult.Value.Diags;

    private Compilation(
        CompilationUnit  cu,
        DependencyIndex  depIndex,
        LanguageFeatures features,
        ImportedSymbols? imported)
    {
        CompilationUnit = cu;
        DepIndex        = depIndex;
        Features        = features;
        Imported        = imported;
        _bindResult = new Lazy<(SemanticModel?, DiagnosticBag)>(
            () => PipelineCore.CheckOnly(cu, depIndex, features, imported),
            System.Threading.LazyThreadSafetyMode.PublicationOnly);
    }

    /// Build a fresh <see cref="Compilation"/> snapshot. The instance is
    /// idempotent — calling <see cref="Model"/> multiple times resolves
    /// the typecheck pass exactly once.
    public static Compilation Create(
        CompilationUnit   cu,
        DependencyIndex   depIndex,
        LanguageFeatures? features = null,
        ImportedSymbols?  imported = null)
        => new(cu, depIndex, features ?? LanguageFeatures.Phase1, imported);

    /// Return a new <see cref="Compilation"/> that swaps in a different
    /// `CompilationUnit` while preserving the original `DepIndex` /
    /// `Features` / `Imported`. Original instance is untouched; if its
    /// <see cref="Model"/> was already resolved, the cache stays valid.
    public Compilation WithCompilationUnit(CompilationUnit cu) =>
        new(cu, DepIndex, Features, Imported);

    /// Return a new <see cref="Compilation"/> with a different
    /// `ImportedSymbols`. Mirrors Roslyn `CSharpCompilation.WithReferences`.
    public Compilation WithImported(ImportedSymbols? imported) =>
        new(CompilationUnit, DepIndex, Features, imported);
}
