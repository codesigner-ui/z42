using System.Text.Json;
using Z42.Compiler.Codegen;
using Z42.Compiler.Lexer;
using Z42.Compiler.Parser;

var argv = Environment.GetCommandLineArgs()[1..];

if (argv.Length == 0)
{
    Console.Error.WriteLine("Usage: z42c <source.z42> [--dump-tokens] [--dump-ast] [--dump-ir]");
    return 1;
}

string sourceFile = argv[0];
if (!File.Exists(sourceFile))
{
    Console.Error.WriteLine($"error: file not found: {sourceFile}");
    return 1;
}

string source = File.ReadAllText(sourceFile);

// ── Lex ──────────────────────────────────────────────────────────────────────

var lexer = new Lexer(source, sourceFile);
List<Token> tokens = lexer.Tokenize();

if (argv.Contains("--dump-tokens"))
{
    foreach (var tok in tokens)
        Console.WriteLine(tok);
    return 0;
}

// ── Parse ─────────────────────────────────────────────────────────────────────

var parser = new Parser(tokens);
CompilationUnit cu;
try
{
    cu = parser.ParseCompilationUnit();
}
catch (ParseException ex)
{
    Console.Error.WriteLine($"parse error at {ex.Span.Line}:{ex.Span.Column}: {ex.Message}");
    return 1;
}

if (argv.Contains("--dump-ast"))
{
    Console.WriteLine(cu);
    return 0;
}

// ── IR Codegen ────────────────────────────────────────────────────────────────

Z42.IR.IrModule irModule;
try
{
    irModule = new IrGen().Generate(cu);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"codegen error: {ex.Message}");
    return 1;
}

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
};
string irJson = JsonSerializer.Serialize(irModule, jsonOptions);

string outFile = Path.ChangeExtension(sourceFile, ".z42ir.json");
File.WriteAllText(outFile, irJson);
Console.Error.WriteLine($"wrote IR → {outFile}");

if (argv.Contains("--dump-ir"))
{
    Console.WriteLine(irJson);
}

return 0;
