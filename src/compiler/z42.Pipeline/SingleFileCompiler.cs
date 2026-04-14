using System.Text.Json;
using Z42.Core.Diagnostics;
using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Project;
using Z42.Semantics.Codegen;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Pipeline;

/// <summary>
/// Compiles a single .z42 source file to a requested emit format.
/// </summary>
public static class SingleFileCompiler
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

        var parser = new Parser(tokens);
        var cu     = parser.ParseCompilationUnit();
        if (parser.Diagnostics.HasErrors)
        {
            parser.Diagnostics.PrintAll();
            return 1;
        }

        if (dumpAst) { Console.WriteLine(cu); return 0; }

        var diags = new DiagnosticBag();
        new Z42.Semantics.TypeCheck.TypeChecker(diags).Check(cu);
        diags.PrintAll();
        if (diags.HasErrors) return 1;

        // Load stdlib: scan up from the source file's directory to find artifacts/z42/libs/
        var stdlibIndex = LocateStdlibIndex(source.FullName);

        IrGen gen;
        IrModule irModule;
        try
        {
            gen = new IrGen(stdlibIndex);
            irModule = gen.Generate(cu);
        }
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
                File.WriteAllBytes(path, ZbcWriter.Write(irModule, exports: exports));
                Console.Error.WriteLine($"wrote → {path}");
                break;
            }
            case "json-zbc":
            {
                string path = outPath ?? (defaultBase + ".zbc");
                var usedNs = gen.UsedStdlibNamespaces.ToList();
                var zbc = new ZbcFile(
                    ZbcVersion : ZbcFile.CurrentVersion,
                    SourceFile : source.FullName,
                    SourceHash : sourceHash,
                    Namespace  : ns,
                    Exports    : exports,
                    Imports    : usedNs,
                    Module     : irModule);
                WriteFile(path, JsonSerializer.Serialize(zbc, jsonOptions));
                break;
            }
            case "zasm":
            {
                string path = outPath ?? (defaultBase + ".zasm");
                WriteFile(path, ZasmWriter.Write(irModule));
                break;
            }
            default:
                Console.Error.WriteLine($"error: unknown --emit format '{emit}' (valid: ir | zbc | json-zbc | zasm)");
                return 1;
        }

        return 0;
    }

    /// Walk up from the source file's directory to find artifacts/z42/libs/ and load stdlib.
    public static StdlibCallIndex LocateStdlibIndex(string sourceFullPath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFullPath) ?? ".");
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "z42", "libs");
            if (Directory.Exists(candidate))
                return PackageCompiler.BuildStdlibIndex([candidate]);
            dir = dir.Parent;
        }
        return StdlibCallIndex.Empty;
    }

    public static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, content);
        Console.Error.WriteLine($"wrote → {path}");
    }

    static string Sha256Hex(string text) => CompilerUtils.Sha256Hex(text);
}
