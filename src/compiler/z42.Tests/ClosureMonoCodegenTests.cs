using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// 2026-05-02 impl-closure-l3-monomorphize regression tests.
/// Verifies that the alias-tracking layer in TypeChecker collapses
/// `var f = Helper; f();`-shaped patterns to direct `CallInstr`,
/// while bona fide indirect calls still emit `CallIndirectInstr`.
public sealed class ClosureMonoCodegenTests
{
    private static IrModule GenModule(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        var model  = new TypeChecker(diags).Check(cu);
        diags.HasErrors.Should().BeFalse(because:
            "test source must type-check cleanly");
        return new IrGen(semanticModel: model).Generate(cu);
    }

    private static IrFunction FindFn(IrModule m, string nameSuffix) =>
        m.Functions.First(f => f.Name.EndsWith(nameSuffix));

    private static List<IrInstr> All(IrFunction fn) =>
        fn.Blocks.SelectMany(b => b.Instructions).ToList();

    [Fact]
    public void Local_Alias_Collapses_To_Direct_Call()
    {
        var m = GenModule("""
            namespace Demo;
            int Helper() { return 42; }
            void Main() {
                var f = Helper;
                f();
            }
            """);
        var instrs = All(FindFn(m, ".Main"));
        instrs.OfType<CallInstr>().Any(ci => ci.Func == "Demo.Helper")
            .Should().BeTrue("var f = Helper; f(); should emit a direct CallInstr to Demo.Helper");
        instrs.OfType<CallIndirectInstr>().Any()
            .Should().BeFalse("alias-resolved call must not fall back to indirect dispatch");
    }

    [Fact]
    public void Reassigned_Var_Falls_Back_To_Indirect()
    {
        var m = GenModule("""
            namespace Demo;
            int A() { return 1; }
            int B() { return 2; }
            void Main() {
                var f = A;
                f = B;
                f();
            }
            """);
        var instrs = All(FindFn(m, ".Main"));
        instrs.OfType<CallIndirectInstr>().Any()
            .Should().BeTrue("after reassignment alias must be cleared → CallIndirect");
    }

    [Fact]
    public void Function_Param_Stays_Indirect()
    {
        var m = GenModule("""
            namespace Demo;
            int Apply((int) -> int f, int x) { return f(x); }
            int Square(int n) { return n * n; }
            void Main() {
                var r = Apply(Square, 5);
            }
            """);
        var apply = FindFn(m, ".Apply");
        var instrs = All(apply);
        instrs.OfType<CallIndirectInstr>().Any()
            .Should().BeTrue("f comes from a parameter, callee is unknown at compile time");
    }

    [Fact]
    public void Direct_TopLevel_Call_Stays_Direct()
    {
        var m = GenModule("""
            namespace Demo;
            int Helper() { return 42; }
            void Main() {
                Helper();
            }
            """);
        var instrs = All(FindFn(m, ".Main"));
        instrs.OfType<CallInstr>().Any(ci => ci.Func == "Demo.Helper").Should().BeTrue();
        instrs.OfType<CallIndirectInstr>().Any().Should().BeFalse();
    }

    [Fact]
    public void Alias_Chain_Propagates()
    {
        var m = GenModule("""
            namespace Demo;
            int Helper() { return 42; }
            void Main() {
                var f = Helper;
                var g = f;
                g();
            }
            """);
        var instrs = All(FindFn(m, ".Main"));
        instrs.OfType<CallInstr>().Any(ci => ci.Func == "Demo.Helper")
            .Should().BeTrue("alias chain f → Helper → g should still resolve to Demo.Helper");
        instrs.OfType<CallIndirectInstr>().Any().Should().BeFalse();
    }
}
