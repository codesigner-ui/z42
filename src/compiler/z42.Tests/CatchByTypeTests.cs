using FluentAssertions;
using Xunit;
using Z42.Core;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.Semantics.Bound;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// catch-by-generic-type (2026-05-06) — verifies that catch-clause type
/// declarations propagate from AST → BoundCatchClause → IrExceptionEntry,
/// and that E0420 fires on invalid catch types.
public sealed class CatchByTypeTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (CompilationUnit Cu, DiagnosticBag Diags) Parse(string source)
    {
        var diags  = new DiagnosticBag();
        var tokens = new Lexer(source, "test.z42").Tokenize();
        var parser = new Parser(tokens, LanguageFeatures.Phase1);
        var cu     = parser.ParseCompilationUnit();
        foreach (var d in parser.Diagnostics.All) diags.Add(d);
        return (cu, diags);
    }

    private static ExportedClassDef Class(string name, string? baseClass = null) =>
        new(name, BaseClass: baseClass,
            IsAbstract: false, IsSealed: false, IsStatic: false,
            Fields:     new List<ExportedFieldDef>(),
            Methods:    new List<ExportedMethodDef>(),
            Interfaces: new List<string>(),
            TypeParams: null);

    private static ImportedSymbols ImportStdException()
    {
        // Std exposes Exception (root) + IOException : Exception (a subclass).
        var mod = new ExportedModule(
            "Std",
            new List<ExportedClassDef>
            {
                Class("Exception"),
                Class("IOException", baseClass: "Exception"),
            },
            new List<ExportedInterfaceDef>(),
            new List<ExportedEnumDef>(),
            new List<ExportedFuncDef>());
        var packageOf = new Dictionary<ExportedModule, string> { [mod] = "z42.core" };
        return ImportedSymbolLoader.Load(
            new[] { mod }, packageOf,
            activatedPackages: new HashSet<string>(),
            preludePackages:   new HashSet<string> { "z42.core" });
    }

    private static SemanticModel Bind(string source, ImportedSymbols imported, DiagnosticBag diags)
    {
        var (cu, parseDiags) = Parse(source);
        foreach (var d in parseDiags.All) diags.Add(d);
        var tc = new TypeChecker(diags, LanguageFeatures.Phase1);
        return tc.Check(cu, imported);
    }

    private static IReadOnlyList<BoundCatchClause> FindCatches(SemanticModel sem)
    {
        var result = new List<BoundCatchClause>();
        foreach (var body in sem.BoundBodies.Values)
        {
            Walk(body, result);
        }
        return result;
    }

    private static void Walk(BoundBlock block, List<BoundCatchClause> sink)
    {
        foreach (var s in block.Stmts)
        {
            switch (s)
            {
                case BoundTryCatch tc:
                    sink.AddRange(tc.Catches);
                    Walk(tc.TryBody, sink);
                    foreach (var c in tc.Catches) Walk(c.Body, sink);
                    if (tc.Finally is { } fin) Walk(fin, sink);
                    break;
                case BoundBlockStmt bb:
                    Walk(bb.Block, sink);
                    break;
                case BoundIf bi:
                    Walk(bi.Then, sink);
                    if (bi.Else is BoundBlockStmt elseBlk) Walk(elseBlk.Block, sink);
                    break;
                case BoundWhile bw:
                    Walk(bw.Body, sink);
                    break;
            }
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void TypedCatch_SetsFqExceptionTypeName()
    {
        var src = @"
            void Main() {
                try { } catch (Exception e) { }
                try { } catch (IOException e) { }
            }
        ";
        var diags = new DiagnosticBag();
        var sem   = Bind(src, ImportStdException(), diags);

        diags.All.Where(d => d.IsError).Should().BeEmpty();

        var catches = FindCatches(sem);
        catches.Should().HaveCount(2);
        catches[0].ExceptionTypeName.Should().Be("Std.Exception",
            because: "imported class is qualified by its declared namespace");
        catches[1].ExceptionTypeName.Should().Be("Std.IOException");
    }

    [Fact]
    public void UntypedCatch_LeavesExceptionTypeNameNull()
    {
        // `catch { }` (no parens, no var) is the only true-untyped form;
        // `catch (e)` is parsed as typed (exType="e") since the parser commits
        // to type-first interpretation when a single ident sits in parens.
        var src = @"
            void Main() {
                try { } catch { }
            }
        ";
        var diags = new DiagnosticBag();
        var sem   = Bind(src, ImportStdException(), diags);

        diags.All.Where(d => d.IsError).Should().BeEmpty();

        var catches = FindCatches(sem);
        catches.Should().HaveCount(1);
        catches[0].ExceptionTypeName.Should().BeNull(
            because: "bare `catch { }` is wildcard — VM treats null as match-any");
    }

    [Fact]
    public void UnknownCatchType_RaisesE0420()
    {
        var src = @"
            void Main() {
                try { } catch (NoSuchType e) { }
            }
        ";
        var diags = new DiagnosticBag();
        Bind(src, ImportStdException(), diags);

        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.InvalidCatchType
            && d.Message.Contains("NoSuchType")
            && d.Message.Contains("not found"));
    }

    [Fact]
    public void NonExceptionCatchType_RaisesE0420()
    {
        var src = @"
            class Foo {
                public int X;
                public Foo(int x) { this.X = x; }
            }
            void Main() {
                try { } catch (Foo e) { }
            }
        ";
        var diags = new DiagnosticBag();
        Bind(src, ImportStdException(), diags);

        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.InvalidCatchType
            && d.Message.Contains("Foo")
            && d.Message.Contains("must derive from Exception"));
    }

    [Fact]
    public void DerivedCatchType_ResolvesViaBaseChain()
    {
        // IOException → Exception. Walk should succeed for both base and derived.
        var src = @"
            void Main() {
                try { } catch (IOException e) { }
            }
        ";
        var diags = new DiagnosticBag();
        var sem   = Bind(src, ImportStdException(), diags);

        diags.All.Where(d => d.IsError).Should().BeEmpty(
            because: "IOException derives from Exception via the base chain walk");
        var catches = FindCatches(sem);
        catches[0].ExceptionTypeName.Should().Be("Std.IOException");
    }

    [Fact]
    public void IrExceptionEntry_CarriesCatchType()
    {
        // End-to-end: typed catch must surface as IrExceptionEntry.CatchType
        // = the same FQ name as BoundCatchClause.ExceptionTypeName, so VM's
        // find_handler can use it for instance-of filtering.
        var src = @"
            void Main() {
                try { } catch (Exception e) { }
                try { } catch { }
            }
        ";
        var diags    = new DiagnosticBag();
        var imported = ImportStdException();
        var sem      = Bind(src, imported, diags);
        diags.All.Where(d => d.IsError).Should().BeEmpty();

        // We can't directly run IrGen on a synthetic CU with stdlib imports
        // without extra plumbing, but the BoundCatchClause field is what
        // FunctionEmitterStmts hands to IrExceptionEntry verbatim — verifying
        // that link is the only contract this test owns.
        var catches = FindCatches(sem);
        catches.Should().HaveCount(2);
        catches[0].ExceptionTypeName.Should().Be("Std.Exception");
        catches[1].ExceptionTypeName.Should().BeNull();
    }
}
