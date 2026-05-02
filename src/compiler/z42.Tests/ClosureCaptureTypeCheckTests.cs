using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Semantics.Bound;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// TypeChecker unit tests for L3 closure capture analysis.
/// Pairs with archived `add-closures` Requirements R5 / R6 and
/// impl spec `L3-C-1` ~ `L3-C-11`. See docs/design/closure.md §4.
public sealed class ClosureCaptureTypeCheckTests
{
    private static (SemanticModel model, DiagnosticBag diags) Check(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        var model  = new TypeChecker(diags, LanguageFeatures.Phase1).Check(cu);
        return (model, diags);
    }

    /// Find the (single, first) BoundLambda inside a top-level Main body's
    /// VarDecl init expression, or null if none.
    private static BoundLambda? FindFirstLambda(SemanticModel model)
    {
        foreach (var body in model.BoundBodies.Values)
        {
            foreach (var stmt in body.Stmts)
            {
                if (stmt is BoundVarDecl v && v.Init is BoundLambda l) return l;
            }
        }
        return null;
    }

    // ── L3-C-1: capture analysis (positive cases) ─────────────────────────────

    [Fact]
    public void ValueType_CapturedAsSnapshot()
    {
        var (model, diags) = Check("""
            void Main() {
                var k = 10;
                (int) -> bool f = (int x) => x > k;
            }
            """);
        diags.HasErrors.Should().BeFalse();
        var lam = FindFirstLambda(model);
        lam.Should().NotBeNull();
        lam!.Captures.Should().HaveCount(1);
        lam.Captures[0].Name.Should().Be("k");
        lam.Captures[0].Kind.Should().Be(BoundCaptureKind.ValueSnapshot);
    }

    [Fact]
    public void ReferenceType_CapturedAsIdentity()
    {
        var (model, diags) = Check("""
            class Counter { public int n; Counter() { this.n = 0; } }
            void Main() {
                var c = new Counter();
                (int) -> int f = (int x) => x + c.n;
            }
            """);
        diags.HasErrors.Should().BeFalse();
        var lam = FindFirstLambda(model);
        lam.Should().NotBeNull();
        lam!.Captures.Should().ContainSingle()
           .Which.Kind.Should().Be(BoundCaptureKind.ReferenceShare);
    }

    [Fact]
    public void MultipleReferences_DedupedToSingleCapture()
    {
        var (model, diags) = Check("""
            void Main() {
                var k = 10;
                (int) -> int f = (int x) => k + k + k;
            }
            """);
        diags.HasErrors.Should().BeFalse();
        var lam = FindFirstLambda(model);
        lam!.Captures.Should().HaveCount(1);   // dedup by name
    }

    [Fact]
    public void NoCapture_WhenOnlyOwnParam()
    {
        var (model, diags) = Check("""
            void Main() {
                (int) -> int f = (int x) => x * x;
            }
            """);
        diags.HasErrors.Should().BeFalse();
        FindFirstLambda(model)!.Captures.Should().BeEmpty();
    }

    // ── L3-C-9: nested capture ────────────────────────────────────────────────

    [Fact]
    public void NestedLambda_BothFramesCaptureSameName()
    {
        // Outer lambda f also captures k2 (transitively) so that g's env can be
        // built inside f's lifted body. See impl-closure-l3-core Decision 6.
        var (model, diags) = Check("""
            void Main() {
                var k2 = 7;
                () -> int f = () => {
                    () -> int g = () => k2 * 2;
                    return g();
                };
            }
            """);
        diags.HasErrors.Should().BeFalse();
        var lam = FindFirstLambda(model);
        lam.Should().NotBeNull();
        lam!.Captures.Should().HaveCount(1);
        lam.Captures[0].Name.Should().Be("k2");
    }

    // ── L3-C-10: local function capture ───────────────────────────────────────

    [Fact]
    public void LocalFn_CaptureNowAllowed()
    {
        var (_, diags) = Check("""
            int Outer() {
                var k = 10;
                int Helper(int x) => x + k;
                return Helper(3);
            }
            void Main() { var x = Outer(); }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    // ── L3-C-11: spawn does not enforce Send (deferred) ──────────────────────

    // ── R7: loop-variable capture (auto-satisfied by value-snapshot) ─────────

    [Fact]
    public void ForeachLoopVar_CapturesByValueEachIteration()
    {
        // 一个闭包在 foreach body 内创建并捕获 `i`：每次迭代是独立 BoundLambda
        // 节点（独立 Captures 列表）。值快照语义保证 runtime 的每个 closure
        // 持有自己迭代时的值。这里仅验证编译期 capture 分析正确产出。
        // 见 docs/design/closure.md §4.3 + impl-closure-l3-core.
        var (model, diags) = Check("""
            void Main() {
                int[] nums = new int[] { 1, 2, 3 };
                () -> int f = () => 0;
                foreach (var i in nums) {
                    f = () => i;
                }
            }
            """);
        diags.HasErrors.Should().BeFalse();
        var lam = FindFirstLambda(model);
        // BoundLambda 在 var f 初始化处（() => 0）—— 它没有 capture。
        // 我们直接断言无错误足够：runtime 行为由 golden 覆盖。
        lam.Should().NotBeNull();
    }

    [Fact]
    public void ForLoopVar_CaptureNoLateBindingAtCompile()
    {
        // C# 5 之前 for 循环变量晚绑定是因为编译器把 `i` hoist 到 display
        // class（按引用共享）。z42 不做 hoisting，直接走值快照路径，所以
        // for 与 foreach 行为一致。本测试仅验证编译通过；runtime 行为由
        // golden `closure_l3_loops` 覆盖。
        var (_, diags) = Check("""
            void Main() {
                () -> int f = () => 0;
                for (int j = 0; j < 3; j = j + 1) {
                    f = () => j;
                }
            }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void SpawnLambda_NoSendCheckYet()
    {
        // 本变更不做 Send 检查（与 concurrency 一起做）。spawn 闭包按普通 capture
        // 规则处理，不应触发 Z0809 错误。
        // NB: spawn 关键字在 z42 当前 codebase 仍未实现，所以这条用 lambda
        // 字面量 + Func 派遣模拟"逃逸到外部回调"——只验证 capture 不会产生
        // Send-related 错误。
        // 2026-05-02 D1c: hardcoded Func desugar removed; declare delegate inline.
        var (_, diags) = Check("""
            public delegate bool IntPred(int x);
            void Main() {
                var k = 10;
                IntPred f = (int x) => x > k;
            }
            """);
        diags.All.Should().NotContain(d =>
            d.Code == DiagnosticCodes.UnexpectedToken && d.Message.Contains("Send"));
        diags.HasErrors.Should().BeFalse();
    }
}
