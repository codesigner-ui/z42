using FluentAssertions;
using Xunit;
using Z42.Core.Diagnostics;
using Z42.Core.Features;
using Z42.IR;
using Z42.Semantics.Bound;
using Z42.Semantics.Codegen;
using Z42.Semantics.TypeCheck;
using Z42.Syntax.Lexer;
using Z42.Syntax.Parser;

namespace Z42.Tests;

/// <summary>
/// Coverage for spec fix-numeric-cast-lowering (2026-05-13).
/// TypeChecker side: E0424 emitted for illegal pairs, accepted for legal.
/// Codegen side: ConvertInstr emitted for cross-type cast, no-op for identity.
/// VM-side semantics covered by VM goldens (`src/tests/casts/` series) and
/// stdlib smoke tests under `z42.math/tests/cast_*`.
/// </summary>
public sealed class NumericCastTests
{
    private static DiagnosticBag CompileTo(string source, out IrModule? ir)
    {
        var diags = new DiagnosticBag();
        var tokens = new Lexer(source).Tokenize();
        var cu = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var sem = new TypeChecker(diags).Check(cu);
        if (diags.All.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            ir = null;
            return diags;
        }
        var gen = new IrGen(semanticModel: sem);
        ir = gen.Generate(cu);
        return diags;
    }

    private static DiagnosticBag TypeCheckOnly(string source)
    {
        var diags = new DiagnosticBag();
        var tokens = new Lexer(source).Tokenize();
        var cu = new Parser(tokens, LanguageFeatures.Phase1).ParseCompilationUnit();
        var tc = new TypeChecker(diags, LanguageFeatures.Phase1);
        tc.Check(cu);
        return diags;
    }

    // ── Illegal cast → E0424 ────────────────────────────────────────────────

    [Fact]
    public void IllegalCast_BoolToInt_EmitsE0424()
    {
        var diags = TypeCheckOnly("""
            void Main() { bool b = true; int n = (int)b; }
            """);
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IllegalCast);
    }

    [Fact]
    public void IllegalCast_IntToBool_EmitsE0424()
    {
        var diags = TypeCheckOnly("""
            void Main() { int n = 1; bool b = (bool)n; }
            """);
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IllegalCast);
    }

    [Fact]
    public void IllegalCast_StringToInt_EmitsE0424()
    {
        var diags = TypeCheckOnly("""
            void Main() { string s = "5"; int n = (int)s; }
            """);
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IllegalCast);
    }

    [Fact]
    public void IllegalCast_IntToString_EmitsE0424()
    {
        var diags = TypeCheckOnly("""
            void Main() { int n = 5; string s = (string)n; }
            """);
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IllegalCast);
    }

    [Fact]
    public void IllegalCast_BoolToString_EmitsE0424()
    {
        var diags = TypeCheckOnly("""
            void Main() { bool b = true; string s = (string)b; }
            """);
        diags.All.Should().Contain(d => d.Code == DiagnosticCodes.IllegalCast);
    }

    // ── Legal cast → no diagnostic ──────────────────────────────────────────

    [Fact]
    public void LegalCast_DoubleToLong_NoDiagnostic()
    {
        var diags = TypeCheckOnly("""
            void Main() { double d = 2.5; long n = (long)d; }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void LegalCast_LongToInt_NoDiagnostic()
    {
        var diags = TypeCheckOnly("""
            void Main() { long x = 100; int n = (int)x; }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void LegalCast_IntToDouble_NoDiagnostic()
    {
        var diags = TypeCheckOnly("""
            void Main() { int x = 5; double d = (double)x; }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void LegalCast_CharToInt_NoDiagnostic()
    {
        var diags = TypeCheckOnly("""
            void Main() { char c = 'A'; int n = (int)c; }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void LegalCast_IntToChar_NoDiagnostic()
    {
        var diags = TypeCheckOnly("""
            void Main() { int n = 65; char c = (char)n; }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void LegalCast_ObjectToLong_AllowedRuntimeChecked()
    {
        // Existing stdlib pattern `(long)object_value` must keep compiling
        // without E0424. VM resolves the actual narrowing at runtime via
        // Value variant inspection.
        var diags = TypeCheckOnly("""
            void Main() { object o = 5; long n = (long)o; }
            """);
        diags.All.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    // ── Codegen — ConvertInstr emission ─────────────────────────────────────

    [Fact]
    public void Codegen_DoubleToLong_EmitsConvertInstr()
    {
        CompileTo("""
            void Main() { double d = 2.5; long n = (long)d; }
            """, out var ir);
        ir.Should().NotBeNull();
        var main = ir!.Functions.FirstOrDefault(f => f.Name.EndsWith(".Main") || f.Name == "Main");
        main.Should().NotBeNull("expected a Main function in {0}",
            string.Join(", ", ir.Functions.Select(f => f.Name)));
        main!.Blocks.SelectMany(b => b.Instructions)
            .OfType<ConvertInstr>()
            .Should().NotBeEmpty("(long)d should lower to ConvertInstr");
    }

    [Fact]
    public void Codegen_IdentityCast_NoConvertInstr()
    {
        CompileTo("""
            void Main() { int x = 5; int y = (int)x; }
            """, out var ir);
        ir.Should().NotBeNull();
        var main = ir!.Functions.FirstOrDefault(f => f.Name.EndsWith(".Main") || f.Name == "Main");
        main.Should().NotBeNull("expected a Main function in {0}",
            string.Join(", ", ir.Functions.Select(f => f.Name)));
        main!.Blocks.SelectMany(b => b.Instructions)
            .OfType<ConvertInstr>()
            .Should().BeEmpty("(int)x where x is already int should stay no-op");
    }

    [Fact]
    public void Codegen_LongToIntNarrowing_EmitsConvertInstr()
    {
        CompileTo("""
            void Main() { long x = 100000000000; int n = (int)x; }
            """, out var ir);
        ir.Should().NotBeNull();
        var main = ir!.Functions.FirstOrDefault(f => f.Name.EndsWith(".Main") || f.Name == "Main");
        main.Should().NotBeNull("expected Main; got {0}",
            string.Join(", ", ir.Functions.Select(f => f.Name)));
        main!.Blocks.SelectMany(b => b.Instructions)
            .OfType<ConvertInstr>()
            .Should().NotBeEmpty();
    }

    [Fact]
    public void Codegen_CharToInt_EmitsConvertInstr()
    {
        CompileTo("""
            void Main() { char c = 'A'; int n = (int)c; }
            """, out var ir);
        ir.Should().NotBeNull();
        var main = ir!.Functions.FirstOrDefault(f => f.Name.EndsWith(".Main") || f.Name == "Main");
        main.Should().NotBeNull("expected Main; got {0}",
            string.Join(", ", ir.Functions.Select(f => f.Name)));
        main!.Blocks.SelectMany(b => b.Instructions)
            .OfType<ConvertInstr>()
            .Should().NotBeEmpty();
    }
}
