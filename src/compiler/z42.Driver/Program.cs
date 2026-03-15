using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Z42.Compiler.Codegen;
using Z42.Compiler.Lexer;
using Z42.Compiler.Parser;
using Z42.IR;

// ── Helpers ───────────────────────────────────────────────────────────────────

static string Sha256Hex(string text)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
    return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
}

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy      = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented             = true,
    DefaultIgnoreCondition    = JsonIgnoreCondition.WhenWritingNull,
    Converters                = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
};

static void WriteFile(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
    File.WriteAllText(path, content);
    Console.Error.WriteLine($"wrote → {path}");
}

// ── CLI ───────────────────────────────────────────────────────────────────────

var argv = Environment.GetCommandLineArgs()[1..];

if (argv.Length == 0)
{
    Console.Error.WriteLine("""
        Usage:
          z42c <source.z42> [--emit ir|zbc|zmod|zlib] [--out <path>]
               [--dump-tokens] [--dump-ast] [--dump-ir]

        --emit ir    (default) write .z42ir.json (debug IR)
        --emit zbc   write .zbc (single-file bytecode unit)
        --emit zmod  write .zmod manifest + per-file .zbc into .cache/
        --emit zlib  write .zlib assembly (all modules bundled)
        """);
    return 1;
}

string sourceFile = argv[0];
if (!File.Exists(sourceFile))
{
    Console.Error.WriteLine($"error: file not found: {sourceFile}");
    return 1;
}

// Parse --emit flag (default: ir)
string emitMode = "ir";
for (int i = 1; i < argv.Length - 1; i++)
    if (argv[i] == "--emit") { emitMode = argv[i + 1]; break; }

string source = File.ReadAllText(sourceFile);

// ── Lex ───────────────────────────────────────────────────────────────────────

var lexer = new Lexer(source, sourceFile);
List<Token> tokens = lexer.Tokenize();

if (argv.Contains("--dump-tokens"))
{
    foreach (var tok in tokens) Console.WriteLine(tok);
    return 0;
}

// ── Parse ─────────────────────────────────────────────────────────────────────

var parser = new Parser(tokens);
CompilationUnit cu;
try   { cu = parser.ParseCompilationUnit(); }
catch (ParseException ex)
{
    Console.Error.WriteLine($"parse error at {ex.Span.Line}:{ex.Span.Column}: {ex.Message}");
    return 1;
}

if (argv.Contains("--dump-ast")) { Console.WriteLine(cu); return 0; }

// ── IR Codegen ────────────────────────────────────────────────────────────────

IrModule irModule;
try   { irModule = new IrGen().Generate(cu); }
catch (Exception ex)
{
    Console.Error.WriteLine($"codegen error: {ex.Message}");
    return 1;
}

if (argv.Contains("--dump-ir"))
    Console.WriteLine(JsonSerializer.Serialize(irModule, jsonOptions));

// ── Emit ──────────────────────────────────────────────────────────────────────

string ns = cu.Namespace ?? "main";
string sourceHash = Sha256Hex(source);
var exports = irModule.Functions.Select(f => f.Name).ToList();

switch (emitMode)
{
    // ── --emit ir (default) ─────────────────────────────────────────────────
    case "ir":
    {
        string outPath = Path.ChangeExtension(sourceFile, ".z42ir.json");
        WriteFile(outPath, JsonSerializer.Serialize(irModule, jsonOptions));
        break;
    }

    // ── --emit zbc ──────────────────────────────────────────────────────────
    case "zbc":
    {
        var zbc = new ZbcFile(
            ZbcVersion : ZbcFile.CurrentVersion,
            SourceFile : sourceFile,
            SourceHash : sourceHash,
            Namespace  : ns,
            Exports    : exports,
            Imports    : [],
            Module     : irModule
        );
        string outPath = Path.ChangeExtension(sourceFile, ".zbc");
        WriteFile(outPath, JsonSerializer.Serialize(zbc, jsonOptions));
        break;
    }

    // ── --emit zmod ─────────────────────────────────────────────────────────
    case "zmod":
    {
        // .zbc goes into .cache/ next to the source file
        string cacheDir  = Path.Combine(Path.GetDirectoryName(sourceFile) ?? ".", ".cache");
        string zbcName   = Path.ChangeExtension(Path.GetFileName(sourceFile), ".zbc");
        string zbcPath   = Path.Combine(cacheDir, zbcName);
        string zbcRel    = Path.GetRelativePath(Path.GetDirectoryName(sourceFile) ?? ".", zbcPath);

        var zbc = new ZbcFile(
            ZbcVersion : ZbcFile.CurrentVersion,
            SourceFile : sourceFile,
            SourceHash : sourceHash,
            Namespace  : ns,
            Exports    : exports,
            Imports    : [],
            Module     : irModule
        );
        WriteFile(zbcPath, JsonSerializer.Serialize(zbc, jsonOptions));

        // Write .zmod manifest beside the source file
        var entry = new ZmodFileEntry(
            Source     : sourceFile,
            Bytecode   : zbcRel,
            SourceHash : sourceHash,
            Exports    : exports
        );
        var zmod = new ZmodManifest(
            ZmodVersion  : ZmodManifest.CurrentVersion,
            Name         : ns,
            Version      : "0.1.0",
            Kind         : ZmodKind.Exe,
            Files        : [entry],
            Dependencies : [],
            Entry        : $"{ns}.Main"
        );
        string zmodPath = Path.ChangeExtension(sourceFile, ".zmod");
        WriteFile(zmodPath, JsonSerializer.Serialize(zmod, jsonOptions));
        break;
    }

    // ── --emit zlib ─────────────────────────────────────────────────────────
    case "zlib":
    {
        var zbc = new ZbcFile(
            ZbcVersion : ZbcFile.CurrentVersion,
            SourceFile : sourceFile,
            SourceHash : sourceHash,
            Namespace  : ns,
            Exports    : exports,
            Imports    : [],
            Module     : irModule
        );
        var libExports = exports
            .Select(e => new ZlibExport($"{ns}.{e}", "func"))
            .ToList();
        var zlib = new ZlibFile(
            ZlibVersion  : ZlibFile.CurrentVersion,
            Name         : ns,
            Version      : "0.1.0",
            Kind         : ZmodKind.Exe,
            Exports      : libExports,
            Dependencies : [],
            Modules      : [zbc],
            Entry        : $"{ns}.Main"
        );
        string outPath = Path.ChangeExtension(sourceFile, ".zlib");
        WriteFile(outPath, JsonSerializer.Serialize(zlib, jsonOptions));
        break;
    }

    default:
        Console.Error.WriteLine($"error: unknown --emit mode '{emitMode}' (ir | zbc | zmod | zlib)");
        return 1;
}

return 0;
