using FluentAssertions;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// TypeChecker unit tests for L2 local function (impl-local-fn-l2).
/// Covers Requirements LF-3 (forward ref / recursion / duplicate),
/// LF-4 (visibility / shadow), LF-5 (no-capture), LF-6 (one-level nesting).
public sealed class LocalFunctionTypeCheckTests
{
    private static (SemanticModel model, DiagnosticBag diags) Check(string src)
    {
        var tokens = new Lexer(src).Tokenize();
        var cu     = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var diags  = new DiagnosticBag();
        var model  = new TypeChecker(diags, LanguageFeatures.Phase1).Check(cu);
        return (model, diags);
    }

    // ── LF-3: forward reference / recursion / duplicate ──────────────────────

    [Fact]
    public void DirectRecursion_Allowed()
    {
        var (_, diags) = Check("""
            int Outer() {
                int Fact(int n) => n <= 1 ? 1 : n * Fact(n - 1);
                return Fact(5);
            }
            void Main() { var x = Outer(); }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void ForwardReference_Allowed()
    {
        // CallFwd is declared first but calls Helper declared later.
        var (_, diags) = Check("""
            int Outer(int x) {
                int CallFwd() => Helper(x);
                int Helper(int v) => v * 2;
                return CallFwd();
            }
            void Main() { var x = Outer(3); }
            """);
        // Note: `Helper(x)` captures `x` so this triggers Z0301 (capture not allowed
        // in L2). But the *forward reference* itself should not produce a "name not
        // found" error — only the capture error should fire.
        diags.All.Where(d => d.Code == DiagnosticCodes.UndefinedSymbol)
             .Should().BeEmpty();
    }

    [Fact]
    public void DuplicateLocalFn_Reported()
    {
        var (_, diags) = Check("""
            int Outer() {
                int Helper(int x) => x * 2;
                int Helper(int x) => x * 3;
                return Helper(0);
            }
            void Main() { var x = Outer(); }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.DuplicateDeclaration
            && d.Message.Contains("Helper"));
    }

    // ── LF-4: visibility ──────────────────────────────────────────────────────

    [Fact]
    public void LocalFn_NotVisibleOutside()
    {
        var (_, diags) = Check("""
            int Outer() {
                int Helper(int x) => x * 2;
                return Helper(3);
            }
            void Main() {
                var y = Helper(5);   // Helper is local to Outer; not visible here
            }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.UndefinedSymbol
            && d.Message.Contains("Helper"));
    }

    // ── LF-5: no-capture ──────────────────────────────────────────────────────

    [Fact]
    public void LocalFn_RejectsCaptureOfOuterLocal()
    {
        var (_, diags) = Check("""
            int Outer() {
                var k = 10;
                int Helper(int x) => x + k;   // captures k
                return Helper(3);
            }
            void Main() { var x = Outer(); }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.FeatureDisabled
            && d.Message.Contains("capture")
            && d.Message.Contains("k"));
    }

    [Fact]
    public void LocalFn_AllowsParamReference()
    {
        var (_, diags) = Check("""
            int Outer() {
                int Helper(int x) => x * 2;
                return Helper(3);
            }
            void Main() { var x = Outer(); }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void LocalFn_ParamShadowsOuter()
    {
        // The local fn's `n` parameter shadows the outer `n` parameter.
        // This must NOT trigger a capture error.
        var (_, diags) = Check("""
            int Outer(int n) {
                int Inner(int n) => n * 2;
                return Inner(n);
            }
            void Main() { var x = Outer(5); }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void LocalFn_CallsSiblingLocalFn_Allowed()
    {
        var (_, diags) = Check("""
            int Outer() {
                int Double(int a) => a * 2;
                int UseDouble(int b) => Double(b) + 1;
                return UseDouble(5);
            }
            void Main() { var x = Outer(); }
            """);
        diags.HasErrors.Should().BeFalse();
    }

    // ── LF-6: one-level nesting limit ─────────────────────────────────────────

    [Fact]
    public void NestedLocalFn_Reported()
    {
        // local fn inside local fn → L2 error
        var (_, diags) = Check("""
            int Outer() {
                int Helper(int x) {
                    int Inner(int y) => y + 1;
                    return Inner(x);
                }
                return Helper(3);
            }
            void Main() { var x = Outer(); }
            """);
        diags.HasErrors.Should().BeTrue();
        diags.All.Should().Contain(d =>
            d.Code == DiagnosticCodes.FeatureDisabled
            && d.Message.Contains("nested local function"));
    }
}
