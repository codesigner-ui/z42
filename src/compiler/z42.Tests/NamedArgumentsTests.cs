using FluentAssertions;
using Xunit;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;
using Z42.Semantics;

namespace Z42.Tests;

/// <summary>
/// Coverage for spec add-named-arguments (2026-05-11). Parser-level naming
/// recognition + TypeChecker reorder for the two wired call paths
/// (static class methods + constructors). Imported / other paths emit
/// Z1002 fallback — covered by FallbackForUnknownParamNames cases.
/// </summary>
public sealed class NamedArgumentsTests
{
    private static (CompilationUnit cu, DiagnosticBag diags) ParseSource(string source)
    {
        var tokens = new Lexer(source).Tokenize();
        var diags  = new DiagnosticBag();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        return (cu, diags);
    }

    private static (CompilationUnit cu, DiagnosticBag diags) Compile(string source)
    {
        var (cu, parseDiags) = ParseSource(source);
        var tc = new TypeChecker(parseDiags);
        tc.Check(cu);
        return (cu, parseDiags);
    }

    // ── Parser-level shape (basic naming) ───────────────────────────────────

    [Fact]
    public void Parse_SimpleNamedArg_ProducesArgumentWithName()
    {
        var (cu, _) = ParseSource("void Main() { Greet(name: \"Alice\"); }");
        var call = (CallExpr)((ExprStmt)cu.Functions[0].Body!.Stmts[0]).Expr;
        call.Args.Should().HaveCount(1);
        call.Args[0].Name.Should().Be("name");
        call.Args[0].Value.Should().BeOfType<LitStrExpr>();
    }

    [Fact]
    public void Parse_NamedArgWithRef_ProducesArgWithModifier()
    {
        var (cu, _) = ParseSource("void Main() { Update(target: ref x); }");
        var call = (CallExpr)((ExprStmt)cu.Functions[0].Body!.Stmts[0]).Expr;
        call.Args[0].Name.Should().Be("target");
        call.Args[0].Value.Should().BeOfType<ModifiedArg>()
            .Which.Modifier.Should().Be(ArgModifier.Ref);
    }

    [Fact]
    public void Parse_TernaryInPositionalArg_NotConfusedWithNamed()
    {
        var (cu, _) = ParseSource("void Main() { f(a ? b : c); }");
        var call = (CallExpr)((ExprStmt)cu.Functions[0].Body!.Stmts[0]).Expr;
        call.Args[0].Name.Should().BeNull();
        call.Args[0].Value.Should().BeOfType<ConditionalExpr>();
    }

    // ── Z1001 positional-after-named ────────────────────────────────────────

    [Fact]
    public void TypeCheck_PositionalAfterNamed_EmitsZ1001()
    {
        var (_, diags) = Compile("""
            void Greet(string name, int n) { }
            void Main() { Greet(name: "Alice", 1); }
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.PositionalAfterNamed);
    }

    // ── Z1002 unknown name ──────────────────────────────────────────────────

    [Fact]
    public void TypeCheck_UnknownArgumentName_OnStaticCall_EmitsZ1002()
    {
        var (_, diags) = Compile("""
            class Tool {
                public static void Run(int width) { }
            }
            void Main() { Tool.Run(unknownName: 5); }
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.UnknownArgumentName);
    }

    // ── Z1003 duplicate name ────────────────────────────────────────────────

    [Fact]
    public void TypeCheck_DuplicateArgumentName_EmitsZ1003()
    {
        var (_, diags) = Compile("""
            class Tool {
                public static void Draw(string color, int width) { }
            }
            void Main() { Tool.Draw(color: "red", color: "blue"); }
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.DuplicateArgumentName);
    }

    // ── Z1004 doubly specified ──────────────────────────────────────────────

    [Fact]
    public void TypeCheck_ParameterDoublySpecified_EmitsZ1004()
    {
        var (_, diags) = Compile("""
            class Tool {
                public static void Draw(string color, int width = 1) { }
            }
            void Main() { Tool.Draw("red", color: "blue"); }
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.ParameterDoublySpecified);
    }

