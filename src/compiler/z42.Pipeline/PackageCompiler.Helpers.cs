using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Project;
using Z42.Semantics.TypeCheck;

namespace Z42.Pipeline;

/// 单文件编译 / check + manifest 加载 / 源文件解析 / hash / using 提取 /
/// DepIndex 构建。所有跨阶段共享的工具方法，与 BuildTarget 的主流程分离。
public static partial class PackageCompiler
{
    // ── Per-file helpers ──────────────────────────────────────────────────────

    /// 编译单个源文件，使用调用方提供的合并后 imported（外部 zpkg + 同包内
    /// intraSymbols）。`source` 由调用方从磁盘预读，避免重复 I/O。
    static CompiledUnit? CompileFile(
        string           sourceFile,
        DependencyIndex  depIndex,
        string           source,
        ImportedSymbols? imported)
    {
        var result = PipelineCore.Compile(source, sourceFile, depIndex, imported: imported);
        result.Diags.PrintAll();
        if (result.Diags.HasErrors || result.Module is null) return null;

        string ns         = result.Namespace ?? "main";
        string sourceHash = Sha256Hex(source);
        var    exports    = result.Module.Functions.Select(f => f.Name).ToList();
        return new CompiledUnit(sourceFile, ns, sourceHash, exports, result.Module,
            result.Usings.ToList(), result.UsedDepNamespaces.ToList(), result.ExportedTypes);
    }

    static bool CheckFile(string sourceFile)
    {
        string source;
        try   { source = File.ReadAllText(sourceFile); }
        catch { Console.Error.WriteLine($"error: cannot read {sourceFile}"); return false; }

        var result = PipelineCore.Compile(source, sourceFile, DependencyIndex.Empty);
        result.Diags.PrintAll();
        return !result.Diags.HasErrors;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    static bool TryLoadManifest(
        string? explicitToml,
        out string tomlPath,
        out ProjectManifest manifest)
    {
        try
        {
            tomlPath = ProjectManifest.Discover(Directory.GetCurrentDirectory(), explicitToml);
            manifest = ProjectManifest.Load(tomlPath);
            return true;
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(ex.Message);
            tomlPath = "";
            manifest = null!;
            return false;
        }
    }

    static bool TryResolveFiles(
        ProjectManifest manifest,
        string projectDir,
        ExeTarget? target,
        out IReadOnlyList<string> files)
    {
        try
        {
            files = target is null
                ? manifest.ResolveSourceFiles(projectDir)
                : manifest.ResolveSourceFiles(projectDir, target);
            return true;
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine(ex.Message);
            files = [];
            return false;
        }
    }

    static string Sha256Hex(string text) => CompilerUtils.Sha256Hex(text);

    /// Lightweight extraction of `using Ns.Name;` declarations from source text.
    /// Avoids full lex/parse — just scans line-by-line for `using` statements.
    public static List<string> ExtractUsingsPublic(string source) => ExtractUsings(source);

    static List<string> ExtractUsings(string source)
    {
        var result = new List<string>();
        foreach (var rawLine in source.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("using ") && line.EndsWith(";"))
            {
                var ns = line[6..^1].Trim();
                if (ns.Length > 0 && !ns.Contains(' '))
                    result.Add(ns);
            }
        }
        return result;
    }

    /// Load lib-kind zpkgs from the given directories and build a DependencyIndex
    /// from their packed modules. Silently skips malformed or non-lib packages.
    /// When `declaredDeps` has entries, only declared packages + stdlib are loaded.
    public static DependencyIndex BuildDepIndex(
        string[] libsDirs, DependencySection? declaredDeps = null)
    {
        var allowedPkgs = declaredDeps is { IsDeclared: true }
            ? declaredDeps.Entries.Select(d => d.Name).ToHashSet(StringComparer.Ordinal)
            : null;

        var modules = new List<(IrModule Module, string Namespace)>();
        foreach (var dir in libsDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var zpkgPath in Directory.EnumerateFiles(dir, "*.zpkg"))
            {
                try
                {
                    var bytes = File.ReadAllBytes(zpkgPath);
                    var meta  = ZpkgReader.ReadMeta(bytes);
                    if (meta.Kind != ZpkgKind.Lib) continue;
                    bool isStdlib = meta.Name.StartsWith("z42.", StringComparison.Ordinal);
                    if (allowedPkgs != null && !isStdlib && !allowedPkgs.Contains(meta.Name))
                        continue;
                    foreach (var (mod, ns) in ZpkgReader.ReadModules(bytes))
                        modules.Add((mod, ns));
                }
                catch { /* skip malformed */ }
            }
        }
        return DependencyIndex.Build(modules);
    }
}
