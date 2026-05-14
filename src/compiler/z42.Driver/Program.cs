using System.CommandLine;
using System.CommandLine.Invocation;
using Z42.Core.Diagnostics;
using Z42.Driver;
using Z42.IR.BinaryFormat;
using Z42.Pipeline;
using Z42.Project;

// ── Diagnostic output format ──────────────────────────────────────────────────
// Pretty mode (rust/clang-style with source context + caret) when stderr is a
// TTY; Plain mode (legacy MSBuild single-line) when piped to a file or another
// process — keeps IDE / golden-test parsing stable.
//
// Overrides:
//   Z42_DIAG_FORMAT=pretty|plain   force one mode regardless of TTY
//   NO_COLOR=<anything>            disable ANSI color in Pretty mode
{
    var fmtEnv = Environment.GetEnvironmentVariable("Z42_DIAG_FORMAT")?.Trim().ToLowerInvariant();
    bool isTty = !Console.IsErrorRedirected;
    DiagnosticBag.DefaultFormat = fmtEnv switch
    {
        "pretty" => DiagnosticOutputFormat.Pretty,
        "plain"  => DiagnosticOutputFormat.Plain,
        _        => isTty ? DiagnosticOutputFormat.Pretty : DiagnosticOutputFormat.Plain,
    };
    bool noColor = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
    DiagnosticBag.DefaultUseColor =
        DiagnosticBag.DefaultFormat == DiagnosticOutputFormat.Pretty && isTty && !noColor;

    // Force Z42.Project's module initializer (which registers WorkspaceCatalog
    // with the central DiagnosticCatalog) — `explain` / `errors` may run before
    // any other Z42.Project type is touched.
    WorkspaceCatalog.Register();
}

// ── Root command ──────────────────────────────────────────────────────────────

var rootCmd = new RootCommand("z42c — the z42 compiler");

// ── Subcommands ───────────────────────────────────────────────────────────────

rootCmd.AddCommand(BuildCommand.Create());
rootCmd.AddCommand(BuildCommand.CreateCheck());
rootCmd.AddCommand(BuildCommand.CreateRun());
rootCmd.AddCommand(BuildCommand.CreateClean());
rootCmd.AddCommand(QueryCommands.CreateInfo());
rootCmd.AddCommand(QueryCommands.CreateMetadata());
rootCmd.AddCommand(QueryCommands.CreateTree());
rootCmd.AddCommand(QueryCommands.CreateLintManifest());
rootCmd.AddCommand(ScaffoldCommands.CreateNew());
rootCmd.AddCommand(ScaffoldCommands.CreateInit());
rootCmd.AddCommand(ScaffoldCommands.CreateFmt());

// disasm
{
    var disasmCmd    = new Command("disasm", "Disassemble a .zbc or .zpkg file to text");
    var fileArg      = new Argument<FileInfo>("file", "The .zbc or .zpkg binary file to disassemble");
    var disasmOutOpt = new Option<FileInfo?>(["-o", "--output"], "Output path (default: beside input)");
    disasmCmd.AddArgument(fileArg);
    disasmCmd.AddOption(disasmOutOpt);
    disasmCmd.SetHandler((InvocationContext ctx) =>
    {
        var inFile  = ctx.ParseResult.GetValueForArgument(fileArg);
        var outFile = ctx.ParseResult.GetValueForOption(disasmOutOpt);
        if (!inFile.Exists)
        {
            Console.Error.WriteLine($"error: file not found: {inFile.FullName}");
            ctx.ExitCode = 1;
            return;
        }
        try
        {
            var    raw  = File.ReadAllBytes(inFile.FullName);
            string ext  = inFile.Extension.ToLowerInvariant();
            if (ext == ".zpkg")
            {
                var sb      = new System.Text.StringBuilder();
                var modules = ZpkgReader.ReadModules(raw);
                foreach (var (mod, ns) in modules)
                {
                    sb.AppendLine($"; === module: {ns} ===");
                    sb.AppendLine(ZasmWriter.Write(mod));
                }
                string outPath = outFile?.FullName ?? System.IO.Path.ChangeExtension(inFile.FullName, ".zpkgd");
                SingleFileCompiler.WriteFile(outPath, sb.ToString());
            }
            else
            {
                var    module  = ZbcReader.Read(raw);
                string zasm    = ZasmWriter.Write(module);
                string outPath = outFile?.FullName ?? System.IO.Path.ChangeExtension(inFile.FullName, ".zasm");
                SingleFileCompiler.WriteFile(outPath, zasm);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); ctx.ExitCode = 1; }
    });
    rootCmd.AddCommand(disasmCmd);
}

