using System.CommandLine;
using System.CommandLine.Invocation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Z42.Compiler.Codegen;
using Z42.Compiler.Diagnostics;
using Z42.Compiler.Lexer;
using Z42.Compiler.Parser;
using Z42.Driver;
using Z42.IR;
using Z42.IR.BinaryFormat;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy      = JsonNamingPolicy.SnakeCaseLower,
    WriteIndented             = true,
    DefaultIgnoreCondition    = JsonIgnoreCondition.WhenWritingNull,
    Converters                = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
};

// ── Root command ──────────────────────────────────────────────────────────────

var rootCmd = new RootCommand("z42c — the z42 compiler");

// ── Subcommands ───────────────────────────────────────────────────────────────

rootCmd.AddCommand(BuildCommand.Create(jsonOptions));
rootCmd.AddCommand(BuildCommand.CreateCheck());

// disasm
{
    var disasmCmd = new Command("disasm", "Disassemble a .zbc file to z42 assembly (.zasm)");
    var fileArg      = new Argument<FileInfo>("file", "The .zbc binary file to disassemble");
    var disasmOutOpt = new Option<FileInfo?>(["-o", "--output"], "Output .zasm path (default: beside input)");
    disasmCmd.AddArgument(fileArg);
    disasmCmd.AddOption(disasmOutOpt);
    disasmCmd.SetHandler((InvocationContext ctx) =>
    {
        var zbcFile = ctx.ParseResult.GetValueForArgument(fileArg);
        var outFile = ctx.ParseResult.GetValueForOption(disasmOutOpt);
        if (!zbcFile.Exists)
        {
            Console.Error.WriteLine($"error: file not found: {zbcFile.FullName}");
            ctx.ExitCode = 1;
            return;
        }
        try
        {
            var    module  = ZbcReader.Read(File.ReadAllBytes(zbcFile.FullName));
            string zasm    = ZasmWriter.Write(module);
            string outPath = outFile?.FullName ?? Path.ChangeExtension(zbcFile.FullName, ".zasm");
            SingleFileDriver.WriteFile(outPath, zasm);
        }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); ctx.ExitCode = 1; }
    });
    rootCmd.AddCommand(disasmCmd);
}

// explain
{
    var explainCmd = new Command("explain", "Explain a diagnostic error code");
    var codeArg    = new Argument<string>("code", "Error code to explain (e.g. E0001)");
    explainCmd.AddArgument(codeArg);
    explainCmd.SetHandler((InvocationContext ctx) =>
    {
        var code = ctx.ParseResult.GetValueForArgument(codeArg);
        Console.WriteLine(DiagnosticCatalog.Explain(code.ToUpperInvariant()));
    });
    rootCmd.AddCommand(explainCmd);
}

// errors
{
    var errorsCmd = new Command("errors", "List all known diagnostic error codes");
    errorsCmd.SetHandler(() => Console.WriteLine(DiagnosticCatalog.ListAll()));
    rootCmd.AddCommand(errorsCmd);
}

// ── Root: single-file compilation ─────────────────────────────────────────────

var sourceArg  = new Argument<FileInfo?>("source", () => null, "Source .z42 file to compile");
sourceArg.Arity = ArgumentArity.ZeroOrOne;
var emitOpt    = new Option<string>("--emit", () => "ir", "Output format: ir | zbc | zasm");
var outOpt     = new Option<FileInfo?>(["-o", "--output"], "Output file path");
var dumpTokOpt = new Option<bool>("--dump-tokens", "Print token stream and exit");
var dumpAstOpt = new Option<bool>("--dump-ast",    "Print AST and exit");
var dumpIrOpt  = new Option<bool>("--dump-ir",     "Print IR as JSON alongside emit");

rootCmd.AddArgument(sourceArg);
rootCmd.AddOption(emitOpt);
rootCmd.AddOption(outOpt);
rootCmd.AddOption(dumpTokOpt);
rootCmd.AddOption(dumpAstOpt);
rootCmd.AddOption(dumpIrOpt);

