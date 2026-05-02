using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.IR.BinaryFormat;
using Z42.Pipeline;
using Z42.Project;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// 2026-05-02 add-generic-delegates (D1c) regression tests.
/// 验证 stdlib 真实 `Action`/`Func`/`Predicate` 类型 + 移除 hardcoded
/// `Action`/`Func` desugar 后的兼容性。
public sealed class StdlibDelegateTests
{
    private static readonly ImportedSymbols? Imported = LoadImported();

    private static ImportedSymbols? LoadImported()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "artifacts", "z42", "libs");
            if (Directory.Exists(candidate))
            {
                var cache = new TsigCache();
                foreach (var zpkg in Directory.EnumerateFiles(candidate, "*.zpkg"))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(zpkg);
                        foreach (var ns in ZpkgReader.ReadNamespaces(bytes))
                            cache.RegisterNamespace(ns, zpkg);
                    }
                    catch { }
                }
                var allPkgs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (_, pkg) in cache.AllPackages()) allPkgs.Add(pkg);
                var (modules, packageOf) = cache.LoadForPackages(allPkgs);
                if (modules.Count == 0) return null;
                return ImportedSymbolLoader.Load(modules, packageOf, allPkgs,
                    preludePackages: Z42.Core.PreludePackages.Names);
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static (SemanticModel model, DiagnosticBag diags) Check(string src)
    {
        if (Imported is null)
            throw new InvalidOperationException("stdlib not built; run scripts/build-stdlib.sh first");
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        var model  = new TypeChecker(diags).Check(cu, Imported);
        return (model, diags);
    }

    [Fact]
    public void Action_Resolves_Via_Stdlib()
    {
        // 0-arity Action — non-generic
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            void Main() {
                Action a = () => {};
                a();
            }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Action_T_Resolves_Via_Stdlib()
    {
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            void Main() {
                Action<int> a = (int x) => {};
                a(42);
            }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Func_T_R_Resolves_Via_Stdlib()
    {
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            void Main() {
                Func<int, int> sq = (int x) => x * x;
                var r = sq(5);
            }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Predicate_T_Resolves_Via_Stdlib()
    {
        // D1c new addition: Predicate<T> previously didn't exist.
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            void Main() {
                Predicate<int> isEven = (int x) => x % 2 == 0;
                var r = isEven(4);
            }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Multiple_Arity_Func_Coexist()
    {
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            void Main() {
                Func<int, int> f1 = (int x) => x;
                Func<int, int, int> f2 = (int a, int b) => a + b;
                Func<int, int, int, int> f3 = (int a, int b, int c) => a + b + c;
            }
            """);
        diags.HasErrors.Should().BeFalse();
    }
}
