using FluentAssertions;
using Z42.Core.Features;
using Z42.IR;
using Z42.Pipeline;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// Unit tests for the `Compilation` immutable snapshot wrapper
/// (review.md F2.1 Phase 1, add-compilation-snapshot-phase1 2026-06-03).
public sealed class CompilationTests
{
    /// Parse + wrap in a Compilation snapshot — helper for tests that
    /// only care about the wrapper, not the parser.
    private static Compilation BuildSnapshot(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        return Compilation.Create(cu, DependencyIndex.Empty);
    }

    [Fact]
    public void Create_StoresInputsImmutably()
    {
        var tokens = new Lexer("void Main() { var x = 1; }").Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var dep    = DependencyIndex.Empty;

        var c = Compilation.Create(cu, dep);

        c.CompilationUnit.Should().BeSameAs(cu);
        c.DepIndex.Should().BeSameAs(dep);
        c.Features.Should().BeSameAs(LanguageFeatures.Phase1);
        c.Imported.Should().BeNull();
    }

    [Fact]
    public void Model_LazilyComputed_FirstAccessRunsTypecheck()
    {
        var c = BuildSnapshot("void Main() { var x = 1; }");

        // First access — binding fires.
        var m1 = c.Model;
        m1.Should().NotBeNull();

        // Second access — same instance, no re-bind.
        var m2 = c.Model;
        m2.Should().BeSameAs(m1, "Model is cached after first access");
    }

    [Fact]
    public void Diagnostics_CapturesTypecheckErrors()
    {
        // Calling an undeclared function — typechecker emits an error,
        // PipelineCore.CheckOnly returns (null Model, populated Diags).
        var c = BuildSnapshot("void Main() { call_an_undefined_function(); }");

        c.Diagnostics.HasErrors.Should().BeTrue();
        c.Model.Should().BeNull("binding aborts when typecheck sees errors");

        // Second access returns the same DiagnosticBag instance from the
        // cache (not a re-bound copy).
        c.Diagnostics.Should().BeSameAs(c.Diagnostics);
    }

    [Fact]
    public void WithCompilationUnit_ReturnsFreshSnapshot_OriginalUnchanged()
    {
        var c1 = BuildSnapshot("void Main() { var x = 1; }");
        var originalModel = c1.Model;
        originalModel.Should().NotBeNull();

        // Swap in a different CU — same parser, different source.
        var tokens2 = new Lexer("void Main() { var y = 42; }").Tokenize();
        var cu2     = new Parser(tokens2, LanguageFeatures.Phase1).ParseCompilationUnit();
        var c2      = c1.WithCompilationUnit(cu2);

        // Fresh snapshot has the new CU.
        c2.CompilationUnit.Should().BeSameAs(cu2);
        c2.CompilationUnit.Should().NotBeSameAs(c1.CompilationUnit);

        // Original cached Model still valid.
        c1.Model.Should().BeSameAs(originalModel,
            "WithCompilationUnit must not mutate or invalidate the original snapshot's cache");

        // New snapshot has its own freshly-bound Model.
        var newModel = c2.Model;
        newModel.Should().NotBeNull();
        newModel.Should().NotBeSameAs(originalModel,
            "fresh snapshot must produce its own SemanticModel");
    }

    [Fact]
    public void WithImported_PreservesCuButFreshlyBinds()
    {
        var c1 = BuildSnapshot("void Main() { }");
        var firstModel = c1.Model;

        var c2 = c1.WithImported(null);  // identity swap; still a fresh snapshot
        c2.CompilationUnit.Should().BeSameAs(c1.CompilationUnit,
            "WithImported keeps CU shared");

        // Fresh snapshot has its own Lazy — the new Model is a distinct
        // SemanticModel instance (binding is re-run for the new snapshot).
        c2.Model.Should().NotBeSameAs(firstModel);
    }
}