rootCmd.SetHandler((InvocationContext ctx) =>
{
    var source = ctx.ParseResult.GetValueForArgument(sourceArg);
    if (source is null)
    {
        Console.Error.WriteLine("error: no input file.\n\nRun `z42c --help` for usage.");
        ctx.ExitCode = 1;
        return;
    }
    if (!source.Exists)
    {
        Console.Error.WriteLine($"error[E0000]: file not found: {source.FullName}");
        ctx.ExitCode = 1;
        return;
    }
    var emit    = ctx.ParseResult.GetValueForOption(emitOpt)!;
    var outFile = ctx.ParseResult.GetValueForOption(outOpt);
    var dumpTok = ctx.ParseResult.GetValueForOption(dumpTokOpt);
    var dumpAst = ctx.ParseResult.GetValueForOption(dumpAstOpt);
    var dumpIr  = ctx.ParseResult.GetValueForOption(dumpIrOpt);
    ctx.ExitCode = SingleFileDriver.Run(source, emit, outFile?.FullName, dumpTok, dumpAst, dumpIr, jsonOptions);
});

return await rootCmd.InvokeAsync(args);

// ── Single-file driver ────────────────────────────────────────────────────────

static class SingleFileDriver
{
    public static int Run(
        FileInfo              source,
        string                emit,
        string?               outPath,
        bool                  dumpTokens,
        bool                  dumpAst,
        bool                  dumpIr,
        JsonSerializerOptions jsonOptions)
    {
        string sourceText;
        try   { sourceText = File.ReadAllText(source.FullName); }
        catch { Console.Error.WriteLine($"error: cannot read {source.FullName}"); return 1; }

        var tokens = new Lexer(sourceText, source.FullName).Tokenize();
        if (dumpTokens)
        {
            foreach (var tok in tokens) Console.WriteLine(tok);
            return 0;
        }

        CompilationUnit cu;
        try   { cu = new Parser(tokens).ParseCompilationUnit(); }
        catch (ParseException ex)
        {
            Console.Error.WriteLine($"error[E0001]: {source.Name}:{ex.Span.Line}:{ex.Span.Column}: {ex.Message}");
            return 1;
        }

        if (dumpAst) { Console.WriteLine(cu); return 0; }

        var diags = new DiagnosticBag();
        new Z42.Compiler.TypeCheck.TypeChecker(diags).Check(cu);
        if (diags.PrintAll()) return 1;

        IrModule irModule;
        try   { irModule = new IrGen().Generate(cu); }
        catch (Exception ex) { Console.Error.WriteLine($"error: codegen: {ex.Message}"); return 1; }

        if (dumpIr) Console.WriteLine(JsonSerializer.Serialize(irModule, jsonOptions));

        string ns         = cu.Namespace ?? "main";
        string sourceHash = Sha256Hex(sourceText);
        var    exports    = irModule.Functions.Select(f => f.Name).ToList();

        string defaultBase = outPath is not null
            ? Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".",
                Path.GetFileNameWithoutExtension(outPath))
            : Path.ChangeExtension(source.FullName, null);

        switch (emit)
        {
            case "ir":
            {
                string path = outPath ?? (defaultBase + ".z42ir.json");
                WriteFile(path, JsonSerializer.Serialize(irModule, jsonOptions));
                break;
            }
            case "zbc":
            {
                string path = outPath ?? (defaultBase + ".zbc");
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
                File.WriteAllBytes(path, ZbcWriter.Write(irModule, exports));
                Console.Error.WriteLine($"wrote → {path}");
                break;
            }
            case "zasm":
            {
                string path = outPath ?? (defaultBase + ".zasm");
                WriteFile(path, ZasmWriter.Write(irModule));
                break;
            }
            default:
                Console.Error.WriteLine($"error: unknown --emit format '{emit}' (valid: ir | zbc | zasm)");
                return 1;
        }

        return 0;
    }

    public static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, content);
        Console.Error.WriteLine($"wrote → {path}");
    }

    static string Sha256Hex(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
