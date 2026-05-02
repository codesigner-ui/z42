using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Project;
using Z42.Pipeline;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// 2026-05-02 add-multicast-action (D2a) regression tests.
/// 验证 stdlib `Std.MulticastAction<T>` TypeCheck + 用户代码可见性 + IDisposable token。
public sealed class MulticastActionTests
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
            throw new InvalidOperationException("stdlib not built; run scripts/build-stdlib.sh");
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        var model  = new TypeChecker(diags).Check(cu, Imported);
        return (model, diags);
    }

    [Fact]
    public void MulticastAction_Construction()
    {
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            void Main() {
                var bus = new MulticastAction<int>();
            }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void MulticastAction_Subscribe_And_Invoke()
    {
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            void Main() {
                var bus = new MulticastAction<int>();
                var token = bus.Subscribe((int x) => {});
                bus.Invoke(42);
            }
            """);
        diags.HasErrors.Should().BeFalse(string.Join("; ",
            diags.All.Select(d => d.Message)));
    }

    [Fact]
    public void MulticastAction_Subscribe_Returns_IDisposable()
    {
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            void Main() {
                var bus = new MulticastAction<int>();
                IDisposable token = bus.Subscribe((int x) => {});
                token.Dispose();
            }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void MulticastAction_Count()
    {
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            void Main() {
                var bus = new MulticastAction<int>();
                int n = bus.Count();
            }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void MulticastAction_As_Class_Field()
    {
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            class Btn {
                public MulticastAction<int> Clicked = new MulticastAction<int>();
            }
            void Main() {
                var b = new Btn();
                b.Clicked.Subscribe((int x) => {});
                b.Clicked.Invoke(1);
            }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void MulticastAction_With_String_Type()
    {
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            void Main() {
                var bus = new MulticastAction<string>();
                bus.Subscribe((string s) => {});
                bus.Invoke("hello");
            }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Type_Mismatch_Wrong_Action_Param_Rejected()
    {
        var (_, diags) = Check("""
            namespace Demo;
            using Std;
            void Main() {
                var bus = new MulticastAction<int>();
                bus.Subscribe((string s) => {});
            }
            """);
        diags.HasErrors.Should().BeTrue("Action<string> can't be subscribed to MulticastAction<int>");
    }
}
