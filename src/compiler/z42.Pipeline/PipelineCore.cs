using System;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Core.Text;
using Z42.IR;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

// ExportedModule lives in z42.IR; ExportedTypeExtractor lives in z42.Semantics

namespace Z42.Pipeline;

/// <summary>
/// Result of a full compilation (TypeCheck + IrGen) against a source text.
/// Contains no file-system references; all information needed by callers is embedded.
/// </summary>
public sealed record SourceCompileResult(
    IrModule?             Module,
    DiagnosticBag         Diags,
    IReadOnlySet<string>  UsedDepNamespaces,
    string?               Namespace,
    IReadOnlyList<string> Usings,
    ExportedModule?       ExportedTypes = null);

/// <summary>
/// Pure compilation core — no file I/O, no console output.
///
/// Two entry points:
/// • <see cref="Compile"/>         — full pipeline from source text (used by PackageCompiler).
/// • <see cref="CheckAndGenerate"/>— TypeCheck + IrGen from an already-parsed CompilationUnit
///                                   (used by SingleFileCompiler, which manages lex/parse itself
///                                   to support --dump-tokens / --dump-ast early exits).
///
/// Callers are responsible for reading source files and printing diagnostics.
/// This class is fully unit-testable without any file-system mocking.
/// </summary>
public static class PipelineCore
{
    /// <summary>
    /// Full pipeline: Lex → Parse → TypeCheck → IrGen.
    /// Returns a <see cref="SourceCompileResult"/> whose <c>Module</c> is null when any stage fails.
    /// </summary>
    public static SourceCompileResult Compile(
        string            source,
        string            fileName,
        DependencyIndex   depIndex,
        LanguageFeatures? features = null,
        ImportedSymbols?  imported = null)
    {
        var feats  = features ?? LanguageFeatures.Phase1;
        var diags  = new DiagnosticBag();
        var tokens = new Lexer(source, fileName).Tokenize();
        var parser = new Parser(tokens, feats);
        var cu     = parser.ParseCompilationUnit();
        foreach (var d in parser.Diagnostics.All) diags.Add(d);
        if (diags.HasErrors)
            return new(null, diags, new HashSet<string>(), cu.Namespace, cu.Usings);
        return CheckAndGenerate(cu, fileName, depIndex, feats, diags, imported);
    }

    /// <summary>
    /// TypeCheck + IrGen from an already-parsed <see cref="CompilationUnit"/>.
    /// Creates a fresh <see cref="DiagnosticBag"/>; the caller is responsible for any
    /// diagnostics produced during the preceding parse step.
    /// </summary>
    public static SourceCompileResult CheckAndGenerate(
        CompilationUnit   cu,
        string            fileName,
        DependencyIndex   depIndex,
        LanguageFeatures? features = null,
        ImportedSymbols?  imported = null)
        => CheckAndGenerate(cu, fileName, depIndex,
                            features ?? LanguageFeatures.Phase1, new DiagnosticBag(), imported);

    // ── Shared implementation ─────────────────────────────────────────────────

    private static SourceCompileResult CheckAndGenerate(
        CompilationUnit cu,
        string          fileName,
        DependencyIndex depIndex,
        LanguageFeatures feats,
        DiagnosticBag   diags,
        ImportedSymbols? imported = null)
    {
        var sem = new TypeChecker(diags, feats, depIndex).Check(cu, imported);
        if (diags.HasErrors)
            return new(null, diags, new HashSet<string>(), cu.Namespace, cu.Usings);
        try
        {
            var gen = new IrGen(depIndex, feats, sem);
            var ir  = gen.Generate(cu);
            ir = new IrPassManager().RunAll(ir);
            var exported = ExportedTypeExtractor.Extract(sem, cu.Namespace ?? "main", cu);
            return new(ir, diags, gen.UsedDepNamespaces, cu.Namespace, cu.Usings, exported);
        }
        catch (CompilationException)
        {
            // Expected: compilation failed with reported diagnostics.
            // DiagnosticBag.ThrowIfErrors() threw this with the error list already attached.
            // Return null module and let the caller report the diagnostics.
            return new(null, diags, new HashSet<string>(), cu.Namespace, cu.Usings);
        }
        // All other exceptions (NullReferenceException, InvalidOperationException, etc.)
        // are compiler internal errors (ICE) and propagate with full stack trace for debugging.
    }
}
