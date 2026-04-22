using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Project;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Pipeline;

/// <summary>
/// Compiles a single .z42 source file to a requested emit format.
/// </summary>
public static class SingleFileCompiler
{
    public static int Run(
        FileInfo source,
        string   emit,
        string?  outPath,
        bool     dumpTokens,
        bool     dumpAst,
        bool     dumpIr)
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

        // Load dependencies: scan up from the source file's directory to find artifacts/z42/libs/
        var depIndex = LocateDepIndex(source.FullName);

        // Load stdlib TSIG so that Console/Assert/Math etc. are visible at compile time
        // without requiring explicit `using` declarations.
        var imported = LocateImportedSymbols(source.FullName);

        var result = PipelineCore.CheckAndGenerate(cu, source.FullName, depIndex, imported: imported);
        result.Diags.PrintAll();
        if (result.Diags.HasErrors || result.Module is null) return 1;

        var irModule = result.Module;

        if (dumpIr) Console.WriteLine(ZasmWriter.Write(irModule));

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
                string path = outPath ?? (defaultBase + ".zasm");
                WriteFile(path, ZasmWriter.Write(irModule));
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
            default:
                Console.Error.WriteLine($"error: unknown --emit format '{emit}' (valid: ir | zbc)");
                return 1;
        }

        return 0;
    }

    /// Walk up from the source file's directory to find artifacts/z42/libs/ and load dependencies.
    public static DependencyIndex LocateDepIndex(string sourceFullPath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFullPath) ?? ".");
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "z42", "libs");
            if (Directory.Exists(candidate))
                return PackageCompiler.BuildDepIndex([candidate]);
            dir = dir.Parent;
        }
        return DependencyIndex.Empty;
    }

    /// Load stdlib TSIG for reference compilation (single-file mode).
    /// Makes all stdlib classes (Console, Assert, Math, ...) visible at compile time
    /// without requiring `using` declarations — matching the project build behavior.
    public static ImportedSymbols? LocateImportedSymbols(string sourceFullPath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(sourceFullPath) ?? ".");
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "z42", "libs");
            if (Directory.Exists(candidate))
            {
                var cache = new TsigCache();
                foreach (var zpkgPath in Directory.EnumerateFiles(candidate, "*.zpkg"))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(zpkgPath);
                        var meta  = ZpkgReader.ReadMeta(bytes);
                        if (meta.Kind != ZpkgKind.Lib) continue;
                        foreach (var ns in meta.Namespaces)
                            cache.RegisterNamespace(ns, zpkgPath);
                    }
                    catch { /* skip malformed */ }
                }
                var modules = cache.LoadAll();
                if (modules.Count == 0) return null;
                var allNs = modules.Select(m => m.Namespace).Distinct().ToList();
                return ImportedSymbolLoader.Load(modules, allNs);
            }
            dir = dir.Parent;
        }
        return null;
    }

    public static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, content);
        Console.Error.WriteLine($"wrote → {path}");
    }

    static string Sha256Hex(string text) => CompilerUtils.Sha256Hex(text);
}