// golden-json — Stable JSON dump of a .zbc file for byte-level golden tests.
// Used by src/tests/zbc-format/ fixtures. See freeze-zbc-v1 spec archive.
{
    var goldenCmd    = new Command("golden-json", "Dump a .zbc as canonical JSON for golden tests");
    var fileArg      = new Argument<FileInfo>("file", "The .zbc binary file to dump");
    var goldenOutOpt = new Option<FileInfo?>(["-o", "--output"], "Output JSON path (default: <file>.json)");
    goldenCmd.AddArgument(fileArg);
    goldenCmd.AddOption(goldenOutOpt);
    goldenCmd.SetHandler((InvocationContext ctx) =>
    {
        var inFile  = ctx.ParseResult.GetValueForArgument(fileArg);
        var outFile = ctx.ParseResult.GetValueForOption(goldenOutOpt);
        if (!inFile.Exists)
        {
            Console.Error.WriteLine($"error: file not found: {inFile.FullName}");
            ctx.ExitCode = 1;
            return;
        }
        try
        {
            var    raw     = File.ReadAllBytes(inFile.FullName);
            var    module  = ZbcReader.Read(raw);
            string json    = ZbcGoldenJsonFormatter.Format(raw, module);
            string outPath = outFile?.FullName ?? System.IO.Path.ChangeExtension(inFile.FullName, ".json");
            SingleFileCompiler.WriteFile(outPath, json);
        }
        catch (Exception ex) { Console.Error.WriteLine($"error: {ex.Message}"); ctx.ExitCode = 1; }
    });
    rootCmd.AddCommand(goldenCmd);
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

// ── Root: single-file compilation ───────────────────────────────���─────────────

var sourceArg  = new Argument<FileInfo?>("source", () => null, "Source .z42 file to compile");
sourceArg.Arity = ArgumentArity.ZeroOrOne;
var emitOpt    = new Option<string>("--emit", () => "ir", "Output format: ir (ZASM text) | zbc (binary)");
var outOpt     = new Option<FileInfo?>(["-o", "--output"], "Output file path");
var dumpTokOpt   = new Option<bool>("--dump-tokens", "Print token stream and exit");
var dumpAstOpt   = new Option<bool>("--dump-ast",    "Print AST (after parse) and exit");
var dumpBoundOpt = new Option<bool>("--dump-bound",  "Print Bound tree (after typecheck) and exit");
var dumpIrOpt    = new Option<bool>("--dump-ir",     "Print IR (ZASM) to stdout alongside emit");

rootCmd.AddArgument(sourceArg);
rootCmd.AddOption(emitOpt);
rootCmd.AddOption(outOpt);
rootCmd.AddOption(dumpTokOpt);
rootCmd.AddOption(dumpAstOpt);
rootCmd.AddOption(dumpBoundOpt);
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
    var dumpTok   = ctx.ParseResult.GetValueForOption(dumpTokOpt);
    var dumpAst   = ctx.ParseResult.GetValueForOption(dumpAstOpt);
    var dumpBound = ctx.ParseResult.GetValueForOption(dumpBoundOpt);
    var dumpIr    = ctx.ParseResult.GetValueForOption(dumpIrOpt);
    ctx.ExitCode = SingleFileCompiler.Run(source, emit, outFile?.FullName, dumpTok, dumpAst, dumpIr, dumpBound);
});

return await rootCmd.InvokeAsync(args);
