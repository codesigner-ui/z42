using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Core.Text;
using Z42.IR;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Pipeline;

/// <summary>
/// Result of a full compilation (TypeCheck + IrGen) against a source text.
/// Contains no file-system references; all information needed by callers is embedded.
/// </summary>
public sealed record SourceCompileResult(
    IrModule?             Module,
    DiagnosticBag         Diags,
    IReadOnlySet<string>  UsedStdlibNamespaces,
    string?               Namespace,
    IReadOnlyList<string> Usings);

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
        StdlibCallIndex   stdlibIndex,
        LanguageFeatures? features = null)
    {
        var feats  = features ?? LanguageFeatures.Phase1;
        var diags  = new DiagnosticBag();
        var tokens = new Lexer(source, fileName).Tokenize();
        var parser = new Parser(tokens, feats);
        var cu     = parser.ParseCompilationUnit();
        foreach (var d in parser.Diagnostics.All) diags.Add(d);
        if (diags.HasErrors)
            return new(null, diags, new HashSet<string>(), cu.Namespace, cu.Usings);
        return CheckAndGenerate(cu, fileName, stdlibIndex, feats, diags);
    }

    /// <summary>
    /// TypeCheck + IrGen from an already-parsed <see cref="CompilationUnit"/>.
    /// Creates a fresh <see cref="DiagnosticBag"/>; the caller is responsible for any
    /// diagnostics produced during the preceding parse step.
    /// </summary>
    public static SourceCompileResult CheckAndGenerate(
        CompilationUnit   cu,
        string            fileName,
        StdlibCallIndex   stdlibIndex,
        LanguageFeatures? features = null)
        => CheckAndGenerate(cu, fileName, stdlibIndex,
                            features ?? LanguageFeatures.Phase1, new DiagnosticBag());

    // ── Shared implementation ─────────────────────────────────────────────────

    private static SourceCompileResult CheckAndGenerate(
        CompilationUnit cu,
        string          fileName,
        StdlibCallIndex stdlibIndex,
        LanguageFeatures feats,
        DiagnosticBag   diags)
    {
        var sem = new TypeChecker(diags, feats).Check(cu);
        if (diags.HasErrors)
            return new(null, diags, new HashSet<string>(), cu.Namespace, cu.Usings);
        try
        {
            var gen = new IrGen(stdlibIndex, feats, sem);
            var ir  = gen.Generate(cu);
            ir = new IrPassManager().RunAll(ir);
            return new(ir, diags, gen.UsedStdlibNamespaces, cu.Namespace, cu.Usings);
        }
        catch (Exception ex)
        {
            diags.Error(DiagnosticCodes.UnsupportedSyntax, ex.Message,
                        new Span(0, 0, 0, 0, fileName));
            return new(null, diags, new HashSet<string>(), cu.Namespace, cu.Usings);
        }
    }
}
