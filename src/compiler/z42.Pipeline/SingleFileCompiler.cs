using Z42.Core;
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

        // strict-using-resolution (2026-04-28): load stdlib TSIG with prelude
        // (z42.core) auto-activated; user `using <ns>;` activates other packages.
        // Types from non-activated packages are not visible — TypeChecker reports
        // E0401 (UnknownIdentifier) when used.
        var imported = LocateImportedSymbols(source.FullName, cu.Usings);

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
    /// strict-using-resolution (2026-04-28): only types from prelude packages
    /// (z42.core) + packages activated by user `using` declarations are visible.
    public static ImportedSymbols? LocateImportedSymbols(
        string sourceFullPath,
        IReadOnlyList<string> userUsings)
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
                        {
                            cache.RegisterNamespace(ns, zpkgPath);
                            // strict-using-resolution: W0603 reserved-prefix warning.
                            if (!PreludePackages.IsStdlibPackage(meta.Name)
                                && PreludePackages.IsReservedNamespace(ns))
                            {
                                Console.Error.WriteLine(
                                    $"warning W0603: package `{meta.Name}` declares reserved namespace `{ns}`; " +
                                    $"`Std` / `Std.*` is reserved for stdlib");
                            }
                        }
                    }
                    catch { /* skip malformed */ }
                }
                // 计算激活包：prelude ∪ (user using → namespace → packages)
                var activatedPkgs = new HashSet<string>(PreludePackages.Names, StringComparer.Ordinal);
                foreach (var ns in userUsings)
                    foreach (var pkg in cache.PackagesProvidingNamespace(ns))
                        activatedPkgs.Add(pkg);

                var (modules, packageOf) = cache.LoadForPackages(activatedPkgs);
                if (modules.Count == 0) return null;
                return ImportedSymbolLoader.Load(modules, packageOf, activatedPkgs,
                    preludePackages: PreludePackages.Names);
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
