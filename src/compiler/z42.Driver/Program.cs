using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Z42.Compiler.Diagnostics;
using Z42.Compiler.Lexer;
using Z42.Compiler.Parser;
using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Driver;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy      = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented             = true,
    DefaultIgnoreCondition    = JsonIgnoreCondition.WhenWritingNull,
    Converters                = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
};

var argv = Environment.GetCommandLineArgs()[1..];

// ── --explain / --list-errors ─────────────────────────────────────────────────

if (argv.Length >= 2 && argv[0] == "--explain")
{
    Console.WriteLine(DiagnosticCatalog.Explain(argv[1].ToUpperInvariant()));
    return 0;
}

if (argv.Length >= 1 && argv[0] == "--list-errors")
{
    Console.WriteLine(DiagnosticCatalog.ListAll());
    return 0;
}

// ── --disassemble ─────────────────────────────────────────────────────────────

if (argv.Length >= 2 && argv[0] == "--disassemble")
{
    string zbcFile = argv[1];
    if (!File.Exists(zbcFile)) { Console.Error.WriteLine($"error: file not found: {zbcFile}"); return 1; }
    try
    {
        var module = ZbcReader.Read(File.ReadAllBytes(zbcFile));
        string zasm     = ZasmWriter.Write(module);
        string zasmPath = argv.Length >= 4 && argv[2] == "--out" ? argv[3]
                        : Path.ChangeExtension(zbcFile, ".zasm");
        SingleFileDriver.WriteFile(zasmPath, zasm);
    }
    catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }
    return 0;
}

// ── No args: show help ────────────────────────────────────────────────────────

if (argv.Length == 0)
{
    Console.Error.WriteLine("""
        z42c — z42 compiler

        ╔══════════════════════════════════════════════════════════════════╗
        ║  Project mode  —  build a complete project from z42.toml        ║
        ║  Use this for: multi-file builds, profiles, output to dist/     ║
        ╚══════════════════════════════════════════════════════════════════╝

          z42c build [<name>.z42.toml] [options]

          Options:
            --release              Use profile.release (default: profile.debug)
            --profile <name>       Use a named profile

          Examples:
            z42c build                    # auto-discover *.z42.toml, debug build (indexed .zpkg)
            z42c build --release          # release build (packed .zpkg)
            z42c build hello.z42.toml     # explicit project file

        ╔══════════════════════════════════════════════════════════════════╗
        ║  Single-file mode  —  compile one .z42 file directly            ║
        ║  Use this for: quick tests, debugging, exploring the pipeline   ║
        ╚══════════════════════════════════════════════════════════════════╝

          z42c <source.z42> [options]

          Options:
            --emit <format>        ir | zbc | zasm  (default: ir)
            --out <path>           Output file path
            --dump-tokens          Print token stream and exit
            --dump-ast             Print AST and exit
            --dump-ir              Print IR as JSON

          Examples:
            z42c hello.z42                      # compile to .z42ir.json
            z42c hello.z42 --emit zbc           # compile to binary bytecode
            z42c hello.z42 --dump-tokens        # inspect lexer output

        ╔══════════════════════════════════════════════════════════════════╗
        ║  Toolchain utilities                                            ║
        ╚══════════════════════════════════════════════════════════════════╝

          z42c --disassemble <file.zbc> [--out <file.zasm>]
          z42c --explain <ERROR_CODE>
          z42c --list-errors
        """);
    return 1;
}

// ── Route: project mode ───────────────────────────────────────────────────────

if (argv[0] == "build")
    return BuildCommand.Run(argv[1..], jsonOptions);

// ── Route: single-file mode ───────────────────────────────────────────────────

string sourceFile = argv[0];
if (!File.Exists(sourceFile))
{
    Console.Error.WriteLine($"error: file not found: {sourceFile}");
    return 1;
}

// Parse flags
string emitMode = "ir";
string? outPath = null;
for (int i = 1; i < argv.Length - 1; i++)
{
    if (argv[i] == "--emit") { emitMode = argv[i + 1]; }
    if (argv[i] == "--out")  { outPath  = argv[i + 1]; }
}

string source = File.ReadAllText(sourceFile);

// Lex
var lexer  = new Lexer(source, sourceFile);
var tokens = lexer.Tokenize();

if (argv.Contains("--dump-tokens"))
{
    foreach (var tok in tokens) Console.WriteLine(tok);
    return 0;
}

// Parse
Z42.Compiler.Parser.CompilationUnit cu;
try   { cu = new Z42.Compiler.Parser.Parser(tokens).ParseCompilationUnit(); }
catch (ParseException ex)
{
    Console.Error.WriteLine($"parse error at {ex.Span.Line}:{ex.Span.Column}: {ex.Message}");
    return 1;
}

if (argv.Contains("--dump-ast")) { Console.WriteLine(cu); return 0; }

// TypeCheck
var diags = new Z42.Compiler.Diagnostics.DiagnosticBag();
new Z42.Compiler.TypeCheck.TypeChecker(diags).Check(cu);
if (diags.PrintAll()) return 1;

// Codegen
Z42.IR.IrModule irModule;
try   { irModule = new Z42.Compiler.Codegen.IrGen().Generate(cu); }
catch (Exception ex) { Console.Error.WriteLine($"codegen error: {ex.Message}"); return 1; }

if (argv.Contains("--dump-ir"))
    Console.WriteLine(JsonSerializer.Serialize(irModule, jsonOptions));

// Emit (single-file: output beside source file unless --out specified)
string ns         = cu.Namespace ?? "main";
string sourceHash = SingleFileDriver.Sha256Hex(source);
var    exports    = irModule.Functions.Select(f => f.Name).ToList();

string absSource   = Path.GetFullPath(sourceFile);
string defaultBase = outPath is not null
    ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".", Path.GetFileNameWithoutExtension(outPath))
    : Path.ChangeExtension(absSource, null);

switch (emitMode)
{
    case "ir":
    {
        string path = outPath ?? (defaultBase + ".z42ir.json");
        SingleFileDriver.WriteFile(path, JsonSerializer.Serialize(irModule, jsonOptions));
        break;
    }
    case "zbc":
    {
        string path = outPath ?? (defaultBase + ".zbc");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllBytes(path, Z42.IR.BinaryFormat.ZbcWriter.Write(irModule, exports));
        Console.Error.WriteLine($"wrote → {path}");
        break;
    }
    case "zasm":
    {
        string path = outPath ?? (defaultBase + ".zasm");
        SingleFileDriver.WriteFile(path, ZasmWriter.Write(irModule));
        break;
    }
    default:
        Console.Error.WriteLine($"error: unknown --emit mode '{emitMode}' (ir | zbc | zasm)");
        return 1;
}

return 0;

// ── Shared helpers ────────────────────────────────────────────────────────────

static class SingleFileDriver
{
    public static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, content);
        Console.Error.WriteLine($"wrote → {path}");
    }
}
