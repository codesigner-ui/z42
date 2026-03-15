using Z42.Compiler.Lexer;
using Z42.Compiler.Parser;

var argv = Environment.GetCommandLineArgs()[1..];

if (argv.Length == 0)
{
    Console.Error.WriteLine("Usage: z42c <source.z42> [options]");
    return 1;
}

string sourceFile = argv[0];
if (!File.Exists(sourceFile))
{
    Console.Error.WriteLine($"error: file not found: {sourceFile}");
    return 1;
}

string source = File.ReadAllText(sourceFile);

// Lex
var lexer = new Lexer(source, sourceFile);
List<Token> tokens = lexer.Tokenize();

if (argv.Contains("--dump-tokens"))
{
    foreach (var tok in tokens)
        Console.WriteLine(tok);
    return 0;
}

// Parse
var parser = new Parser(tokens);
Module module;
try
{
    module = parser.ParseModule();
}
catch (ParseException ex)
{
    Console.Error.WriteLine($"parse error at {ex.Span.Line}:{ex.Span.Column}: {ex.Message}");
    return 1;
}

Console.WriteLine($"Parsed module '{module.Name}' with {module.Items.Count} items.");

// TODO: type check, IR codegen, emit .z42bc

return 0;
