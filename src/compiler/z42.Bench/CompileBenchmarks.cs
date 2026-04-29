using BenchmarkDotNet.Attributes;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Bench;

/// <summary>
/// Per-stage compilation benchmarks. Times the four pipeline stages
/// (Lex, Parse, TypeCheck, Codegen) independently across input sizes.
///
/// Inputs live under <c>Inputs/</c> and are copied to the bench output dir.
/// Add new sizes by dropping a <c>NAME.z42</c> file in <c>Inputs/</c> and
/// extending <see cref="InputSources"/>.
/// </summary>
[MemoryDiagnoser]
public class CompileBenchmarks
{
    /// <summary>Bench input name (file under Inputs/, without extension).</summary>
    [ParamsSource(nameof(InputSources))]
    public string Input { get; set; } = "";

    public static IEnumerable<string> InputSources => new[] { "small", "medium" };

    private string _source = "";
    private string _fileName = "";
    private List<Token> _tokens = new();
    private CompilationUnit _cu = null!;
    private SemanticModel _sem = null!;

    /// <summary>
    /// One-time setup per Input: load source, pre-compute Tokens / CU / SemanticModel
    /// so each benchmark measures only its own stage.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _fileName = $"{Input}.z42";
        var path = Path.Combine(AppContext.BaseDirectory, "Inputs", _fileName);
        _source = File.ReadAllText(path);

        // Pre-compute downstream artifacts so per-stage benches start from
        // realistic state (e.g. Codegen() bench starts with a SemanticModel).
        _tokens = new Lexer(_source, _fileName).Tokenize();
        var parser = new Parser(_tokens, LanguageFeatures.Phase1);
        _cu = parser.ParseCompilationUnit();
        var diags = new DiagnosticBag();
        _sem = new TypeChecker(diags, LanguageFeatures.Phase1, DependencyIndex.Empty)
            .Check(_cu, imported: null);
    }

    [Benchmark]
    public List<Token> Lex()
        => new Lexer(_source, _fileName).Tokenize();

    [Benchmark]
    public CompilationUnit Parse()
        => new Parser(_tokens, LanguageFeatures.Phase1).ParseCompilationUnit();

    [Benchmark]
    public SemanticModel TypeCheck()
    {
        var diags = new DiagnosticBag();
        return new TypeChecker(diags, LanguageFeatures.Phase1, DependencyIndex.Empty)
            .Check(_cu, imported: null);
    }

    [Benchmark]
    public IrModule Codegen()
    {
        var gen = new IrGen(DependencyIndex.Empty, LanguageFeatures.Phase1, _sem);
        return gen.Generate(_cu);
    }
}
