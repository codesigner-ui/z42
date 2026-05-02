using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// 2026-05-02 impl-closure-l3-escape-stack regression tests.
/// 验证 ClosureEscapeAnalyzer 把"capturing closure 仅作 callee 立即调用"
/// 模式标记为 stack-allocatable，其他情形保守 fallback heap。
public sealed class ClosureEscapeAnalyzerTests
{
    private static (SemanticModel model, DiagnosticBag diags) Check(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        var model  = new TypeChecker(diags).Check(cu);
        return (model, diags);
    }

    [Fact]
    public void Local_Use_Marked_Stack()
    {
        var (model, diags) = Check("""
            namespace Demo;
            void Main() {
                int n = 5;
                var add = (int x) => x + n;
                var r = add(3);
            }
            """);
        diags.HasErrors.Should().BeFalse();
        model.StackAllocClosures.Count.Should().Be(1, "the only closure should be stack-allocated");
    }

    [Fact]
    public void Returned_Closure_Marked_Heap()
    {
        var (model, diags) = Check("""
            namespace Demo;
            (int) -> int Make(int n) {
                var add = (int x) => x + n;
                return add;
            }
            void Main() {}
            """);
        diags.HasErrors.Should().BeFalse();
        model.StackAllocClosures.Should().BeEmpty(
            "return statement causes the closure to escape → heap path");
    }

    [Fact]
    public void Closure_Passed_As_Arg_Marked_Heap()
    {
        var (model, diags) = Check("""
            namespace Demo;
            void Run((int) -> int f) { var r = f(1); }
            void Main() {
                int n = 5;
                var add = (int x) => x + n;
                Run(add);
            }
            """);
        diags.HasErrors.Should().BeFalse();
        model.StackAllocClosures.Should().BeEmpty(
            "passing closure as argument is conservative escape (no [NoEscape] yet)");
    }

    [Fact]
    public void Reassigned_Closure_Var_Marked_Heap()
    {
        var (model, diags) = Check("""
            namespace Demo;
            void Main() {
                int n = 5;
                var add = (int x) => x + n;
                add = (int x) => x;
                var r = add(3);
            }
            """);
        diags.HasErrors.Should().BeFalse();
        model.StackAllocClosures.Should().BeEmpty(
            "reassignment kills stack-alloc safety; both lambdas must be heap");
    }

    [Fact]
    public void Aliased_Closure_Var_Marked_Heap()
    {
        // 把 closure var 赋给另一个 var → 第一个 var 的"用途"出现在
        // VarDecl init 位置（非 callee），分析器视作 escape。
        var (model, diags) = Check("""
            namespace Demo;
            void Main() {
                int n = 5;
                var add = (int x) => x + n;
                var same = add;
                var r = same(3);
            }
            """);
        diags.HasErrors.Should().BeFalse();
        model.StackAllocClosures.Should().BeEmpty(
            "aliasing closure to another var is escape (closure value flows out)");
    }

    [Fact]
    public void NoCapture_Lambda_Not_StackAlloc()
    {
        var (model, diags) = Check("""
            namespace Demo;
            void Main() {
                var sq = (int x) => x * x;
                var r = sq(5);
            }
            """);
        diags.HasErrors.Should().BeFalse();
        model.StackAllocClosures.Should().BeEmpty(
            "no-capture lambda goes through LoadFn, not MkClos — stack-alloc not applicable");
    }
}