    // ── Z1005 missing required ──────────────────────────────────────────────

    [Fact]
    public void TypeCheck_MissingRequired_AfterNamed_EmitsZ1005()
    {
        var (_, diags) = Compile("""
            class Tool {
                public static void Draw(string color, int width) { }
            }
            void Main() { Tool.Draw(width: 2); }
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.MissingRequiredArgument);
    }

    // ── Reorder works: out-of-order named ───────────────────────────────────

    [Fact]
    public void TypeCheck_OutOfOrderNamedOnStaticCall_BindsClean()
    {
        var (_, diags) = Compile("""
            class Tool {
                public static void Draw(string color, int width, bool filled) { }
            }
            void Main() { Tool.Draw(filled: true, color: "red", width: 2); }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    // ── Skip middle default with named ──────────────────────────────────────

    [Fact]
    public void TypeCheck_SkipMiddleDefault_WithNamedTail_BindsClean()
    {
        var (_, diags) = Compile("""
            class Tool {
                public static int M(int a, int b = 10, int c = 20) { return a + b + c; }
            }
            void Main() { var r = Tool.M(1, c: 30); }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    // ── Constructor named args ──────────────────────────────────────────────

    [Fact]
    public void TypeCheck_ConstructorNamedArgs_BindsClean()
    {
        var (_, diags) = Compile("""
            class Box {
                public int x;
                public int y;
                public Box(int width, int height) { x = width; y = height; }
            }
            void Main() { var b = new Box(height: 3, width: 5); }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void TypeCheck_ConstructorUnknownName_EmitsZ1002()
    {
        var (_, diags) = Compile("""
            class Box {
                public Box(int width, int height) { }
            }
            void Main() { var b = new Box(width: 5, depth: 3); }
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.UnknownArgumentName);
    }

    // ── Instance method on user class (Z42ClassType path) ───────────────────

    [Fact]
    public void TypeCheck_InstanceMethodNamedArgs_BindsClean()
    {
        var (_, diags) = Compile("""
            class Painter {
                public void Draw(string color, int width, bool filled) { }
            }
            void Main() {
                var p = new Painter();
                p.Draw(filled: true, color: "red", width: 2);
            }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void TypeCheck_InstanceMethodUnknownName_EmitsZ1002()
    {
        var (_, diags) = Compile("""
            class Painter {
                public void Draw(string color) { }
            }
            void Main() {
                var p = new Painter();
                p.Draw(badName: "red");
            }
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.UnknownArgumentName);
    }

    // ── Top-level free function (env.LookupFunc path via SymbolTable.FuncDecls) ──

    [Fact]
    public void TypeCheck_FreeFunctionOutOfOrderNamed_BindsClean()
    {
        var (_, diags) = Compile("""
            void Greet(string name, int times) { }
            void Main() { Greet(times: 3, name: "Alice"); }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void TypeCheck_FreeFunctionUnknownName_EmitsZ1002()
    {
        var (_, diags) = Compile("""
            void Greet(string name) { }
            void Main() { Greet(badName: "Alice"); }
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.UnknownArgumentName);
    }

    [Fact]
    public void TypeCheck_FreeFunctionSkipMiddleDefault_BindsClean()
    {
        var (_, diags) = Compile("""
            int Add(int a, int b = 10, int c = 20) { return a + b + c; }
            void Main() { var r = Add(1, c: 30); }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    // ── Nested local function (spec extend-named-args-shim) ─────────────────

    [Fact]
    public void TypeCheck_NestedLocalFunctionNamedArgs_BindsClean()
    {
        var (_, diags) = Compile("""
            void Main() {
                int Mix(int a, int b, int c) { return a + b * c; }
                var r = Mix(c: 3, a: 1, b: 2);
            }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void TypeCheck_NestedLocalFunctionUnknownName_EmitsZ1002()
    {
        var (_, diags) = Compile("""
            void Main() {
                int Mix(int a, int b) { return a + b; }
                var r = Mix(badName: 3);
            }
            """);
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.UnknownArgumentName);
    }

    // ── Imported (cross-CU) callables (spec extend-named-args-shim) ─────────

    private static (CompilationUnit cu, DiagnosticBag diags) CompileWithImports(
        string source, ImportedSymbols imports)
    {
        var tokens = new Lexer(source).Tokenize();
        var diags  = new DiagnosticBag();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var tc     = new TypeChecker(diags, LanguageFeatures.Phase1);
        tc.Check(cu, imports);
        return (cu, diags);
    }

    private static ImportedSymbols MakeImports(
        IReadOnlyList<ExportedClassDef>?    classes = null,
        IReadOnlyList<ExportedFuncDef>?     funcs   = null)
    {
        var mod = new ExportedModule(
            "Foo",
            classes is null ? new List<ExportedClassDef>()    : classes.ToList(),
            new List<ExportedInterfaceDef>(),
            new List<ExportedEnumDef>(),
            funcs is null   ? new List<ExportedFuncDef>()     : funcs.ToList());
        var packageOf = new Dictionary<ExportedModule, string> { [mod] = "foo.pkg" };
        return ImportedSymbolLoader.Load(
            new[] { mod }, packageOf,
            activatedPackages: new HashSet<string>(),
            preludePackages:   new HashSet<string> { "foo.pkg" });
    }

    [Fact]
    public void TypeCheck_ImportedClassMethodNamedArgs_BindsClean()
    {
        var cls = new ExportedClassDef(
            Name:       "Tool",
            BaseClass:  null,
            IsAbstract: false,
            IsSealed:   false,
            IsStatic:   false,
            Fields:     new List<ExportedFieldDef>(),
            Methods:    new List<ExportedMethodDef>
            {
                new ExportedMethodDef(
                    Name: "Draw", ReturnType: "void", Visibility: "public",
                    IsStatic: true, IsVirtual: false, IsAbstract: false,
                    MinArgCount: 3,
                    Params: new List<ExportedParamDef>
                    {
                        new("color", "string"),
                        new("width", "int"),
                        new("filled", "bool"),
                    })
            },
            Interfaces: new List<string>(),
            TypeParams: null);
        var imports = MakeImports(classes: new[] { cls });
        var (_, diags) = CompileWithImports(
            """
            void Main() { Tool.Draw(filled: true, color: "red", width: 2); }
            """, imports);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void TypeCheck_ImportedClassMethodUnknownName_EmitsZ1002()
    {
        var cls = new ExportedClassDef(
            Name:       "Tool",
            BaseClass:  null,
            IsAbstract: false,
            IsSealed:   false,
            IsStatic:   false,
            Fields:     new List<ExportedFieldDef>(),
            Methods:    new List<ExportedMethodDef>
            {
                new ExportedMethodDef(
                    Name: "Draw", ReturnType: "void", Visibility: "public",
                    IsStatic: true, IsVirtual: false, IsAbstract: false,
                    MinArgCount: 1,
                    Params: new List<ExportedParamDef> { new("color", "string") })
            },
            Interfaces: new List<string>(),
            TypeParams: null);
        var imports = MakeImports(classes: new[] { cls });
        var (_, diags) = CompileWithImports(
            """
            void Main() { Tool.Draw(badName: "red"); }
            """, imports);
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.UnknownArgumentName);
    }

    [Fact]
    public void TypeCheck_ImportedFreeFunctionNamedArgs_BindsClean()
    {
        var fn = new ExportedFuncDef(
            Name:        "Mix",
            ReturnType:  "int",
            MinArgCount: 3,
            Params: new List<ExportedParamDef>
            {
                new("a", "int"),
                new("b", "int"),
                new("c", "int"),
            });
        var imports = MakeImports(funcs: new[] { fn });
        var (_, diags) = CompileWithImports(
            """
            void Main() { var r = Mix(c: 3, a: 1, b: 2); }
            """, imports);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }
}
